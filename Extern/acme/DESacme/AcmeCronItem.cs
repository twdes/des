﻿#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Neo.IronLua;
using TecWare.DE.Server;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE
{
	/// <summary>Manage SSL-Certificates with the ACME protocol.</summary>
	public class AcmeCronItem : CronJobItem
	{
		#region -- enum AcmeState -----------------------------------------------------

		/// <summary>State of the current store.</summary>
		public enum AcmeState
		{
			/// <summary>Certificate is signed and working</summary>
			Installed,
			/// <summary>An order for a new certificate is pending.</summary>
			Pending,
			/// <summary>Certificate is signed.</summary>
			Issued
		} // enum AcmeState

		#endregion

		#region -- class AcmeStateStore -----------------------------------------------

		private class AcmeStateStore
		{
			private readonly Uri acmeUri;
			private readonly string commonName;
			private readonly string fileName;
			
			private string accountKey = null;

			private Uri orderUri = null;
			private string token = null;
			private string keyAuthz = null;

			private byte[] pfxContent = null;

			private AcmeState state = AcmeState.Installed;

			#region -- Ctor -----------------------------------------------------------

			public AcmeStateStore(Uri acmeUri, string commonName, string fileName)
			{
				this.acmeUri = acmeUri ?? throw new ArgumentNullException(nameof(acmeUri));
				this.fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
				this.commonName = commonName ?? throw new ArgumentNullException(nameof(commonName));

				Load();
			} // ctor

			#endregion

			#region -- Load/Save ------------------------------------------------------

			public void Load()
			{
				if (!File.Exists(fileName))
					return;

				var xDoc = XDocument.Load(fileName);

				state = xDoc.Root.GetAttribute("v", AcmeState.Installed);
				accountKey = xDoc.Root.GetNode("account", null);
				switch (state)
				{
					case AcmeState.Pending:
						orderUri = new Uri(xDoc.Root.GetNode("order", null), UriKind.Absolute);
						token = xDoc.Root.GetNode("token", null);
						keyAuthz = xDoc.Root.GetNode("authz", null);
						pfxContent = null;
						break;
					case AcmeState.Installed:
					case AcmeState.Issued:
						var pfxString = xDoc.Root.GetNode("pfx", null);
						pfxContent = pfxString == null ? null : Convert.FromBase64String(pfxString);
						orderUri = null;
						token = null;
						keyAuthz = null;
						break;
				}
			} // proc Load

			private Task SaveAsync()
			{
				var xState = new XElement("state",
					Procs.XAttributeCreate("v", state)
				);

				if (accountKey != null)
					xState.Add(new XElement("account", new XCData(accountKey)));

				switch (state)
				{
					case AcmeState.Pending:
						xState.Add(new XElement("order", orderUri?.ToString()));
						xState.Add(new XElement("token", token));
						xState.Add(new XElement("authz", keyAuthz));
						break;
					case AcmeState.Installed:
					case AcmeState.Issued:
						if (pfxContent != null)
							xState.Add(new XElement("pfx", Convert.ToBase64String(pfxContent)));
						break;

				}

				return Task.Run(() => xState.Save(fileName));
			} // proc SaveAsync

			#endregion

			#region -- Core Helper ----------------------------------------------------

			private async Task<ChallengeStatus> GetChallengeStateAsync(IOrderContext order)
			{
				var http = await (await order.Authorizations()).First().Http();
				var httpRes = await http.Resource();
				var status = httpRes.Status ?? ChallengeStatus.Invalid;

				if (status == ChallengeStatus.Pending) // start validating
					await http.Validate();

				return status;
			} // func GetCurrentStateAsync

			private AcmeContext CreateAcme()
			{
				if (!IsAccountValid)
					throw new ArgumentException("No account defined.");

				return new AcmeContext(acmeUri, KeyFactory.FromPem(accountKey));
			} // func CreateAcme

			#endregion

			#region -- Account --------------------------------------------------------

			public async Task CreateAccountAsync(string email)
			{
				var acme = new AcmeContext(acmeUri);
				var account = await acme.NewAccount(email, true);

				// update account key
				accountKey = acme.AccountKey.ToPem();

				await SaveAsync();
			} // proc CreateAccountAsync

			public Task ImportAccountKeyAsync(string pem)
			{
				accountKey = pem;
				return SaveAsync();
			} // func ImportAccountKeyAsync

			public async Task<string> GetAccountStateAsync()
			{
				var acme = CreateAcme();
				var account = await acme.Account();
				var res = await account.Resource();
				return res.Status + "; " + (res.TermsOfServiceAgreed ?? false ? "agreed" : "not agreed");
			} // func GetAccountStateAsync

			#endregion

			#region -- Order ----------------------------------------------------------

			public async Task NewOrderAsync()
			{
				var acme = CreateAcme();

				// place new order
				var newOrder = await acme.NewOrder(new string[] { commonName });

				// get token
				var authz = (await newOrder.Authorizations()).First();
				var http = await authz.Http();

				// save request
				orderUri = newOrder.Location;
				token = http.Token;
				keyAuthz = http.KeyAuthz;
				pfxContent = null;

				// change state
				state = AcmeState.Pending;
				await SaveAsync();
			} // func NewOrderAsync

			#endregion

			#region -- Challenge ------------------------------------------------------

			private string GetChallengeState(Task<ChallengeStatus> status)
			{
				switch (status.Result)
				{
					case ChallengeStatus.Pending:
						return "pending";
					case ChallengeStatus.Processing:
						return "processing";
					case ChallengeStatus.Valid:
						return "valid";
					default:
						return "failed";
				}
			} // func GetChallengeState

			public Task<string> GetChallengeStateAsync()
			{
				if (orderUri == null)
					return Task.FromResult("failed");

				var acme = CreateAcme();
				return GetChallengeStateAsync(acme.Order(orderUri)).ContinueWith(GetChallengeState);
			} // func GetChallengeStateAsync

			#endregion

			#region -- Generate -------------------------------------------------------

			public async Task<byte[]> GenerateKeyAsync(string pfxPassword = null)
			{
				if (orderUri == null)
					return null;

				var acme = CreateAcme();
				var order = acme.Order(orderUri);

				// check state
				var status = await GetChallengeStateAsync(order);
				if (status != ChallengeStatus.Valid)
					return null;

				// generate key
				var newPrivateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
				var certififcateChain = await order.Generate(new CsrInfo() { CommonName = commonName }, newPrivateKey);
				var pfxBuilder = certififcateChain.ToPfx(newPrivateKey);

				// clear challenge
				orderUri = null;
				token = null;
				keyAuthz = null;

				// mark issued
				pfxContent = pfxBuilder.Build(commonName, pfxPassword ?? String.Empty);
				state = AcmeState.Issued;
				await SaveAsync();

				return pfxContent;
			} // func GenerateKeyAsync

			public byte[] GetPfxContent()
				=> state == AcmeState.Issued ? pfxContent : null;

			#endregion

			#region -- Valid ----------------------------------------------------------

			public Task SetValidAsync()
			{
				// set state
				state = AcmeState.Installed;
				return SaveAsync();
			} // func SetValidAsync

			#endregion

			public string CommonName => commonName;

			/// <summary>Account key</summary>
			public string AccountKey => accountKey;

			/// <summary>Challange uri.</summary>
			public Uri ChallangeUri => orderUri;
			/// <summary>Token of the authent-key</summary>
			public string Token => token;
			/// <summary>Authent-key</summary>
			public string KeyAuthz => keyAuthz;

			public AcmeState State => state;

			/// <summary></summary>
			public bool IsAccountValid => !String.IsNullOrEmpty(accountKey);
		} // class AcmeStateStore

		#endregion

		#region -- Ctor/Dtor ----------------------------------------------------------

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		public AcmeCronItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		#endregion

		#region -- TryGetState --------------------------------------------------------

		private static DateTime GetCurrentCertifcateNotAfter(string commonName)
		{
			var cert = ProcsDE.FindCertificate("store://lm/my/CN=" + commonName)
				.OrderByDescending(c => c.NotAfter)
				.FirstOrDefault();

			if (cert == null)
				return DateTime.MinValue;

			return cert.NotAfter;
		} // func GetCurrentCertifcateNotAfter

		private static Uri GetAcmeUri(string acme)
		{
			if (acme == null)
				return WellKnownServers.LetsEncryptStagingV2;
			else if (acme == "letsencrypt")
				return WellKnownServers.LetsEncryptV2;
			else
				return new Uri(acme, UriKind.Absolute);
		} // func GetAcmeUri

		private bool TryGetState(LogMsgType type, out AcmeStateStore state)
		{
			var acmeUri = GetAcmeUri(Config.GetAttribute("acme", null));
			var hostName = Config.GetAttribute("commonName", null);

			if (acmeUri == null)
			{
				const string msg = "Kein Automatic Certificate Management Service defined.";
				if (type == LogMsgType.Error)
					throw new ArgumentNullException(nameof(acmeUri), msg);
				if (type == LogMsgType.Warning)
					Log.Warn(msg);
				state = null;
				return false;
			}
			if (hostName == null)
			{
				const string msg = "No HostName defined.";
				if (type == LogMsgType.Error)
					throw new ArgumentNullException(nameof(acmeUri), msg);
				if (type == LogMsgType.Warning)
					Log.Warn(msg);
				state = null;
				return false;
			}

			state = new AcmeStateStore(acmeUri, hostName, Path.ChangeExtension(LogFileName, ".state"));
			return true;
		} // func TryGetState

		#endregion

		#region -- NewCertificate, Generate, Update -----------------------------------

		private async Task NewCertificateAsync(AcmeStateStore state, bool force)
		{
			var notAfter = force ? DateTime.MinValue : GetCurrentCertifcateNotAfter(state.CommonName);
			if (notAfter < DateTime.Now
				|| notAfter.AddDays(-10) < DateTime.Now)
			{
				// create a new order
				await state.NewOrderAsync();
				// wait one second
				await Task.Delay(1000);
				// start validation process
				await state.GetChallengeStateAsync();
			}
		} // proc NewCertificate

		private async Task<bool> GenerateAsync(AcmeStateStore state)
		{
			var r = await state.GetChallengeStateAsync();
			if (r != "valid")
			{
				Log.Info("State is '{0}', expected is 'valid'.", r);
				return false;
			}

			// read certificate
			var pfx = new X509Certificate2(await state.GenerateKeyAsync());

			// install certificate in store
			await UpdateCertificateAsync(state, pfx);

			return true;
		} // func ValidateStateAsync

		private async Task UpdateCertificateAsync(AcmeStateStore state, X509Certificate2 pfx)
		{
			// install certificate in store
			await Task.Run(() =>
			{
				using (var lm = new X509Store(StoreName.My, StoreLocation.LocalMachine))
				{
					lm.Open(OpenFlags.ReadWrite);

					if (lm.Certificates.Find(X509FindType.FindByThumbprint, pfx.Thumbprint, false) != null)
					{
						Log.Info("Install Certificate {0} in LocalMachine\\My", pfx.Thumbprint);
						lm.Add(pfx);
					}
					else
						Log.Info("Certificate {0} already installed in LocalMachine\\My", pfx.Thumbprint);
				}
			});

			// register in http.sys
			//Log.Info("Register Certificate {0} for endpoint {1}", pfx.Thumbprint);
			//pfx.Thumbprint;
			// netsh http del sslcert ipport=0.0.0.0:443 certhash={0}
			// netsh http add sslcert ipport=0.0.0.0:443 certhash={0} appid="{$(New-Guid)}"
		} // func InstallCertificateAsync

		#endregion

		#region -- OnRunJob -----------------------------------------------------------

		/// <summary>Run job</summary>
		/// <param name="cancellation"></param>
		protected override void OnRunJob(CancellationToken cancellation)
		{
			if (TryGetState(LogMsgType.Warning, out var state)) // get state file
			{
				switch (state.State)
				{
					case AcmeState.Installed:
						NewCertificateAsync(state, false).AwaitTask();
						this.GetService<IDECronEngine>(true).UpdateNextRuntime(this, DateTime.Now.AddMinutes(1));
						break;
					case AcmeState.Pending:
						if (!GenerateAsync(state).AwaitTask())
							this.GetService<IDECronEngine>(true).UpdateNextRuntime(this, DateTime.Now.AddMinutes(1));
						break;
					case AcmeState.Issued:
						UpdateCertificateAsync(state, new X509Certificate2(state.GetPfxContent())).AwaitTask();
						break;
				}
			}
		} // proc OnRunJob

		#endregion

		#region -- Lua Interface ------------------------------------------------------

		/// <summary>Import a account key.</summary>
		/// <param name="pemData"></param>
		[LuaMember]
		public void ImportAccountKey(string pemData)
		{
			if (TryGetState(LogMsgType.Error, out var state))
				state.ImportAccountKeyAsync(pemData).AwaitTask();
		} // proc ImportAccountKey

		/// <summary>Import a account key.</summary>
		/// <param name="email"></param>
		[LuaMember]
		public void CreateAccountKey(string email)
		{
			if (TryGetState(LogMsgType.Error, out var state))
				state.CreateAccountAsync(email).AwaitTask();
		} // proc ImportAccountKey

		/// <summary>Start a new certificate order.</summary>
		[LuaMember]
		public void StartNewOrder()
		{
			if (TryGetState(LogMsgType.Error, out var state))
				NewCertificateAsync(state, true).AwaitTask();
		} // proc ImportAccountKey

		/// <summary>Update certificates</summary>
		[LuaMember]
		public void UpdateCertificate()
		{
			if (TryGetState(LogMsgType.Error, out var state))
			{
				if (state.State == AcmeState.Pending
					|| state.GetPfxContent() == null)
					throw new ArgumentNullException("pfx", "No certificate.");

				// update
				UpdateCertificateAsync(state, new X509Certificate2(state.GetPfxContent())).AwaitTask();
			}
		} // proc ImportAccountKey

		#endregion

		#region -- OnProcessRequestAsync ----------------------------------------------

		/// <summary>Return token</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		protected override Task<bool> OnProcessRequestAsync(IDEWebRequestScope r)
		{
			if (TryGetState(LogMsgType.Information, out var state)
				&& state.State == AcmeState.Pending
				&& r.RelativeSubPath == state.Token)
			{
				return r.WriteTextAsync(state.KeyAuthz).ContinueWith(t => true);
			}
			
			return base.OnProcessRequestAsync(r);
		} // func OnProcessRequestAsync

		#endregion
	} // class AcmeCronItem
}