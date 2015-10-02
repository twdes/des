using System;
using System.Net;
using System.Security.Principal;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Applications
{
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
				lock (this)
				{
					if (serviceType == typeof(WindowsImpersonationContext) &&
						identity is WindowsIdentity)
						return ((WindowsIdentity)identity).Impersonate();
					else
						return null;
				}
			} // func GetService

			public IIdentity Identity => identity;
		} // class UserContext

		#endregion

		private int iServerSecurityVersion = 0;
		private string[] securityTokens = null;
		private object securityTokensLock = new object();

		#region -- Ctor/Dtor/Configuration ------------------------------------------------

		public DEUser(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			Server.RegisterUser(this);
		} // ctor

		protected override void Dispose(bool lDisposing)
		{
			if (lDisposing)
				Server.UnregisterUser(this);
			base.Dispose(lDisposing);
		} // proc Dispose

		#endregion

		#region -- Security ---------------------------------------------------------------

		public IDEAuthentificatedUser Authentificate(IIdentity identity)
		{
			if (identity is WindowsIdentity)
				if (identity.IsAuthenticated)
					return new UserContext(this, identity);
				else
					return null;
			else if (identity is HttpListenerBasicIdentity)
				if (TestPassowrd(((HttpListenerBasicIdentity)identity).Password))
					return new UserContext(this, identity);
				else
					return null;
			else
				return null;
		} // func Authentificate

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
				if (securityTokens == null || iServerSecurityVersion != currentServerSecurityVersion)
				{
					// Erzeuge die Tokens
					securityTokens = Server.BuildSecurityTokens(Config.GetAttribute("groups", String.Empty));

					// Setze die Version
					iServerSecurityVersion = currentServerSecurityVersion;
				}
			}
		} // proc RefreshSecurityTokens

		private bool TestPassowrd(string testPassword)
		{
			var password = Config.GetAttribute("password", null);
			if (password != null)
				return password == testPassword;
			else
				try
				{
					var l = ProcsDE.PasswordCompare(testPassword, Config.GetAttribute("passwordHash", null));
					if (!l)
						Log.LogMsg(LogMsgType.Warning, "Authentifizierung fehlgeschlagen.");
					return l;
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "Authentifizierung fehlgeschlagen ({0}).", e.Message);
					return false;
				}
		} // func TestPassword

		#endregion

		public override string Icon { get { return "/images/user1.png"; } }
	} // class DEUser

	#endregion
}
