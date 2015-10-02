using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- enum DEConfigItemState ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Status, des Konfigurationseintrages.</summary>
	public enum DEConfigItemState
	{
		/// <summary>Item wurde gerade frisch erzeugt. Der Ctor ist durchlaufen.</summary>
		Initializing,
		/// <summary>Konfiguration des Items wird geladen. Die Konfiguration ist für den Zugriff gesperrt, OnBeforeRefreshConfiguration wurde ausgeführt.</summary>
		Loading,
		/// <summary>Konfigurationsitem wurde nicht geladen, Konfiguration ist fehlerhaft</summary>
		Invalid,
		/// <summary>Eintrag wurde erfolgreich Initialisiert, wird vor OnAfterRefreshConfiguration gesetzt</summary>
		Initialized,
		/// <summary>Das Objekt ist zerstört.</summary>
		Disposed
	} // enum DEConfigItemState

	#endregion

	#region -- class DEConfigurationException -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class DEConfigurationException : Exception
	{
		private string sourceUri;
		private int lineNumber;
		private int linePosition;

		public DEConfigurationException(XObject x, string message, Exception innerException = null)
			: base(message, innerException)
		{
			this.sourceUri = x.BaseUri;

			var lineInfo = (IXmlLineInfo)x;
			if (lineInfo.HasLineInfo())
			{
				this.lineNumber = lineInfo.LineNumber;
				this.linePosition = lineInfo.LinePosition;
			}
			else
			{
				this.lineNumber = -1;
				this.linePosition = -1;
			}
		} // ctor

		public DEConfigurationException(XmlSchemaObject x, string message, Exception innerException = null)
		{
			this.sourceUri = x.SourceUri;
			this.lineNumber = x.LineNumber;
			this.linePosition = x.LinePosition;
		} // ctor

		/// <summary>Position an der der Fehler entdeckt wurde</summary>
		public string PositionText
		{
			get
			{
				var sb = new StringBuilder();
				if (String.IsNullOrEmpty(sourceUri))
					sb.Append("<unknown>");
				else
					sb.Append(sourceUri);

				if (lineNumber >= 0)
				{
					sb.Append(" (")
						.Append(lineNumber.ToString("N0"));
					if (linePosition >= 0)
					{
						sb.Append(',')
							.Append(linePosition.ToString("N0"));
					}
					sb.Append(')');
				}

				return sb.ToString();
			}
		} // prop ConfigFileName

		/// <summary></summary>
		public string SourceUri => sourceUri;
		/// <summary></summary>
		public int LineNumber => lineNumber;
		/// <summary></summary>
		public int LinePosition => linePosition;
	} // class DEConfigurationException

	#endregion

	#region -- class DEConfigHttpAction -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Markiert eine Methode als Action, die von Http-Prozessor verarbeit
	/// werden kann.</summary>
	[AttributeUsage(AttributeTargets.Method)]
	public sealed class DEConfigHttpActionAttribute : Attribute
	{
		/// <summary>Erzeugt die Aktion mit den angegebenen Namen.</summary>
		/// <param name="actionName">Name der Aktion</param>
		public DEConfigHttpActionAttribute(string actionName)
		{
			this.ActionName = actionName;
		} // ctor

		/// <summary>Name der Aktion</summary>
		public string ActionName { get; }
		/// <summary>Gibt den SecurityToken, den der Nutzer besitzen muss, zurück, um die Aktion auszuführen.</summary>
		public string SecurityToken { get; set; }
		/// <summary>Die Aktion erhält einen Parameter für Respone-Objekt und muss sich um alles alleine kümmern.</summary>
		public bool IsNativeCall { get; set; }
		/// <summary>Sollen Exceptions in eine gültige Rückgabe umgewandelt werden (default: false).</summary>
		public bool IsSafeCall { get; set; }
	} // class DEConfigHttpActionAttribute

	#endregion

	#region -- interface IDEConfigItem --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Konfigurationseinträge müssen mindestens diese Eigenschaften besitzen.
	/// Diese Schnittstelle kann nicht implementiert werden.</summary>
	public interface IDEConfigItem : IServiceProvider
	{
		void WalkChildren<T>(Action<T> action, bool recursive = false, bool @unsafe = false) where T : class;
		bool FirstChildren<T>(Predicate<T> predicate, Action<T> action = null, bool recursive = false, bool @unsafe = false) where T : class;

		string Name { get; }

		IDEServer Server { get; }
	} // interface IDEConfigItem

	#endregion

	#region -- interface IDEConfigLoading -----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Zugriff auf den Ladetoken der Konfigurationseinstellungen.</summary>
	public interface IDEConfigLoading : IDisposable
	{
		/// <summary>Erzeugt ein neuen Knoten von einem Konfigurationsknoten.</summary>
		/// <param name="type"></param>
		/// <param name="config"></param>
		/// <returns></returns>
		T RegisterSubItem<T>(XElement config)
			where T : DEConfigItem;

		/// <summary>Bildet den Dateinamen relative zur Konfiguration.</summary>
		/// <param name="fileName">Relativer Dateiname, oder <c>null</c> für das Konfigurationsverzeichnis.</param>
		/// <returns>Vollständiger Pfad</returns>
		string GetFullFileName(string fileName = null);

		/// <summary>Zugriff auf die aktuelle Konfiguration. Kann <c>null</c> sein.</summary>
		XElement ConfigOld { get; }
		/// <summary>Zugriff auf die neue Konfiguration, die eingestellt werden soll.</summary>
		XElement ConfigNew { get; }
		/// <summary>Wurde die Konfiguration geändert.</summary>
		bool IsConfigurationChanged { get; }
		/// <summary>Wurde die Konfiguration erfolgreich geladen</summary>
		bool IsLoadedSuccessful { get; }
		
		/// <summary>Daten</summary>
		PropertyDictionary Tags { get; }

		/// <summary>Von wann ist die Konfigurationsdatei</summary>
		DateTime LastWrite { get; }
	} // interface IDEConfigLoading

	#endregion

	#region -- class DEConfigItem -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Basis jedes Elementes, welches über die Konfiguration geladen werden 
	/// kann.</summary>
	/// <remarks><para>Diese Klasse bildet die Basisklasse für alle Module und Elemente im
	/// DES. Sie stellt den Zurgiff auf die <see href="config.htm">Konfiguration</see>
	/// und bietet den Zugriff auf eine Log-Datei.</para>
	/// <para>Standardmäßig werden die nachfahren dieser Klasse aus der Konfiguration heraus
	/// erzeugt. Die Konfigurationsdaten, können dann über die zu überschreibende Methode <see cref="RefreshConfiguration"/>
	/// abgeholt werden. Standardmäßig wird der DisplayName aus der Konfiguration gelesen.</para>
	// Events:
	//   register
	//   unregister
	//   config
	public partial class DEConfigItem : LuaTable, IDEConfigItem, IComparable<DEConfigItem>, IDisposable
	{
		private const string csLuaActions = "Actions";              // table für alle Aktionen, die in dem Script enthalten sind.
		private const string csLuaDispose = "Dispose";              // table mit Methoden, die aufgerufen werden, der Knoten zerstört wird.
		private const string csLuaConfiguration = "Configuration";  // table mit Methoden, die aufgerufen werden, wenn sich die Konfiguration geändert hat.

		#region -- class DEConfigLoading --------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		internal class DEConfigLoading : IDEConfigLoading, IDisposable
		{
			private DEConfigItem item;
			private List<DEConfigLoading> subLoads = new List<DEConfigLoading>();

			private XElement configNew;
			private XElement configOld;
			private bool configurationChanged;
			private DateTime lastWrite;

			private IDisposable itemLock = null;
			private Exception loadException = null;
			private PropertyDictionary data = null;

			#region -- Ctor/Dtor ------------------------------------------------------------

			internal DEConfigLoading(DEConfigItem item, XElement configNew, DateTime lastWrite)
			{
				this.item = item;
				this.configNew = configNew;
				this.configOld = item.Config;
				this.lastWrite = lastWrite;

				// Prüfe die Konfiguration für diesen Knoten
				item.ValidateConfig(this.configNew);

				// Ordne die Elemente den SubItems zu
				using (item.EnterReadLock())
				{
					var nodes = configNew.Elements().ToArray();
					foreach (XElement xmlCur in nodes)
					{
						var curItem = item.subItems.Find(c => String.Compare(c.Name, item.GetConfigItemName(xmlCur), true) == 0);
						if (curItem != null)
						{
							subLoads.Add(new DEConfigLoading(curItem, xmlCur, lastWrite));
							xmlCur.Remove();
						}
					}
				}

				// Vergleiche die Konfigurationen
				configurationChanged = !Procs.CompareNode(configNew, configOld);
			} // ctor

			~DEConfigLoading()
			{
				Dispose(false);
			} // dtor

			public void Dispose()
			{
				GC.SuppressFinalize(this);
				Dispose(true);
			} // proc Dipose

			private void Dispose(bool disposing)
			{
				if (disposing)
				{
					subLoads.ForEach(c => c.Dispose());
					subLoads.Clear();

					Procs.FreeAndNil(ref itemLock);
				}
			} // proc Dispose

			#endregion

			#region -- BeginReadConfiguration, EndReadConfiguration -------------------------

			/// <summary>Wird aufgerufen, wenn die Konfiguration gestartet wird.</summary>
			/// <param name="config">Informationen zur Konfigurationsänderung.</param>
			/// <returns></returns>
			internal void BeginReadConfiguration()
			{
				itemLock = item.EnterWriteLock();
				try
				{
					// Lade die Daten des aktuellen Knoten
					if (IsConfigurationChanged)
					{
						// Setze den Status auf Laden
						item.state = DEConfigItemState.Loading;

						item.Log.LogMsg(LogMsgType.Information, "{0}: Konfiguration wird geladen...", item.Name);
						Debug.Print("BEGIN Lade [{0}]", item.Name);
						item.OnBeginReadConfiguration(this);
						Debug.Print("END Lade [{0}]", item.Name);
					}

					// Lösche nicht bearbeitete Knoten
					for (int i = item.subItems.Count - 1; i >= 0; i--)
					{
						DEConfigItem curI = item.subItems[i];
						if (!subLoads.Exists(c => Object.ReferenceEquals(c.item, curI)))
						{
							item.UnregisterSubItem(i, curI);
						}
					}
				}
				catch (Exception e)
				{
					item.Log.LogMsg(LogMsgType.Error, "{0}: Konfiguration nicht geladen.\n\n{1}", item.Name, e.GetMessageString());
					item.state = DEConfigItemState.Invalid;
					this.LoadException = e;
				}
			} // proc BeginReadConfiguration

			/// <summary>Abschluss der Konfiguration. Starte ggf. die Anwendungsteile.</summary>
			/// <param name="config"></param>
			/// <returns></returns>
			internal Exception EndReadConfiguration()
			{
				// Aktiviere den aktuellen Knoten
				try
				{
					item.currentConfig = configNew;
					Debug.Print("BEGIN Aktiviere [{0}]", item.Name);
					item.OnEndReadConfiguration(this);
					Debug.Print("END Aktiviere [{0}]", item.Name);
					item.Log.LogMsg(LogMsgType.Information, "{0}: Konfiguration wurde erfolgreich geladen.", item.Name);
					item.state = DEConfigItemState.Initialized;
					return null;
				}
				catch (Exception e)
				{
					item.Log.LogMsg(LogMsgType.Error, "{0}: Konfiguration nicht aktiviert.\n\n{1}", item.Name, e.GetMessageString());
					item.state = DEConfigItemState.Invalid;
					return e;
				}
			} // proc EndReadConfiguration

			public void DestroySubConfiguration()
			{
				for (int i = item.subItems.Count - 1; i >= 0; i--)
					item.UnregisterSubItem(i, item.subItems[i]);
				subLoads.Clear();
			} // proc DestroyConfiguration

			#endregion

			public T RegisterSubItem<T>(XElement config)
				where T : DEConfigItem
			{
				if (config == null)
					return null;

				// Hole den Namen des Knotens ab
				var name = item.GetConfigItemName(config);
				if (String.IsNullOrEmpty(name))
					throw new ArgumentNullException("name");

				// Erzeuge den Knoten
				T newItem = (T)Activator.CreateInstance(typeof(T), item, name);

				// Suche einen Knoten mit gleichen Namen innerhalb der Ebene
				if (subLoads.Exists(c => String.Compare(c.item.Name, name, true) == 0))
					throw new ArgumentException(String.Format("[{0}] existiert schon.", name));

				subLoads.Add(new DEConfigLoading(newItem, config, lastWrite));
				item.subItems.Add(newItem);
				item.subItems.Sort();
				// Kopiere die Annotationen, sonst gehen sie beim Remove verloren
				Procs.XCopyAnnotations(config, config);
				config.Remove();

				item.OnNewSubItem(newItem);
				return newItem;
			} // proc RegisterSubItem

			public string GetFullFileName(string fileName = null) => ProcsDE.GetFileName(configNew, fileName);

			public XElement ConfigNew { get { return configNew; } }
			public XElement ConfigOld { get { return configOld; } }
			public bool IsConfigurationChanged { get { return configurationChanged; } }

			public Exception LoadException { get { return loadException; } set { loadException = value; } }
			public bool IsLoadedSuccessful { get { return loadException == null; } }
			public DateTime LastWrite { get { return lastWrite; } }

			public PropertyDictionary Tags
			{
				get
				{
					if (data == null)
						data = new PropertyDictionary();
					return data;
				}
			} // prop Tags

			public IEnumerable<DEConfigLoading> SubLoadings { get { return subLoads; } }
		} // class DEConfigLoading

		#endregion

		/// <summary>Sicherheitsebene Serveradministrator</summary>
		public const string SecuritySys = "desSys";

		public const string AttachedScriptsListId = "tw_attached_scripts";
		public const string ActionsListId = "tw_actions";
		public const string PropertiesListId = "tw_properties";
		public const string LogLineListId = "tw_lines";

		public const string ConfigurationCategory = "Konfiguration";

		private IServiceProvider sp = null;       // ServiceProvider mit dem der Eintrag erzeugt wurde
		private Lazy<IDEServer> server;						// Zugriff aud das Interne Interface des Servers
		private string name = null;               // Bezeichnung des Elements
		private string securityToken = null;      // Gibt den SecurityToken, den der Nutzer besitzen muss, zurück, um in den Knoten wechseln zu können

		private Lazy<LoggerProxy> log;						// Zugriff die eigene oder die LogDatei des Parents

		private DEList<ILuaAttachedScript> scripts;					// Scripte die auf diesem Knoten ausgeführt werden
		private ConfigActionDictionary actions;							// Aktions, die an diesem Knoten existieren
		private DEList<IDEConfigItemProperty> properties;		// Eigenschaften, dieses Knotens

		private ReaderWriterLockSlim lockConfig;		// Lese/Schreibsperre für die Konfiguration
		private List<DEConfigItem> subItems;				// Konfigurationseinträge unter diesem Knoten
		private XElement currentConfig = null;			// Zugriff auf den Konfigurationsknoten, darf nur lesend zugegriffen werden, und es dürfen keine Referenzen gespeichert werden
		private DEConfigItemState state;            // Aktueller Status des Konfigurationsknotens

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEConfigItem(IServiceProvider sp, string name)
		{
			if (sp == null)
				throw new ArgumentNullException("sp");

			this.lockConfig = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
			this.subItems = new List<DEConfigItem>();

			this.server = new Lazy<IDEServer>(() => this.GetService<IDEServer>(true), true);
			this.log = new Lazy<LoggerProxy>(this.LogProxy, true);

			this.sp = sp;
			this.name = name;
			this.state = DEConfigItemState.Initializing;

			this.scripts = new DEList<ILuaAttachedScript>(this, AttachedScriptsListId, "Zugeordnete Skripte");
			this.actions = new ConfigActionDictionary(this);
			PublishItem(this.properties = new DEList<IDEConfigItemProperty>(this, PropertiesListId, "Eigenschaften"));

			InitTypeProperties();

			Debug.Print("CREATE [{0}]", name);
		} // ctor

		~DEConfigItem()
		{
			Dispose(false);
		} // dtor

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool lDisposing)
		{
			if (state == DEConfigItemState.Disposed) // wurde schon gelöscht
				return;

			Debug.Print("BEGIN DISPOSE [{0}]", name);
			using (EnterReadLock(true))
			{
				using (EnterWriteLock())
					state = DEConfigItemState.Disposed;

				// Gibt es Objekte die freigeben werden sollen
				var disposeList = GetMemberValue(csLuaDispose, false, true) as LuaTable;
				if (disposeList != null)
				{
					foreach (var c in disposeList.Values)
						try
						{
							// Führe eine Methode aus
							if (c.Key is string && (c.Value is Delegate || c.Value is ILuaMethod))
								Lua.RtInvoke(c.Value);

							// Rufe Dispose des Objektes
							var d = c.Value as IDisposable;
							if (d != null)
								d.Dispose();
						}
						catch (Exception e) { Log.Warn(e); }
				}
				// Gib die darunterliegenden Elemente frei
				if (subItems != null)
				{
					while (subItems.Count > 0)
					{
						var c = subItems[subItems.Count - 1];
						c.Dispose();
						using (EnterWriteLock())
							subItems.Remove(c);
					}
				}

				// Gib die zugeordneten Skripte frei
				lock (scripts)
				{
					foreach (var cur in scripts)
						cur.Dispose();
					scripts.Clear();
				}
			}

			Procs.FreeAndNil(ref scripts);
			Procs.FreeAndNil(ref properties);
			Procs.FreeAndNil(ref actions);

			Procs.FreeAndNil(ref lockConfig);
			Debug.Print("END DISPOSE [{0}]", name);
		} // proc Disposing

		public int CompareTo(DEConfigItem other)
		{
			return String.Compare(name, other.name, true);
		} // func CompareTo

		#endregion

		#region -- Locking ----------------------------------------------------------------

		public IDisposable EnterReadLock(bool upgradeable = false)
		{
			if (upgradeable)
			{
				lockConfig.EnterUpgradeableReadLock();
				return new DisposableScope(new Action(lockConfig.ExitUpgradeableReadLock));
			}
			else
			{
				lockConfig.EnterReadLock();
				return new DisposableScope(new Action(lockConfig.ExitReadLock));
			}
		} // func EnterReadLock

		private IDisposable EnterWriteLock()
		{
			lockConfig.EnterWriteLock();
			return new DisposableScope(lockConfig.ExitWriteLock);
		} // func EnterWriteLock

		#endregion

		#region -- Configuration ----------------------------------------------------------

		/// <summary>Normalerweise wird der Name der Konfiguration aus der Konfiguration ausgelesen.</summary>
		/// <param name="element">Konfigurationselement</param>
		/// <returns>Name des Knotens</returns>
		protected virtual string GetConfigItemName(XElement element)
		{
			return element.GetAttribute("name", String.Empty);
		} // func GetConfigItemName

		/// <summary>Ermöglicht es die Konfigurationsdatei, bevor Sie bearbeitet wird zu manipulieren.</summary>
		/// <param name="config"></param>
		/// <returns></returns>
		protected virtual void ValidateConfig(XElement config)
		{
		} // proc ValidateConfig

		/// <summary>Liest die neue Konfiguration ein und meldet die neuen Knoten an</summary>
		/// <param name="config"></param>
		protected virtual void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			// Vergleiche die Skripte
			var scriptList = config.ConfigNew.GetAttribute("script", null);

			var loadIds = new List<string>();
			var removeIds = new List<string>(from s in scripts select s.ScriptId);
			if (!String.IsNullOrEmpty(scriptList))
			{
				foreach (var cur in scriptList.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
				{
					var index = removeIds.FindIndex(c => String.Compare(c, cur, StringComparison.OrdinalIgnoreCase) == 0);
					if (index != -1)
						removeIds.RemoveAt(index);
					else
						loadIds.Add(cur);
				}
			}

			config.Tags.SetProperty("ScriptLoad", loadIds.ToArray());
			config.Tags.SetProperty("ScriptRemove", removeIds.ToArray());

			// Update des Sicherheitstokens
			securityToken = config.ConfigNew.GetAttribute("security", null);
			
			// Lade Http-Erweiterungen
			foreach (XElement current in config.ConfigNew.Elements().ToArray())
			{
				if (current.Name == DEConfigurationConstants.xnFiles)
					config.RegisterSubItem<HttpFileWorker>(current);
				else if (current.Name == DEConfigurationConstants.xnResources)
					config.RegisterSubItem<HttpResourceWorker>(current);
			}
		} // proc OnBeginReadConfiguration

		/// <summary>Wird aufgerufen, bevor die Konfiguration aktiviert wird. Diese Funktion sollte keine Exceptions mehr auslösen.</summary>
		/// <param name="config"></param>
		protected virtual void OnEndReadConfiguration(IDEConfigLoading config)
		{
			lock (scripts)
			{
				var engine = this.GetService<IDELuaEngine>(false);

				var load = config.Tags.GetProperty("ScriptLoad", Procs.EmptyStringArray);
				var remove = config.Tags.GetProperty("ScriptRemove", Procs.EmptyStringArray);

				// Erzeuge die Skriptverbindungen
				if (engine == null)
					Log.Warn("Script engine is not running, there will be no dynamic parts available.");
				else
				{
					foreach (string cur in load)
					{
						var attachedScript = engine.AttachScript(cur, this, true);

						// Registriere Script-Update
						attachedScript.ScriptCompiled += AttachedScriptCompiled;

						scripts.Add(attachedScript);
					}
				}

				// Lösche Scriptverbindungen
				foreach (var cur in remove)
				{
					var index = scripts.FindIndex(c => String.Compare(c.ScriptId, cur, StringComparison.OrdinalIgnoreCase) == 0);
					if (index != -1)
					{
						scripts[index].Dispose();
						scripts.RemoveAt(index);
					}
				}

				// Aktualisiere die Skripte, falls nötig
				foreach (var cur in scripts)
					if (cur.NeedToRun && cur.IsCompiled)
						cur.Run();
			}

			// Führe die Skript-Funktion aus
			CallTableMethods(csLuaConfiguration, config);
		} // proc OnEndReadConfiguration

		/// <summary>Wird aufgerufen, wenn ein neues Element angemeldet wurde.</summary>
		/// <param name="item"></param>
		protected virtual void OnNewSubItem(DEConfigItem item)
		{
		} // proc OnNewSubItem

		protected virtual void OnDisposeSubItem(DEConfigItem item)
		{
		} // proc OnDisposeSubItem

		private void UnregisterSubItem(int iIndex, DEConfigItem item)
		{
			subItems.RemoveAt(iIndex);

			// Zeige die Änderung im Protokoll
			Log.Info("{0} wurde entfernt.", item.Name);
			Debug.Print("DELETE [{0}]", item.Name);

			try { OnDisposeSubItem(item); }
			catch { }

			// Zerstöre den Knoten
			try
			{
				item.Dispose();
			}
			catch (Exception e)
			{
				Log.LogMsg(LogMsgType.Error, "Konfiguration nicht entladen." + Environment.NewLine + Environment.NewLine + e.GetMessageString());
			}
		} // proc UnregisterSubItem

		public void WalkChildren<T>(Action<T> action, bool lRecursive = false, bool lUnsafe = false)
			where T : class
		{
			using (lUnsafe ? null : EnterReadLock())
				foreach (DEConfigItem cur in subItems)
				{
					T r = cur as T;
					if (r != null)
						action(r);

					if (lRecursive)
						cur.WalkChildren<T>(action, true, lUnsafe);
				}
		} // func WalkChildren

		public bool FirstChildren<T>(Predicate<T> predicate, Action<T> action = null, bool lRecursive = false, bool lUnsafe = false)
			where T : class
		{
			using (lUnsafe ? null : EnterReadLock())
				foreach (DEConfigItem cur in subItems)
				{
					T r = cur as T;
					if (r != null && predicate(r))
					{
						if (action != null)
							action(r);
						return true;
					}

					if (lRecursive && cur.FirstChildren<T>(predicate, action, true))
						return true;
				}
			return false;
		} // func FirstChildren

		public DEConfigItem UnsafeFind(string sName)
		{
			return subItems.Find(c => String.Compare(c.Name, sName, StringComparison.OrdinalIgnoreCase) == 0);
		} // func UnsafeFind

		private StringBuilder GetNodeUri(StringBuilder sb)
		{
			DEConfigItem parent = sp as DEConfigItem;
			if (parent == null)
				sb.Append("/");
			else
				parent.GetNodeUri(sb).Append(Name).Append('/');
			return sb;
		} // func GetNodeUri

		/// <summary>mit / getrennnter Pfad</summary>
		public string ConfigPath { get { return GetNodeUri(new StringBuilder()).ToString(); } }

		/// <summary>Zugriff auf die aktuelle Konfiguration</summary>
		public XElement Config { get { return currentConfig; } }

		#endregion

		#region -- IServiceProvider Member ------------------------------------------------

		public virtual object GetService(Type serviceType)
		{
			if (serviceType.IsAssignableFrom(GetType()))
				return this;
			else if (sp != null)
				return sp.GetService(serviceType);
			else
				return null;
		} // func GetService

		#endregion
		
		#region -- Http -------------------------------------------------------------------

		[
		DEConfigHttpAction("config", SecurityToken = SecuritySys),
		Description("Gibt die Einstellungen der aktuellen Knotens zurück.")
		]
		private XElement HttpConfigAction()
		{
			return Config;
		} // func HttpConfigAction

		#endregion

		#region -- Process Request/Action -------------------------------------------------

		internal void UnsafeInvokeHttpAction(string sAction, HttpResponse r)
		{
			var returnValue = InvokeAction(sAction, r);

			// Erzeuge die Rückgabe
			if (returnValue == DBNull.Value) // NativeCall
				return;
			else if (r.IsResponseSended)
				if (returnValue == null)
					return;
				else
					throw new ArgumentException("Rückgabewert nicht verarbeitet.");
			else if (returnValue == null)
				returnValue = CreateDefaultXmlReturn(true, null);
			else if (returnValue is XElement)
			{
				XElement x = (XElement)returnValue;
				if (x.Attribute("status") == null)
					x.SetAttributeValue("status", "ok");
			}
			else if (returnValue is LuaResult)
			{
				LuaResult result = (LuaResult)returnValue;
				XElement x = CreateDefaultXmlReturn(true, result[1] as string);

				if (result[0] is LuaTable)
					ConvertLuaTable(x, (LuaTable)result[0]);
				else if (result[0] != null)
					x.Add(result[0]);

				returnValue = x;
			}
			else if (returnValue is LuaTable)
			{
				returnValue = ConvertLuaTable(CreateDefaultXmlReturn(true, null), (LuaTable)returnValue);
			}

			// Schreibe die Rückgabe
			r.WriteObject(returnValue);

			// Gib die Rückgabe frei
			if (returnValue is IDisposable)
				((IDisposable)returnValue).Dispose();
		} // proc UnsafeInvokeHttpAction

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="sLocalPath"></param>
		/// <returns></returns>
		internal bool UnsafeProcessRequest(HttpResponse r)
		{
			foreach (var w in from c in this.UnsafeChildren
												let cHttp = c as HttpWorker
												where cHttp != null && r.ExistsPath(cHttp.VirtualRoot)
												orderby cHttp.Priority descending
												select cHttp)
			{
				// Führe den Request aus
				using (w.EnterReadLock())
				{
					r.PushPath(w, w.VirtualRoot);
					try
					{
						if (w.Request(r))
							return true; // Alles I/O
					}
					finally
					{
						r.PopPath();
					}
				}
			}
			return OnProcessRequest(r);
		} // func UnsafeProcessRequest

		/// <summary>Wird aufgerufen, wenn eine Http-Anfrage am Knoten verarbeitet werden soll.</summary>
		/// <param name="r">Http-Response</param>
		/// <returns><c>true</c>, wenn die Anfrage beantwortet wurde.</returns>
		protected virtual bool OnProcessRequest(HttpResponse r)
		{
			return false;
		} // func OnProcessRequest

		private static XElement CreateDefaultXmlReturn(bool lState, string sText)
		{
			XElement x = new XElement("return", new XAttribute("status", lState ? "ok" : "error"));
			if (sText != null)
				x.SetAttributeValue("text", sText);
			return x;
		} // func CreateDefaultXmlReturn

		private static XElement ConvertLuaTable(XElement x, LuaTable t)
		{
			foreach (var c in t)
				if (c.Key is string)
				{
					if (c.Value is LuaTable)
						x.Add(ConvertLuaTable(new XElement((string)c.Key), (LuaTable)c.Value));
					else
						x.Add(new XElement((string)c.Key, c.Value.ToString()));
				}
				else if (c.Value is LuaTable)
					ConvertLuaTable(x, (LuaTable)c.Value);
				else
					x.Add(c.Value.ToString());

			return x;
		}  // func ConvertLuaTable

		#endregion

		#region -- Interface for Lua ------------------------------------------------------

		protected void CallTableMethods(string sTableName, params object[] args)
		{
			var table = GetMemberValue(sTableName, false, true) as LuaTable;
			if (table == null)
				return;

			foreach (var c in table.Members)
			{
				try
				{
					Lua.RtInvoke(c.Value, args);
				}
				catch (Exception e)
				{
					Log.Except(String.Format("Failed to call {0}.{1}.", sTableName, c.Key), e);
				}
			}
		} // proc CallTableMethods

		private bool CheckKnownTable(object key, string sTableName, ref object r)
		{
			string sKey = key as string;
			if (sKey != null && sKey == sTableName)
			{
				r = new LuaTable();
				SetMemberValue(sTableName, r, lRawSet: true);
				return true;
			}
			else
				return false;
		} // func CheckKnownTable

		protected override object OnIndex(object key)
		{
			object r = base.OnIndex(key);
			if (r == null)
			{
				if (!CheckKnownTable(key, csLuaActions, ref r) &&
					!CheckKnownTable(key, csLuaDispose, ref r) &&
					!CheckKnownTable(key, csLuaConfiguration, ref r))
				{
					LuaTable t = sp as LuaTable;
					if (t != null)
						r = t[key];
				}
			}
			return r;
		} // func OnIndex

		#endregion

		/// <summary>Setzt ein neues Ereignis.</summary>
		/// <param name="sEvent">Bezeichnung des Ereignisses.</param>
		/// <param name="sIndex">Optionaler Index für das Ereignis. Falls mehrere Elemente zu diesem Ereignis zugeordnet werden können.</param>
		/// <param name="values">Werte die zu dieser Kombination gesetzt werden.</param>
		/// <remarks>Ereignisse werden nicht zwingend sofort gemeldet, sondern können auch gepollt werden.
		/// Deswegen sollte bei den <c>values</c> darauf geachtet werden, dass sie überschreibbar gestaltet 
		/// werden.</remarks>
		protected void FireEvent(string sEvent, string sIndex = null, XElement values = null)
		{
			Server.AppendNewEvent(this, sEvent, sIndex, values);
		} // proc FireEvent

		/// <summary>Gibt den internen Namen zurück (muss nicht veröffentlicht werden).</summary>
		public virtual string Name { get { return name; } }
		/// <summary>Anzeigename des Elements für den LogViewer (muss nicht veröffentlicht werden).</summary>
		public virtual string DisplayName { get { return Config.GetAttribute("displayname", Name); } }
		/// <summary>Gibt den SecurityToken, den der Nutzer besitzen muss, zurück, um in den Knoten wechseln zu können.</summary>
		[
		PropertyName("tw_core_security"),
		DisplayName("SecurityToken"),
		Description("Rechte des Knotens."),
		Category(ConfigurationCategory)
		]
		public virtual string SecurityToken { get { return securityToken; } }

		/// <summary>Gibt ein Symbol für den Knoten zurück.</summary>
		public virtual string Icon { get { return Config.GetAttribute("icon", "/images/config.png"); } }

		/// <summary>Zugriff auf die Konfigurationsdatei.</summary>
		[LuaMember("Log")]
		public LoggerProxy Log => log.Value;

		/// <summary>Zugriff auf den DEServer</summary>
		[LuaMember("Server")]
		public IDEServer Server => server.Value;

		/// <summary>Zugriff auf den Besitzer des Elements</summary>
		public object Owner => sp;
		/// <summary>In welchem Status befindet sich zur Zeit der Knoten</summary>
		public DEConfigItemState State => state;
		/// <summary>Zugriff auf die darunterliegenden Knoten.</summary>
		public IEnumerable<DEConfigItem> UnsafeChildren => subItems;

		// -- Static ----------------------------------------------------------------

		private static MethodInfo miGetParameter;

		private static PropertyInfo piInvariantCulture;
		private static MethodInfo miConvertToStringFallBack;
		private static MethodInfo miConvertFromInvariantString;

		static DEConfigItem()
		{
			miGetParameter = typeof(IDEConfigActionCaller).GetMethod("GetParameter", BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod, null, new Type[] { typeof(string), typeof(string) }, null);

			if (miGetParameter == null)
				throw new ArgumentNullException("sctor", "IDEConfigActionCaller");

			piInvariantCulture = typeof(CultureInfo).GetProperty("InvariantCulture", BindingFlags.Public | BindingFlags.Static | BindingFlags.GetProperty);
			if (piInvariantCulture == null)
				throw new ArgumentNullException("sctor", "CulturInfo");

			miConvertToStringFallBack = typeof(Convert).GetMethod("ToString", BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod, null, new Type[] { typeof(object), typeof(IFormatProvider) }, null);
			if (miConvertToStringFallBack == null)
				throw new ArgumentNullException("sctor", "Convert");

			miConvertFromInvariantString = typeof(TypeConverter).GetMethod("ConvertFromInvariantString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod, null, new Type[] { typeof(string) }, null);
			if (miConvertFromInvariantString == null)
				throw new ArgumentNullException("sctor", "TypeConverter");
		} // sctor
	} // class DEConfigItem

	#endregion
}
