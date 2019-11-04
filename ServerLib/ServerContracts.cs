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
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEAuthentificatedUser ---------------------------------------

	/// <summary>Authentifizierte Nutzer in dem alle Informationen für die aktuelle 
	/// Abfrage gespeichert werden können. Zusätzliche Dienste können via
	/// GetService erfragt werden.</summary>
	public interface IDEAuthentificatedUser : IServiceProvider, IPrincipal, IDisposable
	{
		/// <summary>Return user info</summary>
		IDEUser Info { get; }
	} // interface IDEAuthentificatedUser

	#endregion

	#region -- interface IDEUser ------------------------------------------------------

	/// <summary>User that is registered in the main server..</summary>
	public interface IDEUser : IPropertyReadOnlyDictionary
	{
		/// <summary>Creates a authentificated user.</summary>
		/// <param name="identity">Incoming identity from the user, to check security.</param>
		/// <returns>Context of the authentificated user.</returns>
		Task<IDEAuthentificatedUser> AuthentificateAsync(IIdentity identity);

		/// <summary>Display name for the user</summary>
		string DisplayName { get; }
		/// <summary>Identity of the user.</summary>
		IIdentity Identity { get; }
		/// <summary>Show all security tokens.</summary>
		string[] SecurityTokens { get; }
	} // interface IDEUser

	#endregion

	#region -- interface IDEBaseLog ---------------------------------------------------

	/// <summary>Base log contract.</summary>
	public interface IDEBaseLog
	{
		/// <summary>Gesamtanzahl der Log-Dateien.</summary>
		int TotalLogCount { get; set; }
	} // interface IDEBaseLog

	#endregion

	#region -- interface DEServerEvent ------------------------------------------------

	/// <summary>Special events of the server.</summary>
	public enum DEServerEvent
	{
		/// <summary>Shutdown is initiated.</summary>
		Shutdown,
		/// <summary>Reconfiguration phase.</summary>
		Reconfiguration
	} // DEServerEvent

	#endregion

	#region -- interface IDEServerQueue -----------------------------------------------

	/// <summary>Der Service bietet einen Hintergrund-Thread für die Bearbeitung
	/// nicht Zeitkritischer aufgaben an.</summary>
	public interface IDEServerQueue
	{
		/// <summary>Registers a method, that will be processed during idle of the queue thread.</summary>
		/// <param name="action">Action to run.</param>
		/// <param name="timebetween">Time between the calls, this time is not guaranteed. If the queue thread is under heavy presure it will take longer.</param>
		void RegisterIdle(Action action, int timebetween = 1000);
		/// <summary>Registers a method, that will be executed on an event of the server.</summary>
		/// <param name="action"></param>
		/// <param name="eventType"></param>
		void RegisterEvent(Action action, DEServerEvent eventType);
		/// <summary>Execute the command in the queue thread.</summary>
		/// <param name="action">Command to execute</param>
		/// <param name="timeElapsed">Wait before executed in milliseconds</param>
		void RegisterCommand(Action action, int timeElapsed = 0);
		/// <summary>Removes a command/idle/event action.</summary>
		/// <param name="action">Command/idle/evetn to cancel</param>
		void CancelCommand(Action action);

		/// <summary>Returns the factory for the queue thread. Every task gets executed in a single thread.</summary>
		TaskFactory Factory { get; }

		/// <summary>Get the state of the queue thread. <c>true</c>, means that task can be scheduled. In the 
		/// shutdown phase, no tasks can be added.</summary>
		bool IsQueueRunning { get; }
		/// <summary>Is the current thread Id equal to the queue thread id.</summary>
		bool IsQueueRequired { get; }
	} // interface IDEServerQueue
	#endregion

	#region -- interface IDEServer ----------------------------------------------------

	/// <summary>Contract for the main service host..</summary>
	public interface IDEServer : IDEConfigItem
	{
		/// <summary>Schreibt in das Systemlog</summary>
		/// <param name="type">Typ</param>
		/// <param name="message">Nachricht</param>
		/// <param name="id"></param>
		/// <param name="category"></param>
		/// <param name="rawData"></param>
		void LogMsg(EventLogEntryType type, string message, int id = 0, short category = 0, byte[] rawData = null);
		/// <summary>Meldet einen Fehler in das Systemlog.</summary>
		/// <param name="e"></param>
		void LogMsg(Exception e);

		/// <summary>Versucht einen Knoten über die Extension zu laden.</summary>
		/// <param name="config">In diesen Knoten soll der neue Knoten eingefügt werden</param>
		/// <param name="element">Konfigurationselement, welches geladen werden soll.</param>
		/// <param name="currentNamespace">Aktueller Namespace. Kann null sein, damit nicht auf missing Extensions geprüft wird.</param>
		/// <returns>Wurde etwas geladen.</returns>
		bool LoadConfigExtension(IDEConfigLoading config, XElement element, string currentNamespace);

		/// <summary>Send a event to the client.</summary>
		/// <param name="item">Config node, the event is attached to.</param>
		/// <param name="securityToken">Security token, who can see this event. <c>null</c>, is filled with the node security token.</param>
		/// <param name="event">Event id</param>
		/// <param name="index">Index of the event.</param>
		/// <param name="values">Additional arguments</param>
		void AppendNewEvent(DEConfigItem item, string securityToken, string @event, string index, XElement values);

		/// <summary>Registriert einen Nutzer innerhalb des HttpServers</summary>
		/// <param name="user">Nutzer</param>
		void RegisterUser(IDEUser user);
		/// <summary>Entfernt den Nutzer aus dem HttpServer.</summary>
		/// <param name="user"></param>
		void UnregisterUser(IDEUser user);
		/// <summary>Create authentificated user.</summary>
		/// <param name="user">Identity of the user.</param>
		/// <returns>Context of the user.</returns>
		Task<IDEAuthentificatedUser> AuthentificateUserAsync(IIdentity user);
		/// <summary>Erzeugt aus der Token-Zeichenfolge eine Tokenliste.</summary>
		/// <param name="securityTokens">Token-Zeichenfolge</param>
		/// <returns>Security-Token-Array</returns>
		string[] BuildSecurityTokens(params string[] securityTokens);

		/// <summary>Gibt das Verzeichnis für die Loginformationen zurück.</summary>
		string LogPath { get; }
		/// <summary>Basiskonfigurationsdatei, die geladen wurde.</summary>
		IDEConfigurationService Configuration { get; }
		/// <summary>Access to the main message queue.</summary>
		IDEServerQueue Queue { get; }

		/// <summary>Version der SecurityTokens</summary>
		int SecurityGroupsVersion { get; }
	} // interface IDEServer

	#endregion

	#region -- interface IDEDebugContext ----------------------------------------------

	/// <summary>This service is registered, if this command was invoked from an debug session.</summary>
	public interface IDEDebugContext
	{
		/// <summary>End point for the log system to notify log-messages.</summary>
		/// <param name="type">Qualification of the message.</param>
		/// <param name="message">Message</param>
		void OnMessage(LogMsgType type, string message);
	} // interface IDEDebugContext

	#endregion

	#region -- interface IDETransaction -----------------------------------------------

	/// <summary>Transaction for the current action/command/request.</summary>
	public interface IDETransaction
	{
		/// <summary>Commit all operations.</summary>
		void Commit();
		/// <summary>Rollback all operations.</summary>
		void Rollback();
	} // interface IDETransaction

	#endregion

	#region -- interface IDETransactionAsync ------------------------------------------

	/// <summary>Transaction for the current action/command/request.</summary>
	public interface IDETransactionAsync
	{
		/// <summary>Commit all operations.</summary>
		Task CommitAsync();
		/// <summary>Rollback all operations.</summary>
		Task RollbackAsync();
	} // IDETransactionAsync

	#endregion

	#region -- interface IDECommonScope -----------------------------------------------

	/// <summary>Basic context contract, for an execution thread.</summary>
	public interface IDECommonScope : IPropertyReadOnlyDictionary, IDEScope
	{
		/// <summary>Registers a service factory.</summary>
		/// <param name="serviceType"></param>
		/// <param name="factory"></param>
		void RegisterService(Type serviceType, Func<object> factory);
		/// <summary>Registers a service.</summary>
		/// <param name="serviceType"></param>
		/// <param name="service"></param>
		void RegisterService(Type serviceType, object service);
		/// <summary>Registers a service.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="factory"></param>
		void RegisterService<T>(Func<T> factory) where T : class;
		/// <summary></summary>
		/// <param name="serviceType"></param>
		/// <returns></returns>
		bool RemoveService(Type serviceType);

		/// <summary>Register a commit action.</summary>
		/// <param name="action"></param>
		void RegisterCommitAction(Action action);
		/// <summary>Register a commit action.</summary>
		/// <param name="action"></param>
		void RegisterCommitAction(Func<Task> action);
		/// <summary>Register a rollback action.</summary>
		/// <param name="action"></param>
		void RegisterRollbackAction(Action action);
		/// <summary>Register a rollback action.</summary>
		/// <param name="action"></param>
		void RegisterRollbackAction(Func<Task> action);

		/// <summary>Register an object, that will be disposed with this scope.</summary>
		/// <param name="obj"></param>
		void RegisterDispose(IDisposable obj);

		/// <summary></summary>
		/// <param name="ns"></param>
		/// <param name="variable"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		bool TryGetGlobal(object ns, object variable, out object value);
		/// <summary></summary>
		/// <param name="ns"></param>
		/// <param name="variable"></param>
		/// <param name="value"></param>
		void SetGlobal(object ns, object variable, object value);

		/// <summary>Executes all commit actions.</summary>
		/// <returns></returns>
		Task CommitAsync();
		/// <summary>Executes all rollback actions.</summary>
		/// <returns></returns>
		Task RollbackAsync();

		/// <summary>Access to the user context.</summary>
		/// <typeparam name="T"></typeparam>
		T GetUser<T>() where T : class;
		/// <summary>Raw access to the user.</summary>
		IDEAuthentificatedUser User { get; }

		/// <summary>Check for the given token, if the user can access it.</summary>
		/// <param name="securityToken">Security token.</param>
		void DemandToken(string securityToken);
		/// <summary>Check for the given token, if the user can access it.</summary>
		/// <param name="securityToken">Security token.</param>
		/// <returns><c>true</c>, if the token is granted.</returns>
		bool TryDemandToken(string securityToken);

		/// <summary>Create an authorization exception in the current context.</summary>
		/// <param name="message">Message of this exception</param>
		/// <returns></returns>
		Exception CreateAuthorizationException(string message);

		/// <summary>Language for this context.</summary>
		CultureInfo CultureInfo { get; }
		/// <summary>Access to the base server.</summary>
		IDEServer Server { get; }
	} // interface IDECommonScope

	#endregion

	#region -- class DECommonScope ----------------------------------------------------

	/// <summary>Scope with transaction model and a user based service provider.</summary>
	public class DECommonScope : DEScope, IDECommonScope
	{
		private static readonly string[] restrictAllGroups = new string[] { String.Empty };
		private static readonly string[] allowAllGroups = Array.Empty<string>();

		#region -- struct GlobalKey ---------------------------------------------------

		private struct GlobalKey : IEquatable<GlobalKey>
		{ 
			private readonly object ns;
			private readonly object n;

			public GlobalKey(object ns, object variable)
			{
				this.ns = ns ?? throw new ArgumentNullException(nameof(ns));
				this.n = variable ?? throw new ArgumentNullException(nameof(variable));
			} // ctor

			public override bool Equals(object obj)
				=> obj is GlobalKey t ? Equals(t) : false;

			public bool Equals(GlobalKey other)
				=> (Object.ReferenceEquals(ns, other.ns) || ns.Equals(other.ns))
					&& (Object.ReferenceEquals(n, other.n) || n.Equals(other.n));

			public override int GetHashCode()
				=> ns.GetHashCode() ^ n.GetHashCode();
		} // class GlobalKey

		#endregion

		#region -- class ServiceDescriptor --------------------------------------------

		private sealed class ServiceDescriptor
		{
			private readonly Func<object> factory;
			private object service;
			
			public ServiceDescriptor(object service)
			{
				this.factory = null;
				this.service = service;
			} // ctor

			public ServiceDescriptor(Func<object> factory)
			{
				this.factory = factory;
				this.service = null;
			} // ctor

			private object GetService()
			{
				if (service == null)
				{
					lock (factory)
						service = factory();
				}
				return service;
			} // func GetService

			public object Service => GetService();
			public bool IsValueCreated => service != null;
			public bool IsDisposable => service is IDisposable && factory != null;
		} // class ServiceDescriptor

		#endregion

		private readonly List<Func<Task>> commitActions = new List<Func<Task>>();
		private readonly List<Func<Task>> rollbackActions = new List<Func<Task>>();
		private readonly List<IDisposable> autoDispose = new List<IDisposable>();

		private readonly Dictionary<Type, ServiceDescriptor> services = new Dictionary<Type, ServiceDescriptor>();
		private readonly Dictionary<GlobalKey, object> globals = new Dictionary<GlobalKey, object>();

		private readonly IServiceProvider sp;
		private readonly IDEServer server;
		private readonly bool useAuthentification;
		private readonly string[] allowGroups;
		private IDEAuthentificatedUser user = null;
		private bool userOwner;

		private bool? isCommitted = null;
		private bool isDisposed = false;

		#region -- Ctor/Dtor ----------------------------------------------------------

		private DECommonScope(IServiceProvider sp, IDEAuthentificatedUser user, bool useAuthentification, string allowGroups)
		{
			this.sp = sp ?? throw new ArgumentNullException(nameof(sp));
			this.server = sp.GetService<IDEServer>(true);

			this.user = user;
			this.userOwner = user == null;
			this.useAuthentification = useAuthentification;
			this.allowGroups = allowGroups == "*" ? allowAllGroups : (Procs.GetStrings(allowGroups, true) ?? restrictAllGroups);
			this.allowGroups = sp.GetService<IDEServer>(true).BuildSecurityTokens(allowGroups);
		} // ctor

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="user"></param>
		public DECommonScope(IServiceProvider sp, IDEAuthentificatedUser user)
			: this(sp, user, user != null, null)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="useAuthentification"></param>
		/// <param name="allowGroups"></param>
		public DECommonScope(IServiceProvider sp, bool useAuthentification, string allowGroups)
			: this(sp, null, useAuthentification, allowGroups)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
			=> DisposeAsync().AwaitTask();

		/// <summary></summary>
		/// <returns></returns>
		public async Task DisposeAsync()
		{
			if (isDisposed)
				return;
			isDisposed = true;

			if (!isCommitted.HasValue)
				await RollbackAsync();

			// dispose resources
			foreach (var dispose in autoDispose)
				await Task.Run(new Action(dispose.Dispose));
			autoDispose.Clear();

			// dispose services
			IEnumerable<IDisposable> activeServices;
			lock (services)
				activeServices = from c in services.Values where c.IsDisposable select (IDisposable)c.Service;
			await Task.Run(
				() =>
				{
					foreach (var c in activeServices)
						c.Dispose();
				}
			);

			// clear user
			if (user != null && userOwner)
				await Task.Run(new Action(user.Dispose));

			base.Dispose(true);
		} // proc DisposeAsync

		#endregion

		#region -- User ---------------------------------------------------------------

		/// <summary>Change the current user on the context, to a server user. Is the given user null, the result is also null.</summary>
		public async Task AuthentificateUserAsync(IIdentity authentificateUser)
		{
			if (authentificateUser != null)
			{
				if (user != null)
					throw new InvalidOperationException();

				user = await server.AuthentificateUserAsync(authentificateUser);
				if (user == null)
					throw CreateAuthorizationException(false, String.Format("Authentification against the DES-Users failed: {0}.", authentificateUser.Name));
				userOwner = true;
			}
		} // proc AuthentificateUser

		/// <summary>Change the current user.</summary>
		/// <param name="user"></param>
		public void SetUser(IDEAuthentificatedUser user)
			=> this.user = user;

		/// <summary>Get the current user.</summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		public T GetUser<T>()
			where T : class
		{
			if (user == null)
				throw CreateAuthorizationException(false, "Authorization expected.");

			if (user is T r)
				return r;

			throw new NotImplementedException(String.Format("User class does not implement '{0}.", typeof(T).FullName));
		} // func GetUser

		#endregion

		#region -- Security -----------------------------------------------------------

		/// <summary>Check for the given token, if the user can access it.</summary>
		/// <param name="securityToken">Security token.</param>
		public void DemandToken(string securityToken)
		{
			if (String.IsNullOrEmpty(securityToken))
				return; // no security token -> public

			// is this token allowed in the current context
			if (allowGroups == restrictAllGroups
				|| allowGroups != allowAllGroups && !Array.Exists(allowGroups, c => String.Compare(c, securityToken, StringComparison.OrdinalIgnoreCase) == 0))
				throw CreateAuthorizationException(true, "Access is restricted.");

			// is http authentification active
			if (!useAuthentification)
				return;

			if (!TryDemandToken(securityToken))
				throw CreateAuthorizationException(false, String.Format("User {0} is not authorized to access token '{1}'.", User == null ? "Anonymous" : User.Identity.Name, securityToken));
		} // proc DemandToken

		/// <summary>Check for the given token, if the user can access it.</summary>
		/// <param name="securityToken">Security token.</param>
		/// <returns><c>true</c>, if the token is granted.</returns>
		public bool TryDemandToken(string securityToken)
		{
			if (!useAuthentification)
				return true;
			if (String.IsNullOrEmpty(securityToken))
				return true;

			return User != null && User.IsInRole(securityToken);
		} // proc TryDemandToken

		/// <summary>Create an authorization exception in the current context.</summary>
		/// <param name="isTokenRestricted"><c>true</c>, the token is not allowed under the current context. <c>false</c>, the user has no right on the token.</param>
		/// <param name="message">Message of this exception</param>
		/// <returns></returns>
		public virtual Exception CreateAuthorizationException(bool isTokenRestricted, string message)
			=> new ArgumentException(message);

		Exception IDECommonScope.CreateAuthorizationException(string message)
			=> CreateAuthorizationException(false, message);

		#endregion

		#region -- GetService, Register -----------------------------------------------

		/// <summary>Get service from the current scope.</summary>
		/// <param name="serviceType"></param>
		/// <returns></returns>
		public override object GetService(Type serviceType)
		{
			// first self test
			var r = base.GetService(serviceType);
			if (r != null)
				return r;

			if (serviceType == typeof(IDEAuthentificatedUser))
				return user;

			// second check user service
			r = user?.GetService(serviceType);
			if (r != null)
				return r;

			// check for an registered service
			lock (services)
			{
				if (services.TryGetValue(serviceType, out var srv))
					r = srv.Service;
			}
			if (r != null)
				return r;

			// third down to config node
			return sp?.GetService(serviceType);
		} // func GetService

		private void RegisterService(Type serviceType, ServiceDescriptor descriptor)
		{
			lock (services)
			{
				if (services.ContainsKey(serviceType))
					throw new ArgumentException($"There is already a service '{serviceType.Name}'");
				services.Add(serviceType, descriptor);
			}
		} // proc RegisterService

		/// <summary>Register service in the current scope.</summary>
		/// <param name="serviceType"></param>
		/// <param name="factory"></param>
		public void RegisterService(Type serviceType, Func<object> factory)
			=> RegisterService(serviceType, new ServiceDescriptor(factory));

		/// <summary>Register service in the current scope.</summary>
		/// <param name="serviceType"></param>
		/// <param name="service"></param>
		public void RegisterService(Type serviceType, object service)
			=> RegisterService(serviceType, new ServiceDescriptor(service));

		/// <summary>Register service in the current scope.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="factory"></param>
		public void RegisterService<T>(Func<T> factory) where T : class
			=> RegisterService(typeof(T), new ServiceDescriptor(factory));

		/// <summary>Register service in the current scope.</summary>
		/// <param name="serviceType"></param>
		public bool RemoveService(Type serviceType)
			=> services.Remove(serviceType);

		#endregion

		#region -- Commit, Rollback, Auto Dispose -------------------------------------

		private async Task RunActionsAsync(IEnumerable<Func<Task>> actions, bool rollback)
		{
			var transactions = new List<Func<Task>>();

			// registered commit actions
			lock (actions)
				transactions.AddRange(actions);

			// find service commit actions
			lock (services)
			{
				foreach (var cur in services.Values.Where(c => c.IsValueCreated))
				{
					switch (cur.Service)
					{
						case IDbTransaction trans:
							transactions.Add(
								() => Task.Run(() =>
								  {
									  if (rollback)
										  trans.Rollback();
									  else
										  trans.Commit();
								  })
							);
							break;
						case IDETransaction trans:
							transactions.Add(() => Task.Run(() =>
							  {
								  if (rollback)
									  trans.Rollback();
								  else
									  trans.Commit();
							  })
							);
							break;
						case IDETransactionAsync trans:
							transactions.Add(rollback ? new Func<Task>(trans.RollbackAsync) : new Func<Task>(trans.CommitAsync));
							break;
					}
				}
			}

			// execute actions
			var e = rollback ? Enumerable.Reverse(transactions) : transactions;
			foreach (var c in e)
				await c();
		} // func RunActionsAsync

		/// <summary></summary>
		/// <param name="action"></param>
		public void RegisterCommitAction(Action action)
			=> RegisterCommitAction(() => Task.Run(action));

		/// <summary></summary>
		/// <param name="action"></param>
		public void RegisterCommitAction(Func<Task> action)
		{
			lock (commitActions)
				commitActions.Add(action);
		} // func RegisterCommitAction

		/// <summary></summary>
		/// <param name="action"></param>
		public void RegisterRollbackAction(Action action)
			=> RegisterRollbackAction(() => Task.Run(action));

		/// <summary></summary>
		/// <param name="action"></param>
		public void RegisterRollbackAction(Func<Task> action)
		{
			lock (rollbackActions)
				rollbackActions.Add(action);
		} // func RegisterRollbackAction

		/// <summary></summary>
		/// <param name="obj"></param>
		public void RegisterDispose(IDisposable obj)
		{
			lock (autoDispose)
			{
				autoDispose.Add(obj);

				switch(obj)
				{
					case IDETransaction trans:
						RegisterCommitAction(new Action(trans.Commit));
						RegisterRollbackAction(new Action(trans.Rollback));
						break;
					case IDETransactionAsync transAsync:
						RegisterCommitAction(new Func<Task>(transAsync.CommitAsync));
						RegisterRollbackAction(new Func<Task>(transAsync.RollbackAsync));
						break;
					case IDbTransaction transDb:
						RegisterCommitAction(new Action(transDb.Commit));
						RegisterRollbackAction(new Action(transDb.Rollback));
						break;
				}
			}
		} // proc RegisterDispose

		/// <summary></summary>
		/// <returns></returns>
		public Task CommitAsync()
		{
			isCommitted = true;
			return RunActionsAsync(commitActions, false);
		} // proc Commit

		/// <summary></summary>
		/// <returns></returns>
		public Task RollbackAsync()
		{
			isCommitted = false;
			return RunActionsAsync(rollbackActions, true);
		} // proc Rollback

		#endregion

		#region -- Global Store -------------------------------------------------------

		/// <summary></summary>
		/// <param name="ns"></param>
		/// <param name="variable"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool TryGetGlobal(object ns, object variable, out object value)
			=> globals.TryGetValue(new GlobalKey(ns, variable), out value);

		/// <summary></summary>
		/// <param name="ns"></param>
		/// <param name="variable"></param>
		/// <param name="value"></param>
		public void SetGlobal(object ns, object variable, object value)
			=> globals[new GlobalKey(ns, variable)] = value;

		#endregion

		/// <summary></summary>
		/// <param name="name"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public virtual bool TryGetProperty(string name, out object value)
		{
			value = null;
			return false;
		} // func TryGetProperty

		/// <summary>Server</summary>
		public IDEServer Server => server;
		/// <summary>Current user</summary>
		public IDEAuthentificatedUser User => user;
		/// <summary>Current culture info</summary>
		public virtual CultureInfo CultureInfo => CultureInfo.CurrentUICulture;
		
		/// <summary>Is the scope committed (<c>null</c> if the scope is active)</summary>
		public bool? IsCommited => isCommitted;
	} // class DETransactionContext

	#endregion

	#region -- class DEModulInfoAttribute ---------------------------------------------

	/// <summary>Marks a assembly as Data Exchange Server modul.</summary>
	[AttributeUsage(AttributeTargets.Assembly)]
	public sealed class DEModulInfoAttribute : Attribute
	{
		/// <summary></summary>
		/// <param name="imageResource"></param>
		/// <param name="type"></param>
		public DEModulInfoAttribute(string imageResource, Type type = null)
		{
			Image = type != null ? type.Namespace + "." + imageResource : imageResource;
		} // ctor

		/// <summary>Image name</summary>
		public string Image { get; }
	} // class DEModulInfoAttribute

	#endregion

	#region -- DEServerBaseLog --------------------------------------------------------

	/// <summary>Ermöglicht den Zugriff auf die Basis-Logdatei</summary>
	public class DEServerBaseLog { }

	#endregion
}
