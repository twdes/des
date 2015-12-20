using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal interface IDEServerResolver
	{
		/// <summary></summary>
		/// <param name="path"></param>
		void AddPath(string path);
		/// <summary></summary>
		/// <param name="assemblyName"></param>
		/// <returns></returns>
		Assembly Load(string assemblyName);
	} // interface IDEServerResolver

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal partial class DEServer : DEConfigLogItem, IDEBaseLog, IDEServer, IDEServerResolver
	{
		public const string ServerCategory = "Dienst";
		public const string NetworkCategory = "Netzwerk";

		#region -- class DumpFileInfo -----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		[DEListTypePropertyAttribute("dump")]
    private sealed class DumpFileInfo
		{
			public DumpFileInfo(int id, string fileName)
			{
				this.Id = id;
				this.FileName = fileName;
			} // ctor

			[DEListTypeProperty("@id")]
			public int Id { get; }
			[DEListTypeProperty("@size")]
			public long Size
			{
				get
				{
					try
					{
						return new FileInfo(FileName).Length;
					}
					catch
					{
						return -1;
					}
				}
			} // prop Size

			[DEListTypeProperty("@created")]
			public DateTime LastWriteTimeUtc
			{
				get
				{
					try
					{
						return new FileInfo(FileName).LastWriteTimeUtc;
					}
					catch
					{
						return DateTime.MinValue;
					}
				}
			} // prop Size

			public string FileName { get; }
		} // class DumpFileInfo

		#endregion

		private string logPath = null;                 // Pfad für sämtliche Log-Dateien
		private SimpleConfigItemProperty<int> propertyLogCount = null; // Zeigt an wie viel Log-Dateien verwaltet werden

		private volatile int securityGroupsVersion = 0;
		private Dictionary<string, string[]> securityGroups = new Dictionary<string, string[]>(); // Sicherheitsgruppen
		private Dictionary<string, IDEUser> users = new Dictionary<string, IDEUser>(StringComparer.OrdinalIgnoreCase); // Nutzer

		private ResolveEventHandler resolveEventHandler;
		private DEConfigurationService configuration;
		private DEQueueScheduler queue = null;

		private DEList<DumpFileInfo> dumpFiles;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEServer(string configurationFile, IEnumerable<string> properties)
			: base(new ServiceContainer(), "Main")
		{
			if (Current != null)
				throw new InvalidOperationException("Only one instance per process is allowed.");
			Current = this;

			// register resolver
			this.resolveEventHandler = (sender, e) => ResolveAssembly(e.Name, e.RequestingAssembly);
			AddAssemblyPath(".");
			AddAssemblyPath(Path.GetDirectoryName(GetType().Assembly.Location));
			AppDomain.CurrentDomain.AssemblyResolve += resolveEventHandler;

			// create configurations service
			this.configuration = new DEConfigurationService(this, configurationFile, ConvertProperties(properties));
			this.dumpFiles = new DEList<DumpFileInfo>(this, "tw_dumpfiles", "Dumps");

			PublishItem(dumpFiles);
			PublishItem(new DEConfigItemPublicAction("dump") { DisplayName = "Dump" });
    } // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				AppDomain.CurrentDomain.AssemblyResolve -= resolveEventHandler;

				queue.ExecuteEvent(DEServerEvent.Shutdown);

				queue.Dispose(); // Stop main thread queue
				DoneConfiguration(); // close configuration
			}

			base.Dispose(disposing);
		} // proc Dispose

		private void OnStart()
		{
			LogMsg(EventLogEntryType.Information, "Service is starting...", 0, 0, null);
			try
			{
				queue = new DEQueueScheduler(this); // Start the queue thread, this is needed for the configuration initialization.
				InitConfiguration(); // Start the configuration system
				InitServerInfo(); // initialize server info
			// the rest will be started from the configuration system
			}
			catch (Exception e)
			{
				LogMsg(e);
			}
		} // proc OnStart

		private void OnStop()
		{
			LogMsg(EventLogEntryType.Information, "Service is shuting down.", 0, 0, null);
			try
			{
				Dispose();
			}
			catch (Exception e)
			{
				LogMsg(e);
				throw new Exception("Data Exchange Server shutdown failure.", e);
			}
		} // proc OnStop

		private static PropertyDictionary ConvertProperties(IEnumerable<string> properties)
		{
			var configurationProperties = new PropertyDictionary();

			foreach (var c in properties)
			{
				var equalAt = c.IndexOf('=');
				if (equalAt == -1)
					configurationProperties.SetProperty(c.Trim(), typeof(bool), true);
				else
				{
					var name = c.Substring(0, equalAt).Trim();
					var value = c.Substring(equalAt + 1).Trim();
					int t1;
					if (Int32.TryParse(value, out t1))
						configurationProperties.SetProperty(name, typeof(int), value);
					else
						configurationProperties.SetProperty(name, typeof(string), value);
				}
			}

			return configurationProperties;
		} // proc ConvertProperties

		#endregion

		#region -- ServerInfo Member - Http -----------------------------------------------

		private SimpleConfigItemProperty<long> propertyMemory = null;
		private long lastMemory = 0;
		
		private void InitServerInfo()
		{
			// Zeige die Versionsnummer an
			RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_version", "Version", ServerCategory, "Versioninformation des Servers.", null, GetServerFileVersion()));

			// Speichereigenschaften
			RegisterProperty(propertyMemory = new SimpleConfigItemProperty<long>(this, "tw_base_gc", "Speicher", ServerCategory, "Aktuell größe des verwalteten Heaps.", "FILESIZE", 0));

			// Angaben zum Netzwerk
			var hostName = Dns.GetHostName();
			RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_hostname", "Rechnername", NetworkCategory, "Hostname des Servers.", null, hostName));

			var i = 1;
			var he = Dns.GetHostEntry(hostName);
			foreach (IPAddress addr in he.AddressList)
			{
				RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_address_" + i.ToString(), "IP " + i.ToString(), NetworkCategory, "IP Adresse unter der der Server ereichbar ist.", null, addr.ToString()));
				i++;
			}

			Queue.RegisterIdle(() =>
				{
					int iTmp = 0;
					IdleMemoryViewerRefreshValue(false, ref iTmp);
				}, 3000);

			PublishItem(new DEConfigItemPublicPanel("tw_server_info", "/wpf/ServerInfoControl.xaml") { DisplayName = "Server Informationen" });
		} // proc InitServerInfo

		private string GetServerFileVersion()
		{
			var attrVersion = GetType().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
			return attrVersion == null ? "0.0.0.0" : attrVersion.Version;
		} // func GetServerFileVersion

		private void IdleMemoryViewerRefreshValue(bool collect, ref int iLuaMemory)
		{
			// GC Speicher
			var newMemory = GC.GetTotalMemory(collect);
			if (newMemory != lastMemory)
				propertyMemory.Value = lastMemory = newMemory;
		} // proc IdleMemoryViewerRefreshValue

		#region -- GetServerInfoData ------------------------------------------------------

		private XElement GetServerInfoAssembly(string name, string assemblyName, string title, string version, string copyright, string imagePath)
		{
			return new XElement("assembly",
				new XAttribute("name", name),
				new XAttribute("assembly", assemblyName),
				new XAttribute("title", title),
				new XAttribute("version", version),
				new XAttribute("copyright", copyright),
				new XAttribute("image", imagePath)
			);
		} // proc GetServerInfoAssembly

		private XElement GetServerInfoAssembly(Assembly assembly, string imagePath)
		{
			var attrTitle = assembly.GetCustomAttribute<AssemblyTitleAttribute>();
			var attrVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
			var attrCopy = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();

			if (!String.IsNullOrEmpty(imagePath))
				imagePath.Replace(" ", "");

			return GetServerInfoAssembly(
				assembly.GetName().Name,
				assembly.FullName,
				attrTitle.Title,
				attrVersion == null ? assembly.GetName().Version.ToString() : attrVersion.Version,
				attrCopy.Copyright,
				imagePath);
		} // func GetServerInfoAssembly

		[
		DEConfigHttpAction("serverinfo", SecurityToken = SecuritySys),
		Description("Gibt die Information über den Server zurück.")
		]
		private XElement GetServerInfoData()
		{
			var xData = new XElement("serverinfo");

			// Server selbst
			var attrCopy = typeof(DEServer).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();
			xData.SetAttributeValue("version", GetServerFileVersion());
			xData.SetAttributeValue("copyright", attrCopy == null ? "err" : attrCopy.Copyright);

			// Betriebssystem Informationen / .net Informationen
			var asmDotNet = typeof(IQueryable).Assembly;
			var attrVersionDotNet = asmDotNet.GetCustomAttribute<AssemblyFileVersionAttribute>();
			var versionDotNet = attrVersionDotNet == null ? asmDotNet.GetName().Version : new Version(attrVersionDotNet.Version);
			var attrCopyDotNet = asmDotNet.GetCustomAttribute<AssemblyCopyrightAttribute>();

			xData.Add(new XElement("os",
				new XAttribute("versionstring", Environment.OSVersion.VersionString),
				new XAttribute("version", Environment.OSVersion.Version.ToString())
			));
			xData.Add(new XElement("net",
				new XAttribute("assembly", asmDotNet.GetName().ToString()),
				new XAttribute("versionstring", String.Format(".Net Framework {0}.{1}", versionDotNet.Major, versionDotNet.Minor)),
				new XAttribute("versionfile", versionDotNet),
				new XAttribute("version", Environment.Version.ToString()),
				new XAttribute("copyright", attrCopyDotNet.Copyright)
			));

			var xAssemblies = new XElement("assemblies");
			xData.Add(xAssemblies);

			// NeoLua
			var asmLua = typeof(Neo.IronLua.Lua).Assembly;

			var luaCompany = asmLua.GetCustomAttribute<AssemblyCompanyAttribute>();
			var luaCopyright = asmLua.GetCustomAttribute<AssemblyCopyrightAttribute>();
			var luaFileVersion = asmLua.GetCustomAttribute<AssemblyFileVersionAttribute>();
			var versionLua = asmLua.GetName().Version;

			xAssemblies.Add(GetServerInfoAssembly(
					"NeoLua",
					asmLua.FullName,
					String.Format("NeoLua {0}.{1}", versionLua.Major, versionLua.Minor),
					luaFileVersion.Version,
					luaCopyright.Copyright + " " + luaCompany.Company,
					"/images/lua16.png"
				)
			);

			// Geladene Assemblies
			foreach (Assembly asmCur in AppDomain.CurrentDomain.GetAssemblies())
			{
				var attrDesc = asmCur.GetCustomAttribute<DescriptionAttribute>();
				if (attrDesc != null)
					xAssemblies.Add(GetServerInfoAssembly(asmCur, "/?action=resource&image=" + attrDesc.Description + "," + asmCur.FullName.Replace(" ", "")));
			}

			return xData;
		} // func GetServerInfoData

		#endregion

		#region -- HttpProcessAction ------------------------------------------------------

		[
		DEConfigHttpAction("process", SecurityToken = SecuritySys),
		Description("Gibt Informationen zum Process zurück. Mittels Typ kann man die Rückgabe einschränken (1=speicher,2=zeit,3=beides). Collect bewirkt das der GC aufgerufen wird.")
		]
		private XElement HttpProcessAction(int typ = 3, bool collect = false)
		{
			// Speicherinformationen des GC
			var luaMemory = 0;
			if ((typ & 1) == 1 && collect)
			{
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
				IdleMemoryViewerRefreshValue(true, ref luaMemory);
			}

			// Speicherinformation des Processes
			using (Process p = Process.GetCurrentProcess())
			{
				// Rückgabe zusammensetzen
				XElement r = new XElement("process");
				if ((typ & 1) != 0)
				{
					var count = 0;
					lock (propertyChanged)
						count = propertyChanged.Count;
					r.Add(
						new XElement("gc_generation", GC.MaxGeneration),
						new XElement("gc_memory", propertyMemory.Value),
						new XElement("srv_properties", count),
						new XElement("mem_paged", p.PagedMemorySize64),
						new XElement("mem_sys_paged", p.PagedSystemMemorySize64),
						new XElement("mem_sys_nonpaged", p.NonpagedSystemMemorySize64),
						new XElement("mem_peak_paged", p.PeakPagedMemorySize64),
						new XElement("mem_private", p.PrivateMemorySize64),
						new XElement("mem_virtual", p.VirtualMemorySize64),
						new XElement("mem_peak_virtual", p.PeakVirtualMemorySize64),
						new XElement("mem_workingset", p.WorkingSet64),
						new XElement("mem_peak_workingset", p.PeakWorkingSet64),
						new XElement("mem_handles", p.HandleCount)
						);
				}
				if ((typ & 2) != 0)
					r.Add(
						new XElement("time_start", p.StartTime.ToString("G")),
						new XElement("time_total", p.TotalProcessorTime.ToString()),
						new XElement("time_user", p.UserProcessorTime.ToString()),
						new XElement("time_priv", p.PrivilegedProcessorTime.ToString())
						);
				return r;
			}
		} // proc HttpProcessAction

		#endregion

		#region -- HttpDumpAction -----------------------------------------------------------

		private int lastDumpFileInfoId = 0;

		[
		DEConfigHttpAction("dump", IsSafeCall = true, SecurityToken = SecuritySys),
		Description("Dumps the current state of the process in a local file.")
		]
		private XElement HttpDumpAction(bool mini = false)
		{
			// check for the procdump.exe
			var procDump = Config.Element(xnServer)?.GetAttribute("procdump", String.Empty);
			if (String.IsNullOrEmpty(procDump) || !File.Exists(procDump))
				throw new ArgumentException("procdump.exe is not available.");

			// prepare arguments
			var sbArgs = new StringBuilder();
			if (!mini)
				sbArgs.Append("-ma "); // dump all
			sbArgs.Append("-o "); // overwrite existing dump
			sbArgs.Append("-accepteula "); // accept eula
      sbArgs.Append(Process.GetCurrentProcess().Id).Append(' '); // process id

			// create the dump in the temp directory
			DumpFileInfo fi;
			using (dumpFiles.EnterWriteLock())
			{
				var newId = ++lastDumpFileInfoId;
				dumpFiles.Add(fi = new DumpFileInfo(newId, Path.Combine(Path.GetTempPath(), $"DEServer_{newId:000}.dmp")));
			}

			sbArgs.Append(fi.FileName);

			// prepare calling procdump
			ProcessStartInfo psi = new ProcessStartInfo(procDump, sbArgs.ToString());
			psi.UseShellExecute = false;
			psi.RedirectStandardOutput = true;
      using (var p = Process.Start(psi))
			{
				if (!p.WaitForExit(5 * 60 + 1000))
					p.Kill();

				var outputText = p.StandardOutput.ReadToEnd();

				return new XElement("return",
					new XAttribute("status", p.ExitCode > 0 ? "ok" : "error"),
					new XAttribute("id", fi.Id),
					new XAttribute("exitcode", p.ExitCode),
					new XAttribute("text", outputText)
				);
			}
		} // proc HttpDumpAction

		[
		DEConfigHttpAction("dumpload", SecurityToken = SecuritySys),
		Description("Sends the dump to the client.")
		]
		private void HttpDumpLoadAction(IDEContext r, int id = -1)
		{
			// get the dump file
			DumpFileInfo di = null;
			using (dumpFiles.EnterReadLock())
				{
					var index = dumpFiles.FindIndex(c => c.Id == id);
					if (index >= 0)
						di = dumpFiles[index];
				}
			
			// send the file
			if (di == null)
				throw new ArgumentException("dump id is wrong.");
			var fi = new FileInfo(di.FileName);
			if (!fi.Exists)
				throw new ArgumentException("dump id is invalid.");
			
			r.SetAttachment(fi.Name)
				.WriteFile(fi.FullName, MimeTypes.Application.OctetStream + ";gzip");
		} // HttpDumpLoadAction

		#endregion

		#endregion

		#region -- OnProcessRequest -------------------------------------------------------

		protected override bool OnProcessRequest(IDEContext r)
		{
			if (String.Compare(r.RelativeSubPath, "favicon.ico", true) == 0)
			{
				r.WriteResource(typeof(DEServer), "des.ico");
				return true;
			}
			else
				return base.OnProcessRequest(r);
		} // proc OnProcessRequest

		#endregion

		#region -- Configuration Load -----------------------------------------------------

		private Action refreshConifg;

		private void InitConfiguration()
		{
			refreshConifg = InternalRefreshConfiguration;

			PublishItem(new DEConfigItemPublicAction("readconfig") { DisplayName = "Refresh(Configuration)" });

			// Lese die Konfigurationsdatei
			ReadConfiguration();
		} // proc InitConfiguration

		private void DoneConfiguration()
		{
			configuration = null;
		} // proc DoneConfiguration

		[
		DEConfigHttpAction("readconfig", SecurityToken = SecuritySys),
		Description("Lädt die Konfigurations neu.")
		]
		private void ReadConfiguration()
		{
			Queue.CancelCommand(refreshConifg);
			Queue.RegisterCommand(refreshConifg, 500);
		} // proc ReadConfiguration

		private void BeginReadConfiguration(DEConfigLoading config)
		{
			// Lade die aktuelle Konfiguration
			config.BeginReadConfiguration();

			// Wenn erfolgreich geladen, dann hole SubItems
			if (config.IsLoadedSuccessful)
			{
				// Lade die SubItems
				foreach (DEConfigLoading cur in config.SubLoadings)
					BeginReadConfiguration(cur);

				// Aktiviere die Konfiguration
				if (config.IsConfigurationChanged)
				{
					Exception e = config.EndReadConfiguration();
					if (e != null)
					{
						config.Log
							.NewLine()
							.WriteLine()
							.WriteException(e)
							.WriteLine();

						config.DestroySubConfiguration(); // Zerstöre die SubItems
					}
				}
			}
			else // Zerstöre die SubItems
				config.DestroySubConfiguration();
		} // proc BeginReadConfiguration

		private void InternalRefreshConfiguration()
		{
			// Ohne Log, werden die Informationen in das Windows-Erreignisprotokoll geschrieben.
			if (!HasLog)
				LogMsg(EventLogEntryType.Information, "Reread configuration.");

			using (var log = this.LogProxy().GetScope(LogMsgType.Information, true))
				try
				{
					var xConfig = configuration.ParseConfiguration();

					// Zerlege die Konfiguration zur Validierung
					log.WriteLine("BEGIN Load configuration");
					using (log.Indent())
					{
						using (DEConfigLoading config = new DEConfigLoading(this, log, xConfig, configuration.ConfigurationStamp))
							BeginReadConfiguration(config);
					}
					log.WriteLine("END Load configuration");
				}
				catch (Exception e)
				{
					log.WriteException(e);

					if (!HasLog || !Queue.IsQueueRunning) // Schreib die Fehlermeldung ins Windowsprotokoll
						LogMsg(e);
				}

			FireEvent("refresh");
		} // proc InternalRefreshConfiguration

		#endregion

		#region -- Configuration Process --------------------------------------------------

		protected override string GetConfigItemName(XElement element)
		{
			if (element.Name == xnLuaEngine)
				return "LuaEngine";
			else if (element.Name == xnHttp)
				return "Http";
			else if (element.Name == xnCron)
				return "Cron";
			else if (element.Name == xnServerTcp)
				return "ServerTcp";
			else
				return base.GetConfigItemName(element);
		} // func GetConfigItemName

		protected override void ValidateConfig(XElement config)
		{
			base.ValidateConfig(config);

			// forece the lua engine and the http server
			if (config.Element(xnLuaEngine) == null)
				config.AddFirst(new XElement(xnLuaEngine));
			if (config.Element(xnHttp) == null)
				config.AddFirst(new XElement(xnHttp));
		} //  func ValidateConfig

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			lock (securityGroups)
				securityGroups.Clear();

			// Lösche die alten Resolveeinträge
			if (config.ConfigOld != null)
				RemoveResolve(config.ConfigOld.Element(xnServer));

			// Lade die Erweiterungen
			var server = config.ConfigNew.Element(xnServer);
			if (server != null)
			{
				foreach (XElement cur in server.Elements())
					if (cur.Name == xnServerSecurityGroup)
					{
						var name = cur.GetAttribute("name", String.Empty).ToLower();
						if (String.IsNullOrEmpty(name))
							Log.LogMsg(LogMsgType.Warning, "server/securitygroup benötigt Namen.");
						else
						{
							lock (securityGroups)
							{
								string[] tmp;
								if (securityGroups.TryGetValue(name, out tmp))
									securityGroups[name] = CombineSecurityTokens(tmp, SplitSecurityGroup(cur.Value));
								else
									securityGroups[name] = SplitSecurityGroup(cur.Value);
							}
						}
					}
			}
			securityGroupsVersion++;

			// Den Start verzögern
			if (server != null && config.ConfigOld == null)
			{
				// Dienstabhängigkeiten
				foreach (var dependon in server.Elements(xnServerDependOnServer))
					WaitForService(dependon.Value, dependon.GetAttribute("maxtime", 30000));

				// Warten
				var waitTimeout = server.GetAttribute("globalwait", 0);
				if (waitTimeout > 0)
				{
					LogMsg(EventLogEntryType.Information, String.Format("Wait {0}ms...", waitTimeout));
					//serviceLog.RequestAdditionalTime(iWait);
					Thread.Sleep(waitTimeout);
					LogMsg(EventLogEntryType.Information, "Continue load configure...");
				}
			}

			// Initialisiere die Log-Datei
			var newLogPath = server.GetAttribute("logpath", String.Empty);
			if (logPath == null)
			{
				if (String.IsNullOrEmpty(newLogPath))
					LogMsg(EventLogEntryType.Error, "server/@logpath wurde nicht nicht angegeben.");

				// Lege das Verzeichnis an
				this.logPath = newLogPath;
				var di = new DirectoryInfo(logPath);
				if (!di.Exists)
					di.Create();

				// Erzeuge Statie
				propertyLogCount = new SimpleConfigItemProperty<int>(this, "tw_base_logcount", "Logs", ServerCategory, "Anzahl der Log-Dateien.", "{0:N0}", 0);
			}
			else if (String.Compare(newLogPath, logPath, true) != 0)
				Log.Warn("Für die Änderung des LogPath ist ein Neustart erforderlich.");

			// Initialisiere die Basis
			base.OnBeginReadConfiguration(config);
		} // proc OnBeginReadConfiguration

		protected override bool IsSubConfigurationElement(XName xn)
		{
			if (xn == xnLuaEngine ||
				xn == xnHttp ||
				xn == xnServerTcp ||
				xn == xnCron)
				return true;
			else
				return base.IsSubConfigurationElement(xn);
		} // func IsSubConfigurationElement

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			// refresh cron service
			queue.ExecuteEvent(DEServerEvent.Reconfiguration);
		} // proc OnEndReadConfiguration

		[ThreadStatic]
		private static MethodInfo miRegisterSubItem = null;

		public bool LoadConfigExtension(IDEConfigLoading config, XElement load, string currentNamespace)
		{
			try
			{
				var type = configuration[load.Name]?.ClassType;
				if (type == null)
				{
					if (!String.IsNullOrEmpty(currentNamespace) && load.Name.NamespaceName != currentNamespace)
						Log.LogMsg(LogMsgType.Warning, "Typ für Element '{0}' wurde nicht im Schema gefunden.", load.Name);
					return false;
				}

				// Suche die Methode
				if (miRegisterSubItem == null)
					miRegisterSubItem = config.GetType().GetMethod("RegisterSubItem");

				// Erzeuge die Method
				var mi = miRegisterSubItem.MakeGenericMethod(type);
				mi.Invoke(config, new object[] { load });

				return true;
			}
			catch (Exception e)
			{
				Log.LogMsg(LogMsgType.Error, e.GetMessageString(), load.Name);
				return false;
			}
		} // proc LoadConfigExtensions

		private void RemoveResolve(XElement server)
		{
			if (server == null)
				return;

			foreach (XElement resolve in server.Elements(xnServerResolve))
				RemoveAssemblyPath(resolve.Value);
		} // proc RemoveResolve

		private void WaitForService(string serviceName, int maxTime)
		{
			var cur = Array.Find(ServiceController.GetServices(), sv => String.Compare(sv.ServiceName, serviceName, true) == 0);

			if (cur == null)
				LogMsg(EventLogEntryType.Warning, String.Format("Service '{0}' nicht gefunden...", serviceName));
			else
			{
				LogMsg(EventLogEntryType.Information, String.Format("Warte auf Service '{0}' für maximal {1:N0}ms...", serviceName, maxTime));
				while (cur.Status != ServiceControllerStatus.Running && maxTime > 0)
				{
					// serviceLog.RequestAdditionalTime(700); service is already started
					Thread.Sleep(500);
					cur.Refresh();
					maxTime -= 500;
				}
				if (cur.Status != ServiceControllerStatus.Running)
					LogMsg(EventLogEntryType.Warning, String.Format("Service '{0}' nicht gestartet...", serviceName));
			}
		} // proc WaitForService

		#endregion

		#region -- User Dictionary --------------------------------------------------------

		public void RegisterUser(IDEUser user)
		{
			lock (users)
			{
				if (users.ContainsKey(user.Name))
					throw new ArgumentException(String.Format("Nutzerkonflikt. Es gibt schon einen Nutzer '{0}'.", user.Name));
				users[user.Name] = user;
			}
		} // proc RegisterUser

		public void UnregisterUser(IDEUser user)
		{
			lock (users)
			{
				IDEUser tmp;
				if (users.TryGetValue(user.Name, out tmp) && tmp == user)
					users.Remove(user.Name);
			}
		} // proc UnregisterUser

		public IDEAuthentificatedUser AuthentificateUser(IIdentity user)
		{
			lock (users)
			{
				IDEUser u;
				if (users.TryGetValue(user.Name, out u))
					return u.Authentificate(user);
				else
					return null;
			}
		} // func AuthentificateUser

		#endregion

		#region -- Security Groups --------------------------------------------------------

		public string[] BuildSecurityTokens(string securityTokens)
		{
			var tokens = new List<string>();
			BuildSecurityTokens(tokens, SplitSecurityGroup(securityTokens), true);
			return tokens.ToArray();
		} // func BuildSecurityTokens

		private string[] CombineSecurityTokens(string[] securityTokens1, string[] securityTokens2)
		{
			var tokens = new List<string>();
			BuildSecurityTokens(tokens, securityTokens1, false);
			BuildSecurityTokens(tokens, securityTokens2, false);
			return tokens.ToArray();
		} // func CombineSecurityTokens

		private void BuildSecurityTokens(List<string> tokens, string[] securityTokens, bool resolveGroups)
		{
			var length = securityTokens.Length;

			for (var i = 0; i < length; i++)
			{
				var token = securityTokens[i].ToLower();

				// Füge den token ein
				var index = tokens.BinarySearch(token);
				if (index < 0)
				{
					tokens.Insert(~index, token);

					if (resolveGroups)
					{
						// Löse den Token auf
						lock (securityGroups)
						{
							string[] groupTokens;
							if (securityGroups.TryGetValue(token, out groupTokens))
								BuildSecurityTokens(tokens, groupTokens, true);
						}
					}
				}
			}
		} // proc BuildSecurityTokens

		private string[] SplitSecurityGroup(string securityTokens)
		{
			if (String.IsNullOrEmpty(securityTokens))
				return new string[0];

			return securityTokens.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
		} // func SplitSecurityGroup

		public int SecurityGroupsVersion { get { return securityGroupsVersion; } }

		#endregion

		#region -- IServiceProvider Member ------------------------------------------------

		public override object GetService(Type serviceType)
		{
			if (serviceType == typeof(DEServerBaseLog))
				return this;
			else if (serviceType == typeof(IDEConfigurationService))
				return configuration;
			else
				return base.GetService(serviceType);
		} // func GetService

		#endregion

		#region -- IDEServerResolver members ----------------------------------------------

		private readonly List<string> searchPaths = new List<string>();
		private readonly string[] assemblyExtensions = new string[] { ".dll", ".exe" };

		private static string GetAssemblyNormalizedPath(string path)
		{
			if (String.IsNullOrEmpty(path))
				throw new ArgumentNullException("path");

			path = Path.GetFullPath(path);
			if (path.EndsWith("\\"))
				path = path.Substring(0, path.Length - 1);
			return path;
		} // func GetAssemblyNormalizedPath

		private void AddAssemblyPath(string path)
		{
			lock(searchPaths)
			{
				path = GetAssemblyNormalizedPath(path);
				if (searchPaths.Exists(cur => String.Compare(cur, path, StringComparison.OrdinalIgnoreCase) == 0))
					return;

				searchPaths.Add(path);
			}
		} // proc AddAssemblyPath

		private void RemoveAssemblyPath(string path)
		{
			lock(searchPaths)
			{
				if (searchPaths == null || searchPaths.Count == 0)
					return;

				var index = searchPaths.FindIndex(cur => String.Compare(cur, path, true) == 0);
				if (index > 0)
					searchPaths.RemoveAt(index);
			}
		} // proc RemoveAssemblyPath

		private Assembly ResolveAssembly(string assemblyName, Assembly requestingAssembly)
		{
			var fileName = new AssemblyName(assemblyName);

			// Is the assembly loaded
			var assemblyFilter =
				(from c in AppDomain.CurrentDomain.GetAssemblies()
				where String.Compare(c.GetName().Name, fileName.Name, StringComparison.OrdinalIgnoreCase) == 0
				select c).ToArray();

			var assembly = assemblyFilter.Where(c => c.GetName().Version == fileName.Version).FirstOrDefault();
			if (assembly != null)
				return assembly;

			// Search the paths
			var fileList = new List<KeyValuePair<AssemblyName, FileInfo>>();
			lock (searchPaths)
			{
				var lengthI = searchPaths.Count;
				var lengthJ = assemblyExtensions.Length;
				for (var i = 0; i < lengthI; i++)
				{
					for (var j = 0; j < lengthJ; j++)
					{
						var fi = new FileInfo(Path.Combine(searchPaths[i], fileName.Name + assemblyExtensions[j]));
						if (fi.Exists)
							try
							{
								var testName = AssemblyName.GetAssemblyName(fi.FullName);
								if (String.Compare(testName.Name, fileName.Name, StringComparison.OrdinalIgnoreCase) == 0)
									fileList.Add(new KeyValuePair<AssemblyName, FileInfo>(testName, fi));
							}
							catch(Exception e)
							{
								Log.Warn(String.Format("Assembly '{0}' not loaded.", fi.FullName), e);
							}
					}
				}
			}

			// find a assembly with the correct version, and the newest stamp
			var bestByVersion = -1;
			var bestByStamp = -1;
			var versionLastWrite = DateTime.MinValue;
			var stampLastWrite = DateTime.MinValue;
			for (var i = 0; i < fileList.Count; i++)
			{
				var currentStamp = fileList[i].Value.LastWriteTime;
        if (currentStamp > stampLastWrite)
					bestByStamp = i;
				if (currentStamp > versionLastWrite && fileList[i].Key.Version == fileName.Version)
					bestByVersion = i;
			}

			if (bestByVersion >= 0)
				return Assembly.LoadFile(fileList[bestByVersion].Value.FullName);
			if (bestByStamp >= 0)
			{
				assembly = assemblyFilter.FirstOrDefault(c => c.FullName == fileList[bestByStamp].Key.FullName);
				return assembly ?? Assembly.LoadFile(fileList[bestByStamp].Value.FullName);
			}

			return null;
		} // func ResolveAssembly

		void IDEServerResolver.AddPath(string path) => AddAssemblyPath(path);
		Assembly IDEServerResolver.Load(string assemblyName) => ResolveAssembly(assemblyName, Assembly.GetCallingAssembly());

		#endregion

		#region -- IDEBaseLog members -----------------------------------------------------

		int IDEBaseLog.TotalLogCount
		{
			get { return propertyLogCount.Value; }
			set { propertyLogCount.Value = value; }
		} // prop TotalLogCount

		#endregion

		#region -- Base Lua Environment ---------------------------------------------------
		
		[LuaMember("rawget")]
		private object LuaRawGet(LuaTable t, object key)
			=> t.GetValue(key, true);

		[LuaMember("rawset")]
		private object LuaRawSet(LuaTable t, object key, object value)
			=> t.SetValue(key, value, true);

		[LuaMember("safecall")]
		private LuaResult LuaSafeCall(object target, params object[] args)
		{
			try
			{
				return new LuaResult(true, Lua.RtInvoke(target, args));
			}
			catch (Exception e)
			{
				return new LuaResult(false, e.Message, e);
			}
		} // func LuaSafeCall

		[LuaMember("rawmembers")]
		private IEnumerable<KeyValuePair<string, object>> LuaRawMembers(LuaTable t)
			=> t.Members;

		[LuaMember("rawarray")]
		private IList<object> LuaRawArray(LuaTable t)
			=> t.ArrayList;

		[LuaMember("format")]
		private string LuaFormat(string text, params object[] args)
			=> String.Format(text, args);

		[LuaMember("error")]
		private void LuaError(object error)
		{
			if (error is Exception)
				throw (Exception)error;
			else
				throw new Exception(error?.ToString());
		} // func LuaError

		[LuaMember("String")]
		private static LuaType LuaString => LuaType.GetType(typeof(String));

		[LuaMember("LogMsgType")]
		private static LuaType LuaLogMsgType => LuaType.GetType(typeof(LogMsgType));

		#endregion

		string IDEServer.LogPath => logPath;
		IDEConfigurationService IDEServer.Configuration => configuration;

		public IDEServerQueue Queue => queue;

		public override string Icon { get { return "/images/des16.png"; } }

		// -- Static --------------------------------------------------------------

		public static DEServer Current { get; private set; }
	} // class DEServer
}
