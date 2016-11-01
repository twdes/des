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
			: base(message, innerException)
		{
			this.sourceUri = DEConfigItem.GetSourceUri(x);
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
		string SecurityToken { get; }

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

		/// <summary>Adds that gets executed on the end of configuration process.</summary>
		/// <param name="action"></param>
		void EndReadAction(Action action);

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
		private const string LuaActions = "Actions";              // table für alle Aktionen, die in dem Script enthalten sind.
		private const string LuaDispose = "Dispose";              // table mit Methoden, die aufgerufen werden, der Knoten zerstört wird.
		private const string LuaConfiguration = "Configuration";  // table mit Methoden, die aufgerufen werden, wenn sich die Konfiguration geändert hat.

		#region -- class DEConfigLoading --------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		internal class DEConfigLoading : IDEConfigLoading, IDisposable
		{
			private DEConfigItem item;
			private LogMessageScopeProxy log;
			private List<DEConfigLoading> subLoads = new List<DEConfigLoading>();

			private XElement configNew;
			private XElement configOld;
			private bool configurationChanged;
			private DateTime lastWrite;

			private IDisposable itemLock = null;
			private Exception loadException = null;
			private PropertyDictionary data = null;
			private LinkedList<Action> endReadConfig = new LinkedList<Action>();

			#region -- Ctor/Dtor ------------------------------------------------------------

			internal DEConfigLoading(DEConfigItem item, LogMessageScopeProxy log, XElement configNew, DateTime lastWrite)
			{
				this.item = item;
				this.log = log;
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
							subLoads.Add(new DEConfigLoading(curItem, log, xmlCur, lastWrite));
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
						log.WriteLine($"BEGIN Lade [{ item.Name}]");
						using (log.Indent())
							item.OnBeginReadConfiguration(this);
						log.WriteLine($"END Lade [{item.Name}]");
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
					log.WriteLine($"BEGIN Aktiviere [{item.Name}]");
					using (log.Indent())
					{
						// call end actions
						foreach (var a in endReadConfig)
							a();

						// call ent configuration
						item.OnEndReadConfiguration(this);
					}
					log.WriteLine($"END Aktiviere [{item.Name}]");
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

				subLoads.Add(new DEConfigLoading(newItem, log, config, lastWrite));
				item.subItems.Add(newItem);
				item.subItems.Sort();
				// Kopiere die Annotationen, sonst gehen sie beim Remove verloren
				Procs.XCopyAnnotations(config, config);
				config.Remove();

				item.OnNewSubItem(newItem);
				return newItem;
			} // proc RegisterSubItem

			public void EndReadAction(Action action)
			{
				endReadConfig.AddLast(action);
			} // proc EndReadAction

			public string GetFullFileName(string fileName = null) => ProcsDE.GetFileName(configNew, fileName);

			public XElement ConfigNew { get { return configNew; } }
			public XElement ConfigOld { get { return configOld; } }
			public bool IsConfigurationChanged { get { return configurationChanged; } }

			public Exception LoadException { get { return loadException; } set { loadException = value; } }
			public bool IsLoadedSuccessful { get { return loadException == null; } }
			public DateTime LastWrite { get { return lastWrite; } }

			public LogMessageScopeProxy Log => log;

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

			this.scripts = new DEList<ILuaAttachedScript>(this, AttachedScriptsListId, "Attached Scripts");
			this.actions = new ConfigActionDictionary(this);
			PublishItem(this.properties = new DEList<IDEConfigItemProperty>(this, PropertiesListId, "Properties"));

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

		protected virtual void Dispose(bool disposing)
		{
			if (state == DEConfigItemState.Disposed) // wurde schon gelöscht
				return;

			Debug.Print("BEGIN DISPOSE [{0}]", name);
			using (EnterReadLock(true))
			{
				using (EnterWriteLock())
					state = DEConfigItemState.Disposed;

				// Gibt es Objekte die freigeben werden sollen
				var disposeList = GetMemberValue(LuaDispose, false, true) as LuaTable;
				if (disposeList != null)
				{
					foreach (var c in disposeList.Values)
						DisposeLuaValue(c.Key, c.Value);
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

		private void DisposeLuaValue(object key, object value)
		{
			if (value == null)
				return;

			try
			{
				// Führe eine Methode aus
				if (key is string && Lua.RtInvokeable(value))
					Lua.RtInvoke(value);

				// Rufe Dispose des Objektes
				var d = value as IDisposable;
				if (d != null)
					d.Dispose();
			}
			catch (Exception e) { Log.Warn(e); }
		} // proc DisposeLuaValue

		public int CompareTo(DEConfigItem other)
			=> String.Compare(name, other.name, true);

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

			// load extensions
			foreach (var cur in config.ConfigNew.Elements().Where(c => IsSubConfigurationElement(c.Name)).ToArray())
				Server.LoadConfigExtension(config, cur, String.Empty);
		} // proc OnBeginReadConfiguration

		protected virtual bool IsSubConfigurationElement(XName xn)
		{
			if (xn.Namespace == DEConfigurationConstants.MainNamespace)
			{
				return xn == DEConfigurationConstants.xnFiles ||
					xn == DEConfigurationConstants.xnResources ||
					xn == DEConfigurationConstants.xnGroup ||
					xn == DEConfigurationConstants.xnLuaCronBatch ||
					xn == DEConfigurationConstants.xnLuaCronGroup ||
					xn == DEConfigurationConstants.xnLuaCronJob ||
					xn == DEConfigurationConstants.xnLuaProcess ||
					xn == DEConfigurationConstants.xnDirectoryListener ||
					xn == DEConfigurationConstants.xnLuaConfigItem ||
					xn == DEConfigurationConstants.xnLuaConfigLogItem ||
					xn == DEConfigurationConstants.xnHttpBasicUser ||
					xn == DEConfigurationConstants.xnHttpNtmlUser;
			}
			else
				return true;
		} // func IsSubConfigurationElement

		/// <summary>Wird aufgerufen, bevor die Konfiguration aktiviert wird. Diese Funktion sollte keine Exceptions mehr auslösen.</summary>
		/// <param name="config"></param>
		protected virtual void OnEndReadConfiguration(IDEConfigLoading config)
		{
			lock (scripts)
			{
				var engine = this.GetService<IDELuaEngine>(false);

				var load = config.Tags.GetProperty("ScriptLoad", Procs.EmptyStringArray);
				var remove = config.Tags.GetProperty("ScriptRemove", Procs.EmptyStringArray);

				// update variables
				foreach (var xVariable in config.ConfigNew.Elements(DEConfigurationConstants.xnVariable))
				{
					var name = xVariable.GetAttribute("name", String.Empty);
					var type = xVariable.GetAttribute("type", "string");
					var data = xVariable.Value;
					try
					{
						// set variable to nil
						if (data == String.Empty)
							data = null;

						// convert value to the target type
						var value = Procs.ChangeType(data, LuaType.GetType(type));

						var nameList = name.Split('.');
						var curTable = (LuaTable)this;
						for (var i = 0; i < nameList.Length - 1; i++) // create the table structure
						{
							var t = nameList[i];
							if (curTable[t] is LuaTable)
								curTable[t] = new LuaTable();
							curTable = (LuaTable)curTable[t];
						}

						// finally set the value
						curTable[nameList[nameList.Length - 1]] = value;
					}
					catch (Exception e)
					{
						Log.Warn($"Variable not set {name} : {type} = {data}", e);
					}
				}

				// Connect the scripts with the engine
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

				// Disconnect unused scripts
				foreach (var cur in remove)
				{
					var index = scripts.FindIndex(c => String.Compare(c.ScriptId, cur, StringComparison.OrdinalIgnoreCase) == 0);
					if (index != -1)
					{
						scripts[index].Dispose();
						scripts.RemoveAt(index);
					}
				}

				// Rerun scripts
				foreach (var cur in scripts.Where(c => c.NeedToRun && c.IsCompiled))
					cur.Run();
			}

			// Run script initialization routines
			CallTableMethods(LuaConfiguration, config);
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

		public T[] CollectChildren<T>(Predicate<T> p = null, bool recursive = false, bool walkUnsafe = false)
			where T : class
		{
			var children = new List<T>();

			WalkChildren<T>(c =>
			{
				if (p?.Invoke(c) ?? true)
					children.Add(c);
			}, recursive, walkUnsafe);

			return children.ToArray();
		} // func CollectChildren

		public void WalkChildren<T>(Action<T> action, bool recursive = false, bool walkUnsafe = false)
			where T : class
		{
			using (walkUnsafe ? null : EnterReadLock())
				foreach (DEConfigItem cur in subItems)
				{
					T r = cur as T;
					if (r != null)
						action(r);

					if (recursive)
						cur.WalkChildren<T>(action, true, walkUnsafe);
				}
		} // func WalkChildren

		public bool FirstChildren<T>(Predicate<T> predicate, Action<T> action = null, bool recursive = false, bool walkUnsafe = false)
			where T : class
		{
			using (walkUnsafe ? null : EnterReadLock())
				foreach (DEConfigItem cur in subItems)
				{
					T r = cur as T;
					if (r != null && predicate(r))
					{
						if (action != null)
							action(r);
						return true;
					}

					if (recursive && cur.FirstChildren<T>(predicate, action, true))
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

		#region -- Validation Helper ------------------------------------------------------

		protected static void ValidateDirectory(XElement x, XName name, bool optional = false)
		{
			ValidateDirectory(x, "@" + name.LocalName, x?.Attribute(name)?.Value, optional);
		} // proc ValidateDirectory

		protected static void ValidateDirectory(XObject x, string name, string directoryPath, bool optional = false)
		{
			try
			{
				// check for null
				if (String.IsNullOrEmpty(directoryPath))
				{
					if (optional)
						return;
					throw new ArgumentNullException(name);
				}

				// directory must exists
				var di = new DirectoryInfo(directoryPath);
				if (!di.Exists)
					throw new IOException("Directory not existing.");
			}
			catch (Exception e)
			{
				throw new DEConfigurationException(x, String.Format("Can not validate {0}.", name), e);
			}
		} // proc ValidateDirectory

		#endregion

		#region -- IServiceProvider Member ------------------------------------------------

		public virtual object GetService(Type serviceType)
		{
			if (serviceType == null)
				throw new ArgumentNullException("serviceType");

			if (serviceType.IsAssignableFrom(GetType()))
				return this;
			else if (sp != null)
				return sp.GetService(serviceType);
			else
				return null;
		} // func GetService

		#endregion

		#region -- Http -------------------------------------------------------------------

		private static void BuildAnnotatedAttribute(XElement annotatedConfig, IDEConfigurationAttribute attributeDefinition, string value)
		{
			var property = new XElement("attribute");

			property.SetAttributeValue("name", attributeDefinition.Name.LocalName);
			property.SetAttributeValue("typename", attributeDefinition.TypeName);
			property.SetAttributeValue("documentation", attributeDefinition.Documentation);

			if (value == null) // default property
			{
				property.SetAttributeValue("isDefault", true);
				if (attributeDefinition.DefaultValue != null)
					property.Add(new XText(attributeDefinition.DefaultValue));
			}
			else // value property
			{
				property.SetAttributeValue("isDefault", false);
				property.Add(new XText(Procs.ChangeType<string>(value)));
			}

			annotatedConfig.Add(property);
		} // proc BuildAnnotatedAttribute

		private XElement BuildAnnotatedConfigNode(XName name, XElement config)
		{
			var annotatedElement = new XElement("element");
			var elementDefinition = Server.Configuration[name];

			annotatedElement.SetAttributeValue("name", name.LocalName);
			if (elementDefinition != null)
			{
				annotatedElement.SetAttributeValue("documentation", elementDefinition.Documentation);
				if (elementDefinition.ClassType != null)
					annotatedElement.SetAttributeValue("classType", elementDefinition.ClassType.FullName);

				// add attributes
				foreach (var c in elementDefinition.GetAttributes())
				{

					var attribute = config?.Attribute(c.Name);
					if (attribute != null || c.MaxOccurs == 1)
					{
						BuildAnnotatedAttribute(annotatedElement, c, attribute == null ? config?.Element(c.Name)?.Value : attribute.Value);
					}
					else if (config != null)
					{
						foreach (var element in config.Elements(c.Name))
						{
							if (element.Value != null)
								BuildAnnotatedAttribute(annotatedElement, c, element.Value);
						}
					}
				}

				// add elements
				foreach (var c in elementDefinition.GetElements())
				{
					if (c.ClassType == null)
					{
						if (c.MaxOccurs == 1)
						{
							annotatedElement.Add(BuildAnnotatedConfigNode(c.Name, config?.Element(c.Name)));
						}
						else if (config != null)
						{
							foreach (var x in config.Elements(c.Name))
								annotatedElement.Add(BuildAnnotatedConfigNode(c.Name, x));
						}
					}
				}
			}
			return annotatedElement;
    } // proc BuildAnnotatedConfigNode

		[
		DEConfigHttpAction("config", SecurityToken = SecuritySys),
		Description("Gibt die Einstellungen der aktuellen Knotens zurück.")
		]
		private XElement HttpConfigAction(bool raw = false)
		{
			var config = Config;
			if (raw || config == null)
				return config;
			else
				return BuildAnnotatedConfigNode(config.Name, config);
		} // func HttpConfigAction

		#endregion

		#region -- Process Request/Action -------------------------------------------------

		internal void UnsafeInvokeHttpAction(string sAction, IDEContext r)
		{
			var returnValue = InvokeAction(sAction, r);

			// Erzeuge die Rückgabe
			if (returnValue == DBNull.Value) // NativeCall
				return;
			else if (r.IsOutputStarted)
			{
				if (returnValue == null)
					return;
				else
					throw new ArgumentException("Return value expected.");
			}
			else if (returnValue == null)
			{
				returnValue = CreateDefaultXmlReturn(true, null);
			}
			else if (returnValue is XElement)
			{
				var x = (XElement)returnValue;
				if (x.Attribute("status") == null)
					SetStatusAttributes(x, true);
			}
			else if (returnValue is LuaResult)
			{
				LuaResult result = (LuaResult)returnValue;
				XElement x = CreateDefaultXmlReturn(true, result[1] as string);

				if (result[0] is LuaTable)
					Procs.ToXml((LuaTable)result[0], x);
				else if (result[0] != null)
					x.Add(result[0]);

				returnValue = x;
			}
			else if (returnValue is LuaTable)
			{
				var x = CreateDefaultXmlReturn(true, null);
				Procs.ToXml((LuaTable)returnValue, x);
				returnValue = x;
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
		internal bool UnsafeProcessRequest(IDEContext r)
		{
			foreach (var w in from c in this.UnsafeChildren
												let cHttp = c as HttpWorker
												where cHttp != null && r.RelativeSubPath.StartsWith(cHttp.VirtualRoot, StringComparison.OrdinalIgnoreCase)
												orderby cHttp.Priority descending
												select cHttp)
			{
				// Führe den Request aus
				if (r.TryEnterSubPath(w, w.VirtualRoot))
				{
					using (w.EnterReadLock())
					{
						try
						{
							if (w.Request(r))
								return true; // Alles I/O
						}
						finally
						{
							r.ExitSubPath(w);
						}
					}
				}
			}
			return OnProcessRequest(r);
		} // func UnsafeProcessRequest

		/// <summary>Wird aufgerufen, wenn eine Http-Anfrage am Knoten verarbeitet werden soll.</summary>
		/// <param name="r">Http-Response</param>
		/// <returns><c>true</c>, wenn die Anfrage beantwortet wurde.</returns>
		protected virtual bool OnProcessRequest(IDEContext r)
		{
			return false;
		} // func OnProcessRequest

		public static XElement SetStatusAttributes(XElement x, bool state, string text = null)
		{
			x.SetAttributeValue("status", state ? "ok" : "error");
			if (text != null)
				x.SetAttributeValue("text", text);
			return x;
		} // func SetStatusAttributes

		public static XElement CreateDefaultXmlReturn(bool state, string text = null)
			=> SetStatusAttributes(new XElement("return"), state, text);
		
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

		private bool CheckKnownTable(object key, string tableName, ref object r)
		{
			var stringKey = key as string;
			if (stringKey != null && stringKey == tableName)
			{
				r = new LuaTable();
				SetMemberValue(tableName, r, rawSet: true);
				return true;
			}
			else
				return false;
		} // func CheckKnownTable

		protected override object OnIndex(object key)
		{
			var r = base.OnIndex(key); // ask __metatable
			if (r == null) // check for parent
			{
				if (!CheckKnownTable(key, LuaActions, ref r) &&
					!CheckKnownTable(key, LuaDispose, ref r) &&
					!CheckKnownTable(key, LuaConfiguration, ref r))
				{
					var t = sp as LuaTable;
					if (t != null)
						return t.GetValue(key);
				}
			}
			return r;
		} // func OnIndex

		[LuaMember("GetService")]
		private object LuaGetService(object serviceType)
		{
			var createServiceType = new Func<object, Type>(v =>
			 {
				 if (v is Type)
					 return (Type)v;
				 else if (v is LuaType)
					 return ((LuaType)v).Type;
				 else if (v is string)
					 return LuaType.GetType((string)v);
				 else
					 return null;
			 });

			if (serviceType == null)
				return null;
			else
				return GetService(createServiceType(serviceType));
		} // func GetService

		[LuaMember(nameof(RegisterDisposable))]
		private object RegisterDisposable(string key, object value)
		{
			var luaDispose = (LuaTable)GetMemberValue(LuaDispose);
			DisposeLuaValue(key, luaDispose[key]);
			luaDispose[key] = value;
			return value;
		} // proc RegisterDisposableObject

		#endregion

		/// <summary>Setzt ein neues Ereignis.</summary>
		/// <param name="eventId">Bezeichnung des Ereignisses.</param>
		/// <param name="index">Optionaler Index für das Ereignis. Falls mehrere Elemente zu diesem Ereignis zugeordnet werden können.</param>
		/// <param name="values">Werte die zu dieser Kombination gesetzt werden.</param>
		/// <remarks>Ereignisse werden nicht zwingend sofort gemeldet, sondern können auch gepollt werden.
		/// Deswegen sollte bei den <c>values</c> darauf geachtet werden, dass sie überschreibbar gestaltet 
		/// werden.</remarks>
		protected void FireEvent(string eventId, string index = null, XElement values = null)
		{
			Server.AppendNewEvent(this, eventId, index, values);
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
		public virtual string Icon { get { return null; } }

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

		private static MethodInfo miGetPropertyObject;
		private static MethodInfo miGetPropertyString;
		private static MethodInfo miGetPropertyGeneric;

		private static PropertyInfo piInvariantCulture;
		private static MethodInfo miConvertToStringFallBack;
		private static MethodInfo miConvertFromInvariantString;

		static DEConfigItem()
		{
			var typePropertyDictionaryExtensions = typeof(PropertyDictionaryExtensions).GetTypeInfo();
			foreach (var mi in typePropertyDictionaryExtensions.GetDeclaredMethods("GetProperty"))
			{
				var parameterInfo = mi.GetParameters();
				if (parameterInfo.Length == 3)
				{
					if (parameterInfo[2].ParameterType == typeof(object))
						miGetPropertyObject = mi;
					else if (parameterInfo[2].ParameterType == typeof(string))
						miGetPropertyString = mi;
					else if (mi.IsGenericMethodDefinition)
						miGetPropertyGeneric = mi;
				}
			}
			if (miGetPropertyObject == null || miGetPropertyString == null || miGetPropertyGeneric == null)
				throw new ArgumentNullException("sctor", "PropertyDictionaryExtensions");

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

		internal static string GetSourceUri(XmlSchemaObject x)
		{
			if (x.Parent == null)
				return x.SourceUri;
			else
			{
				var t = x.SourceUri;
				return String.IsNullOrEmpty(t) ? GetSourceUri(x.Parent) : t;
			}
		} // func GetSourceUri
	} // class DEConfigItem

	#endregion
}
