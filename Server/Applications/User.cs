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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security;
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
			=> obj is DESimpleIdentity c && Name.Equals(c.Name);

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
		private sealed class UserContext : DEAuthentificatedUser<DEUser>
		{
			public UserContext(DEUser user, IIdentity loginIdentity)
				: base(user, loginIdentity)
			{
			} // ctor

			public override bool IsInRole(string role)
				=> User.DemandToken(role);
		} // class UserContext

		#endregion

		private readonly object securityTokensLock = new ();
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

		bool IEquatable<IDEUser>.Equals(IDEUser other)
			=> ReferenceEquals(this, other);

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
				return Task.FromResult<IDEAuthentificatedUser>(
					windowsIdentity.IsAuthenticated
						? new UserContext(this, identity)
						: null
				);
			}
			else if (identity is HttpListenerBasicIdentity basicIdentity)
			{
				return Task.FromResult<IDEAuthentificatedUser>(
					TestPassword(basicIdentity.Password)
						? new UserContext(this, identity)
						: null
				);
			}
			else
				return Task.FromResult<IDEAuthentificatedUser>(null);
		} // func AuthentificateAsync

		private bool DemandToken(string securityToken)
			=> Array.BinarySearch(RefreshSecurityTokens(), securityToken.ToLower()) >= 0; // Is the token in list

		private string[] RefreshSecurityTokens()
		{
			lock (securityTokensLock)
			{
				// Create the new token list
				var currentServerSecurityVersion = Server.SecurityGroupsVersion;
				if (securityTokens == null || serverSecurityVersion != currentServerSecurityVersion)
				{
					// Resolve token groups to the list
					securityTokens = Server.BuildSecurityTokens(Config.GetAttribute("groups", String.Empty), SecurityUser);

					// Set the new security version
					serverSecurityVersion = currentServerSecurityVersion;
				}
			}
			return securityTokens;
		} // proc RefreshSecurityTokens

		private bool TestPassword(string testPassword)
		{
			var password = ConfigNode.GetAttribute<SecureString>("password");
			if (password != null)
				return password.Compare(testPassword);
			else
			{
				var passwordHash = Config.GetAttribute("passwordHash", null);
				if (passwordHash == "*")
					return true; // allow all passwords

				try
				{
					var tmp = ProcsDE.PasswordCompare(testPassword, passwordHash);
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

		bool IPropertyReadOnlyDictionary.TryGetProperty(string name, out object value)
			=> Members.TryGetValue(name, out value);

		IEnumerator<PropertyValue> IEnumerable<PropertyValue>.GetEnumerator()
			=> Members.Select(c => new PropertyValue(c.Key, c.Value)).GetEnumerator();

		string IDEUser.DisplayName => userName;
		IIdentity IDEUser.Identity => identity;
		IReadOnlyList<string> IDEUser.SecurityTokens => RefreshSecurityTokens();

		public override string Icon => "/images/user1.png";
	} // class DEUser

	#endregion
}
