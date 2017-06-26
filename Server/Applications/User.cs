#region -- copyright --
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
using System.Net;
using System.Security.Principal;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Applications
{
	#region -- class DEUserNamePrincipal ------------------------------------------------

	internal sealed class DEUserNamePrincipal : IPrincipal, IIdentity
	{
		private readonly string userName;

		public DEUserNamePrincipal(string userName)
			=> this.userName = userName ?? throw new ArgumentNullException(nameof(userName));

		public IIdentity Identity => this;
		public string Name => userName;
		public string AuthenticationType => "basic";
		public bool IsAuthenticated => false;

		public bool IsInRole(string role)
			=> false;
	} // class DEUserNamePrincipal

	#endregion

	#region -- class DEUser -------------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DEUser : DEConfigItem, IDEUser
	{
		#region -- class UserContext ------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class UserContext : IDEAuthentificatedUser
		{
			private readonly object userLock = new object();
			private DEUser user;
			private IIdentity identity;

			public UserContext(DEUser user, IIdentity identity)
			{
				this.user = user;
				this.identity = identity;
			} // ctor

			public void Dispose()
			{
			} // proc Dispose

			public bool IsInRole(string role)
				=> user.DemandToken(role);

			public object GetService(Type serviceType)
			{
				lock (userLock)
				{
					if (serviceType == typeof(WindowsImpersonationContext) && identity is WindowsIdentity)
						return ((WindowsIdentity)identity).Impersonate();
					else
						return null;
				}
			} // func GetService

			public IIdentity Identity => identity;
		} // class UserContext

		#endregion

		private readonly object securityTokensLock = new object();
		private int serverSecurityVersion = 0;
		private string[] securityTokens = null;

		private string userName = null;

		#region -- Ctor/Dtor/Configuration ------------------------------------------------

		public DEUser(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				Server.UnregisterUser(this);
			base.Dispose(disposing);
		} // proc Dispose

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			// unregister the user
			if (userName != null)
				Server.UnregisterUser(this);

			base.OnBeginReadConfiguration(config);
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			// get user name
			userName = Config.GetAttribute("userName", Name);
			var domain = Config.GetAttribute("domain", String.Empty);
			if (!String.IsNullOrEmpty(domain))
				userName = domain + '\\' + userName;

			// register the user again
			Server.RegisterUser(this);

			base.OnEndReadConfiguration(config);
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Security ---------------------------------------------------------------

		public Task<IDEAuthentificatedUser> AuthentificateAsync(IIdentity identity)
		{
			if (identity is WindowsIdentity)
				if (identity.IsAuthenticated)
					return Task.FromResult<IDEAuthentificatedUser>(new UserContext(this, identity));
				else
					return null;
			else if (identity is HttpListenerBasicIdentity)
				if (TestPassword(((HttpListenerBasicIdentity)identity).Password))
					return Task.FromResult<IDEAuthentificatedUser>(new UserContext(this, identity));
				else
					return null;
			else
				return null;
		} // func AuthentificateAsync

		private bool DemandToken(string securityToken)
		{
			RefreshSecurityTokens();

			// Ist der Token enthalten
			lock (securityTokensLock)
				return Array.BinarySearch(securityTokens, securityToken.ToLower()) >= 0;
		} // func DemandToken

		private void RefreshSecurityTokens()
		{
			lock (securityTokensLock)
			{
				// Erzeuge die Tokens
				var currentServerSecurityVersion = Server.SecurityGroupsVersion;
				if (securityTokens == null || serverSecurityVersion != currentServerSecurityVersion)
				{
					// Erzeuge die Tokens
					securityTokens = Server.BuildSecurityTokens(Config.GetAttribute("groups", String.Empty));

					// Setze die Version
					serverSecurityVersion = currentServerSecurityVersion;
				}
			}
		} // proc RefreshSecurityTokens

		private bool TestPassword(string testPassword)
		{
			var password = Config.GetAttribute("password", null);
			if (password != null)
				return password == testPassword;
			else
				try
				{
					var l = ProcsDE.PasswordCompare(testPassword, Config.GetAttribute("passwordHash", null));
					if (!l)
						Log.LogMsg(LogMsgType.Warning, String.Format("Autentification failed ({0}).", "Password"));
					return l;
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "Autentification failed ({0}).", e.Message);
					return false;
				}
		} // func TestPassword

		#endregion

		string IDEUser.Name => userName;

		public override string Icon => "/images/user1.png";
	} // class DEUser

	#endregion
}
