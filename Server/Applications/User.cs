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
	#region -- class DESimpleIdentity -------------------------------------------------

	internal sealed class DESimpleIdentity : IIdentity
	{
		public DESimpleIdentity(string userName)
			=> Name = userName ?? throw new ArgumentNullException(nameof(userName));

		public override int GetHashCode() 
			=> Name.GetHashCode();

		public override bool Equals(object obj)
			=> obj is DESimpleIdentity c ? Name.Equals(c.Name) : false;

		public string Name { get; }

		public string AuthenticationType => "basic";
		public bool IsAuthenticated => false;
	} // class DESimpleIdentity

	#endregion

	#region -- class DEUser -----------------------------------------------------------

	/// <summary>Generic user implementation</summary>
	internal sealed class DEUser : DEConfigItem, IDEUser
	{
		#region -- class UserContext --------------------------------------------------

		/// <summary></summary>
		private sealed class UserContext : IDEAuthentificatedUser
		{
			private readonly object userLock = new object();
			private DEUser user;
			private readonly IIdentity identity;

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
					return serviceType == typeof(WindowsImpersonationContext) && identity is WindowsIdentity windowsIdentity
						? windowsIdentity.Impersonate()
						: null;
				}
			} // func GetService

			public IIdentity Identity => identity;
		} // class UserContext

		#endregion

		private readonly object securityTokensLock = new object();
		private int serverSecurityVersion = 0;
		private string[] securityTokens = null;

		private IIdentity identity = null;
		private string userName = null;

		#region -- Ctor/Dtor/Configuration --------------------------------------------

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

			identity = new DESimpleIdentity(userName);

			// register the user again
			Server.RegisterUser(this);

			base.OnEndReadConfiguration(config);
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Security -----------------------------------------------------------

		public Task<IDEAuthentificatedUser> AuthentificateAsync(IIdentity identity)
		{
			if (identity is WindowsIdentity windowsIdentity)
			{
				return windowsIdentity.IsAuthenticated
					? Task.FromResult<IDEAuthentificatedUser>(new UserContext(this, identity))
					: null;
			}
			else if (identity is HttpListenerBasicIdentity basicIdentity)
			{
				return TestPassword(basicIdentity.Password)
					? Task.FromResult<IDEAuthentificatedUser>(new UserContext(this, identity))
					: null;
			}
			else
				return null;
		} // func AuthentificateAsync

		private bool DemandToken(string securityToken)
		{
			RefreshSecurityTokens();

			// Is the token in list
			lock (securityTokensLock)
				return Array.BinarySearch(securityTokens, securityToken.ToLower()) >= 0;
		} // func DemandToken

		private void RefreshSecurityTokens()
		{
			lock (securityTokensLock)
			{
				// Create the new token list
				var currentServerSecurityVersion = Server.SecurityGroupsVersion;
				if (securityTokens == null || serverSecurityVersion != currentServerSecurityVersion)
				{
					// Resolve token groups to the list
					securityTokens = Server.BuildSecurityTokens(Config.GetAttribute("groups", String.Empty));

					// Set the new security version
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
			{
				try
				{
					var tmp = ProcsDE.PasswordCompare(testPassword, Config.GetAttribute("passwordHash", null));
					if (!tmp)
						Log.LogMsg(LogMsgType.Warning, String.Format("Autentification failed ({0}).", "Password"));
					return tmp;
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "Autentification failed ({0}).", e.Message);
					return false;
				}
			}
		} // func TestPassword

		#endregion

		string IDEUser.DisplayName => userName;
		IIdentity IDEUser.Identity => identity;

		public override string Icon => "/images/user1.png";
	} // class DEUser

	#endregion
}
