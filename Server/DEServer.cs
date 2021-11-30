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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Applications;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Server.IO;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- interface IDEServerResolver --------------------------------------------

	internal interface IDEServerResolver
	{
		void AddPath(string path);
		Assembly Load(string assemblyName);
	} // interface IDEServerResolver

	#endregion

	/// <summary></summary>
	internal partial class DEServer : DEConfigLogItem, IDEBaseLog, IDEServer, IDEServerResolver
	{
		public const string ServerCategory = "Service";
		public const string NetworkCategory = "Network";

		#region -- class DumpFileInfo -------------------------------------------------

		[DEListTypeProperty("dump")]
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

		#region -- class UserListDescriptor -------------------------------------------

		private sealed class UserListDescriptor : IDEListDescriptor
		{
			private readonly List<Type> knownType = new List<Type>();
			private readonly List<DEUserProperty> extendedProperties = new List<DEUserProperty>();

			public void Update(Type type)
			{
				if (knownType.IndexOf(type) >= 0)
					return;

				foreach (var attr in type.GetCustomAttributes<DEUserProperty>())
				{
					if (!extendedProperties.Exists(c => String.Compare(c.ListName, attr.ListName, StringComparison.OrdinalIgnoreCase) == 0))
						extendedProperties.Add(attr);
				}
			} // proc Update

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType("u");
				xml.WriteProperty("@id", typeof(string));
				xml.WriteProperty("@type", typeof(string));
				xml.WriteProperty("@displayName", typeof(string));
				xml.WriteProperty("@security", typeof(string));

				foreach (var c in extendedProperties)
					xml.WriteProperty("@" + c.ListName, c.Type);

				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				if (item is IDEUser user)
				{
					xml.WriteStartProperty("u");
					xml.WriteAttributeProperty("id", user.Identity.Name); // is equal to Identity.Name
					xml.WriteAttributeProperty("type", user.Identity.AuthenticationType);
					xml.WriteAttributeProperty("displayName", user.DisplayName);
					if (user.SecurityTokens != null)
						xml.WriteAttributeProperty("security", String.Join(";", user.SecurityTokens));

					foreach (var c in extendedProperties)
					{
						if (user.TryGetProperty(c.Name, out var v))
							xml.WriteAttributeProperty(c.ListName, v);
					}

					xml.WriteEndProperty();
				}
			} // proc WriteItem

			public static UserListDescriptor Default { get; } = new UserListDescriptor();
		} // class UserListDescriptor

		#endregion

		#region -- class UserListController -------------------------------------------

		private sealed class UserListController : DEListControllerBase
		{
			private readonly Dictionary<string, List<IDEUser>> users = new Dictionary<string, List<IDEUser>>(StringComparer.OrdinalIgnoreCase);

			public UserListController(DEConfigItem configItem)
				: base(configItem, UserListDescriptor.Default, "tw_users", "Active users")
			{
			} // ctor

			private List<IDEUser> GetBag(string key, bool allowCreate)
			{
				if (users.TryGetValue(key, out var bag))
					return bag;

				if (allowCreate)
				{
					bag = new List<IDEUser>();
					users[key] = bag;
					return bag;
				}
				else
					return null;
			} // func GetBag

			private int FindUserInBag(List<IDEUser> bag, IDEUser user)
				=> bag.IndexOf(user);

			public void Register(IDEUser user)
			{
				// get bag for user
				var bag = GetBag(user.Identity.Name, true);

				// add user to bag
				var idx = FindUserInBag(bag, user);
				if (idx == -1)
					bag.Add(user);
				else
					bag[idx] = user;
			} // proc Register

			public void UnRegister(IDEUser user)
			{
				var bag = GetBag(user.Identity.Name, false);
				if (bag != null)
				{
					var idx = FindUserInBag(bag, user);
					if (idx >= 0)
						bag.RemoveAt(0);
				}
			} // proc UnRegister

			public IEnumerable<IDEUser> GetUserByIdentity(IIdentity identity, bool exact = false)
			{
				var bag = GetBag(identity.Name, false);
				if (bag != null)
				{
					var notExact = new List<IDEUser>();

					// collect exact matches
					foreach (var u in bag)
					{
						if (ReferenceEquals(u.Identity, identity)
							|| u.Identity is IEquatable<IIdentity> exactCompare && exactCompare.Equals(identity))
							yield return u;
						else if (!exact)
							notExact.Add(u);
					}

					// emit not exact
					foreach (var u in notExact)
						yield return u;
				}
			} // func GetUserByIdentity

			public IEnumerable<IDEUser> GetUserByName(string name)
			{
				var bag = GetBag(name, false);
				if (bag != null)
				{
					foreach (var u in bag)
					{
						if (String.Compare(u.Identity.Name, name, StringComparison.Ordinal) == 0)
							yield return u;
					}
				}
			} // func GetUserByName

			public override IEnumerable List
			{
				get
				{
					foreach(var kv in users)
					{
						foreach (var c in kv.Value)
							yield return c;
					}
				}
			} // prop List
		} // class UserListController

		#endregion

		private string logPath = null;                                  // Pfad für sämtliche Log-Dateien
		private SimpleConfigItemProperty<int> propertyLogCount = null;  // Zeigt an wie viel Log-Dateien verwaltet werden

		private volatile int securityGroupsVersion = 0;
		private readonly Dictionary<string, string[]> securityGroups = new Dictionary<string, string[]>();
		private readonly UserListController users; // Active users for authentification

		private readonly ResolveEventHandler resolveEventHandler;
		private DEConfigurationService configuration;
		private DEQueueScheduler queue = null;

		private readonly DEList<DumpFileInfo> dumpFiles;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public DEServer(string configurationFile, IEnumerable<string> properties)
			: base(new ServiceContainer(), "Main")
		{
			if (Current != null)
				throw new InvalidOperationException("Only one instance per process is allowed.");
			Current = this;

			// register resolver
			resolveEventHandler = (sender, e) => ResolveAssembly(e.Name, e.RequestingAssembly);
			AddAssemblyPath(".");
			AddAssemblyPath(Path.GetDirectoryName(GetType().Assembly.Location));
			AppDomain.CurrentDomain.AssemblyResolve += resolveEventHandler;

			// certificate extension
			ServicePointManager.ServerCertificateValidationCallback = CheckServerCertificate;

			// register session list
			eventSessions = new DEList<EventSession>(this, "tw_eventsessions", "Event sessions");

			// create configurations service
			configuration = new DEConfigurationService(this, configurationFile, ConvertProperties(properties));
			dumpFiles = new DEList<DumpFileInfo>(this, "tw_dumpfiles", "Dumps");

			users = new UserListController(this);

			PublishItem(dumpFiles);
			PublishItem(new DEConfigItemPublicAction("dump") { DisplayName = "Dump" });
			PublishItem(users);
			PublishItem(eventSessions);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				AppDomain.CurrentDomain.AssemblyResolve -= resolveEventHandler;

				CloseEventSessions();

				queue.ExecuteEvent(DEServerEvent.Shutdown);

				queue.Dispose(); // Stop main thread queue
				DoneConfiguration(); // close configuration

				propertyMemory?.Dispose();
				users?.Dispose();
				dumpFiles?.Dispose();
				propertyLogCount?.Dispose();
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
					if (Int32.TryParse(value, out var t1))
						configurationProperties.SetProperty(name, typeof(int), value);
					else
						configurationProperties.SetProperty(name, typeof(string), value);
				}
			}

			return configurationProperties;
		} // proc ConvertProperties

		#endregion

		#region -- ServerInfo Member - Http -------------------------------------------

		private SimpleConfigItemProperty<FileSize> propertyMemory = null;
		private long lastMemory = 0;

		private void InitServerInfo()
		{
			// Zeige die Versionsnummer an
			RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_version", "Version", ServerCategory, "Versioninformation des Servers.", null, GetServerFileVersion()));

			// Speichereigenschaften
			RegisterProperty(propertyMemory = new SimpleConfigItemProperty<FileSize>(this, "tw_base_gc", "Speicher", ServerCategory, "Aktuell größe des verwalteten Heaps.", "{0:XiB}", 0));

			// Angaben zum Netzwerk
			var hostName = Dns.GetHostName();
			RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_hostname", "Rechnername", NetworkCategory, "Hostname des Servers.", null, hostName));

			var i = 1;
			var he = Dns.GetHostEntry(hostName);
			foreach (var addr in he.AddressList)
			{
				RegisterProperty(new SimpleConfigItemProperty<string>(this, "tw_base_address_" + i.ToString(), "IP " + i.ToString(), NetworkCategory, "IP Adresse unter der der Server ereichbar ist.", null, addr.ToString()));
				i++;
			}

			Queue.RegisterIdle(() => IdleMemoryViewerRefreshValue(false), 3000);
		} // proc InitServerInfo

		private string GetServerFileVersion()
		{
			var attrVersion = GetType().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
			return attrVersion == null ? "0.0.0.0" : attrVersion.Version;
		} // func GetServerFileVersion

		private void IdleMemoryViewerRefreshValue(bool collect)
		{
			// GC Speicher
			var newMemory = GC.GetTotalMemory(collect);
			if (newMemory != lastMemory)
				propertyMemory.Value = lastMemory = newMemory;
		} // proc IdleMemoryViewerRefreshValue

		#region -- GetServerInfoData --------------------------------------------------

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
			var attrVersion2 = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
			var attrCopy = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();

			if (!String.IsNullOrEmpty(imagePath))
				imagePath.Replace(" ", "");

			return GetServerInfoAssembly(
				assembly.GetName().Name,
				assembly.FullName,
				attrTitle.Title,
				attrVersion2?.InformationalVersion ?? attrVersion?.Version ?? assembly.GetName().Version.ToString(),
				attrCopy.Copyright,
				imagePath
			);
		} // func GetServerInfoAssembly

		[
		DEConfigHttpAction("serverinfo", SecurityToken = SecuritySys),
		Description("Gibt die Information über den Server zurück.")
		]
		public XElement GetServerInfoData(bool simple = false)
		{
			var xData = new XElement("serverinfo");

			var luaEngine = this.GetService<IDELuaEngine>(false);

			// send server info
			var attrCopy = typeof(DEServer).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();
			xData.SetAttributeValue("version", GetServerFileVersion());
			xData.SetAttributeValue("copyright", attrCopy == null ? "err" : attrCopy.Copyright);
			xData.SetAttributeValue("debug", LuaEngine.FormatAllowDebug(luaEngine?.DebugAllowed ?? LuaEngineAllowDebug.Disabled));

			if (!simple)
			{
				// Operation system / .net informationen
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
				var asmLua = typeof(Lua).Assembly;

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

				// get description for loaded assemblies
				foreach (var asmCur in AppDomain.CurrentDomain.GetAssemblies())
				{
					var attrInfo = asmCur.GetCustomAttribute<DEModulInfoAttribute>();
					var attrDesc = asmCur.GetCustomAttribute<DescriptionAttribute>(); // old variant
					var imageResource = attrInfo?.Image ?? attrDesc?.Description;
					if (imageResource != null)
						xAssemblies.Add(GetServerInfoAssembly(asmCur, "/?action=resource&image=" + imageResource + "," + asmCur.FullName.Replace(" ", "")));
				}
			}

			return xData;
		} // func GetServerInfoData

		#endregion

		#region -- HttpProcessAction --------------------------------------------------

		[
		DEConfigHttpAction("process", SecurityToken = SecuritySys),
		Description("Gibt Informationen zum Process zurück. Mittels Typ kann man die Rückgabe einschränken (1=speicher,2=zeit,3=beides). Collect bewirkt das der GC aufgerufen wird.")
		]
		public XElement HttpProcessAction(int typ = 3, bool collect = false)
		{
			// Speicherinformationen des GC
			if ((typ & 1) == 1 && collect)
			{
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
				IdleMemoryViewerRefreshValue(true);
			}

			// Speicherinformation des Processes
			using var p = Process.GetCurrentProcess();

			// Rückgabe zusammensetzen
			var r = new XElement("process");
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
			{
				r.Add(
					new XElement("time_start", p.StartTime.ToString("G")),
					new XElement("time_total", p.TotalProcessorTime.ToString()),
					new XElement("time_user", p.UserProcessorTime.ToString()),
					new XElement("time_priv", p.PrivilegedProcessorTime.ToString())
				);
			}
			return r;
		} // proc HttpProcessAction

		#endregion

		#region -- HttpDumpAction -----------------------------------------------------

		private int lastDumpFileInfoId = 0;

		[
		DEConfigHttpAction("dump", IsSafeCall = true, SecurityToken = SecuritySys),
		Description("Dumps the current state of the process in a local file.")
		]
		public XElement HttpDumpAction(bool mini = false)
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
			var psi = new ProcessStartInfo(procDump, sbArgs.ToString())
			{
				UseShellExecute = false,
				RedirectStandardOutput = true
			};

			using var p = Process.Start(psi);
			if (!p.WaitForExit(5 * 60 + 1000))
				p.Kill();

			var outputText = p.StandardOutput.ReadToEnd();

			return SetStatusAttributes(
				new XElement("return",
					new XAttribute("id", fi.Id),
					new XAttribute("exitcode", p.ExitCode)
				),
				p.ExitCode > 0 ? DEHttpReturnState.Ok : DEHttpReturnState.Error,
				outputText
			);
		} // proc HttpDumpAction

		[
		DEConfigHttpAction("dumpload", SecurityToken = SecuritySys),
		Description("Sends the dump to the client.")
		]
		public void HttpDumpLoadAction(IDEWebRequestScope r, int id = -1)
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

		#region -- HttpResource -------------------------------------------------------

		[DEConfigHttpAction("resource", SecurityToken = SecuritySys)]
		public void HttpResource(IDEWebRequestScope r, string image)
		{
			if (String.IsNullOrEmpty(image))
			{
				r.SetStatus(HttpStatusCode.BadRequest, "Missing parameter");
				return;
			}

			var p = image.IndexOf(',');
			if (p == -1)
			{
				r.SetStatus(HttpStatusCode.BadRequest, "Wrong parameter");
				return;
			}

			// search only loaded assemblies
			var assemblyName = new AssemblyName(image.Substring(p + 1));
			var imageName = image.Substring(0, p);
			var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
			{
				var curName = a.GetName();
				return curName.Name == assemblyName.Name && curName.Version == assemblyName.Version;
			});
			var src = asm?.GetManifestResourceStream(imageName);
			if (src == null)
			{
				r.SetStatus(HttpStatusCode.NotFound, "Image not found.");
				return;
			}

			// output content, do not cache, client should cache
			r.SetLastModified(File.GetLastWriteTimeUtc(asm.Location));
			r.WriteStream(src, MimeTypeMapping.GetMimeTypeFromExtension(Path.GetExtension(imageName)));
		} // void HttpResource

		#endregion

		#endregion

		#region -- OnProcessRequest ---------------------------------------------------

		protected override async Task<bool> OnProcessRequestAsync(IDEWebRequestScope r)
		{
			if (String.Compare(r.RelativeSubPath, "favicon.ico", StringComparison.OrdinalIgnoreCase) == 0)
			{
				await Task.Run(() => r.WriteResource(typeof(DEServer), "des.ico"));
				return true;
			}
			else
				return await base.OnProcessRequestAsync(r);
		} // proc OnProcessRequestAsync

		#endregion

		#region -- Configuration Load -------------------------------------------------

		private Action refreshConfig;
		private readonly TaskCompletionSource<bool> serviceInitialized = new TaskCompletionSource<bool>();

		private void InitConfiguration()
		{
			refreshConfig = InternalRefreshConfiguration;

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
			Queue.CancelCommand(refreshConfig);
			Queue.RegisterCommand(refreshConfig, 500);
		} // proc ReadConfiguration

		private void BeginReadConfiguration(DEConfigLoading config)
		{
			// Lade die aktuelle Konfiguration
			config.BeginReadConfiguration();

			// Wenn erfolgreich geladen, dann hole SubItems
			if (config.IsLoadedSuccessful)
			{
				// Lade die SubItems
				foreach (var cur in config.SubLoadings)
					BeginReadConfiguration(cur);

				// Aktiviere die Konfiguration
				if (config.IsConfigurationChanged)
				{
					var e = config.EndReadConfiguration();
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
			// write message for the configuration read
			if (!HasLog)
				LogMsg(EventLogEntryType.Information, "Reread configuration.");

			using (var log = this.LogProxy().GetScope(LogMsgType.Information, autoFlush: true, stopTime: true))
			{
				try
				{
					var xConfig = configuration.ParseConfiguration();

					// Zerlege die Konfiguration zur Validierung
					log.WriteLine("BEGIN Load configuration");

					using (log.Indent())
					{
						using (var config = new DEConfigLoading(this, log, xConfig, configuration.ConfigurationStamp))
							BeginReadConfiguration(config);
					}
					log.WriteLine("END Load configuration");

					if (!serviceInitialized.Task.IsCompleted)
						serviceInitialized.SetResult(true);
				}
				catch (Exception e)
				{
					log.WriteException(e);

					if (!serviceInitialized.Task.IsCompleted)
						serviceInitialized.SetResult(false);

					if (!HasLog || !Queue.IsQueueRunning) // Schreib die Fehlermeldung ins Windowsprotokoll
						LogMsg(e);
				}
			}

			FireSysEvent("refresh");
		} // proc InternalRefreshConfiguration

		public Task<bool> IsInitializedAsync()
			=> serviceInitialized.Task;

		#endregion

		#region -- Configuration Process ----------------------------------------------

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
				config.AddFirst(new XElement(xnLuaEngine)); // fixme: wrong position
			if (config.Element(xnHttp) == null)
				config.AddFirst(new XElement(xnHttp)); // fixme: wrong position

			// force des-server
			if (config.Elements(xnFiles).FirstOrDefault(x => String.Compare(x.GetAttribute("name", String.Empty), "des", StringComparison.OrdinalIgnoreCase) == 0) == null)
			{
				var currentAssembly = typeof(DEHttpServer).Assembly;
				var baseLocation = Path.GetDirectoryName(currentAssembly.Location);
				var alternativePaths = new string[]
				{
					Path.GetFullPath(Path.Combine(baseLocation, @"..\..\Resources\Http")),
					Path.GetFullPath(Path.Combine(baseLocation, @"..\..\..\ServerWebUI"))
				};

				var xFiles = new XElement(xnResources,
					new XAttribute("name", "des"),
					new XAttribute("displayname", "Data Exchange Server - Http"),
					new XAttribute("base", ""),
					new XAttribute("assembly", currentAssembly.FullName),
					new XAttribute("namespace", "TecWare.DE.Server.Resources.Http"),
					new XAttribute("priority", 100)
				);

				if (Directory.Exists(alternativePaths[0]))
				{
					xFiles.Add(new XAttribute("nonePresentAlternativeExtensions", ".map .ts")); // exception for debug files
					xFiles.Add(
						alternativePaths.Select(c => new XElement(xnAlternativeRoot, c))
					);
				}

				// add security
				xFiles.Add(
					new XElement(xnSecurityDef,
						new XAttribute("filter", "des.html"),
						SecuritySys
					),
					new XElement(xnSecurityDef,
						new XAttribute("filter", "DEViewer.css"),
						SecuritySys
					),
					new XElement(xnSecurityDef,
						new XAttribute("filter", "DEViewer.js"),
						SecuritySys
					)
				);

				config.Add(xFiles);
			}
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
				foreach (var cur in server.Elements())
					if (cur.Name == xnServerSecurityGroup)
					{
						var name = cur.GetAttribute("name", String.Empty).ToLower();
						if (String.IsNullOrEmpty(name))
							Log.LogMsg(LogMsgType.Warning, "server/securitygroup benötigt Namen.");
						else
						{
							lock (securityGroups)
							{
								securityGroups[name] = securityGroups.TryGetValue(name, out var tmp)
									? CombineSecurityTokens(tmp, Procs.GetStrings(cur.Value))
									: Procs.GetStrings(cur.Value);
							}
						}
					}
			}
			securityGroupsVersion++;

			// do wait before continue
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
					Thread.Sleep(waitTimeout);
					LogMsg(EventLogEntryType.Information, "Continue load configure...");
				}
			}

			// initialize log system
			var newLogPath = server.GetAttribute("logpath", String.Empty);
			if (logPath == null)
			{
				if (String.IsNullOrEmpty(newLogPath))
					LogMsg(EventLogEntryType.Error, "server/@logpath wurde nicht nicht angegeben.");

				// create log directory
				this.logPath = newLogPath;
				var di = new DirectoryInfo(logPath);
				if (!di.Exists)
					di.Create();

				// create states
				propertyLogCount = new SimpleConfigItemProperty<int>(this, "tw_base_logcount", "Logs", ServerCategory, "Anzahl der Log-Dateien.", "{0:N0}", 0);
			}
			else if (String.Compare(newLogPath, logPath, true) != 0)
				Log.Warn("LogPath is changed. Restart needed.");

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
		private static MethodInfo registerSubItemMethodInfo = null;

		public bool LoadConfigExtension(IDEConfigLoading config, XElement load, string currentNamespace)
		{
			try
			{
				var type = configuration[load.Name]?.ClassType;
				if (type == null)
				{
					if (!String.IsNullOrEmpty(currentNamespace) && load.Name.NamespaceName != currentNamespace)
						Log.LogMsg(LogMsgType.Warning, "Type for element '{0}' is missing in schema.", load.Name);
					return false;
				}

				// Suche die Methode
				if (registerSubItemMethodInfo == null)
					registerSubItemMethodInfo = config.GetType().GetMethod("RegisterSubItem");

				// Erzeuge die Method
				var mi = registerSubItemMethodInfo.MakeGenericMethod(type);
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

			foreach (var resolve in server.Elements(xnServerResolve))
				RemoveAssemblyPath(resolve.Value);
		} // proc RemoveResolve

		private void WaitForService(string serviceName, int maxTime)
		{
			var cur = Array.Find(ServiceController.GetServices(), sv => String.Compare(sv.ServiceName, serviceName, true) == 0);

			if (cur == null)
				LogMsg(EventLogEntryType.Warning, String.Format("Service '{0}' not found...", serviceName));
			else
			{
				LogMsg(EventLogEntryType.Information, String.Format("Wait {1:N0}ms for service '{0}' start up...", serviceName, maxTime));
				while (cur.Status != ServiceControllerStatus.Running && maxTime > 0)
				{
					// serviceLog.RequestAdditionalTime(700); service is already started
					Thread.Sleep(500);
					cur.Refresh();
					maxTime -= 500;
				}
				if (cur.Status != ServiceControllerStatus.Running)
					LogMsg(EventLogEntryType.Warning, String.Format("Service '{0}' not started...", serviceName));
			}
		} // proc WaitForService

		#endregion

		#region -- User Dictionary ----------------------------------------------------

		private readonly object logNextUserLock = new();
		private string logNextUser = null;
		private int logNextUserCount = 0;

		public void RegisterUser(IDEUser user)
		{
			using (users.EnterWriteLock())
			{
				users.Register(user);

				UserListDescriptor.Default.Update(user.GetType());
			}
		} // proc RegisterUser

		public void UnregisterUser(IDEUser user)
		{
			using (users.EnterWriteLock())
				users.UnRegister(user);
		} // proc UnregisterUser

		public T[] GetUser<T>()
			where T : class, IDEUser
		{
			using (users.EnterReadLock())
				return users.List.OfType<T>().ToArray();
		} // func GetUser

		[LuaMember]
		public IDEUser[] GetUserByIdentity(IIdentity identity, bool exact = false)
		{
			using (users.EnterReadLock())
				return users.GetUserByIdentity(identity, exact).ToArray();
		} // func GetUserByIdentity

		[LuaMember]
		public IDEUser[] GetUserByName(string name)
		{
			using (users.EnterReadLock())
				return users.GetUserByName(name).ToArray();
		} // func GetUserByName

		private bool LogIdentity(IIdentity identity)
		{
			lock (logNextUserLock)
			{
				if (logNextUserCount <= 0)
					return false;

				logNextUserCount--;
				return logNextUser == null || String.Compare(identity.Name, logNextUser, StringComparison.OrdinalIgnoreCase) == 0;
			}
		} // func LogIdentity

		[
		LuaMember,
		DEConfigHttpAction("logNextIdentity", IsAutoLog = true, IsSafeCall = true, SecurityToken = SecuritySys)
		]
		public void LogNextIdentity(string name = null, int count = 1)
		{
			lock (logNextUserLock)
			{
				logNextUser = name;
				logNextUserCount = count;
			}
		} // proc LogNextIdentity

		public async Task<IDEAuthentificatedUser> AuthentificateUserAsync(IIdentity user)
		{
			using (users.EnterReadLock())
			using (var log = LogIdentity(user) ? Log.CreateScope(LogMsgType.Information, autoFlush: true, stopTime: true) : null)
			{
				if (log != null)
					log.WriteLine("Authentificate {0}", user.Name);

				foreach (var u in users.GetUserByIdentity(user, exact: false))
				{
					if (log != null)
						log.Write("- try {0} ({1}, {2})", u.Identity.Name, u.Identity.AuthenticationType, u.DisplayName);
					try
					{
						var r = await u.AuthentificateAsync(user);
						if (r != null)
						{
							if (log != null)
								log.WriteLine(" - Success.");
							return r;
						}
						else
						{
							if (log != null)
								log.WriteLine(" - Failed.");
						}
					}
					catch (Exception e)
					{
						if (log != null)
						{
							log.WriteLine(" - Failed.");
							using (log.Indent())
							{
								log.WriteException(e);
								log.SetType(LogMsgType.Warning, true);
							}
						}
					}
				}

				if (log != null)
					log.WriteLine("No match found.");
				return null;
			}
		} // func AuthentificateUser

		public Task<DECommonScope> CreateCommonScopeAsync(string userName = null)
			=> CreateCommonScopeAsync(userName != null ? new DESimpleIdentity(userName) : null);

		public async Task<DECommonScope> CreateCommonScopeAsync(IIdentity user = null)
		{
			var scope = new DECommonScope(this, user != null, null);
			if (user != null)
				await scope.AuthentificateUserAsync(user);
			return scope;
		} // proc SetUserTransactionContext

		#endregion

		#region -- Security Groups ----------------------------------------------------

		public string[] BuildSecurityTokens(params string[] securityTokens)
		{
			var tokens = new List<string>();
			if (securityTokens != null)
			{
				foreach (var t in securityTokens)
					BuildSecurityTokens(tokens, Procs.GetStrings(t), true);
			}
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
						// resolve group tokens
						lock (securityGroups)
						{
							if (securityGroups.TryGetValue(token, out var groupTokens))
								BuildSecurityTokens(tokens, groupTokens, true);
						}
					}
				}
			}
		} // proc BuildSecurityTokens

		public int SecurityGroupsVersion => securityGroupsVersion;

		#endregion

		#region -- Certificates -------------------------------------------------------

		private static bool TryGetHostName(object sender, out string hostName)
		{
			if (sender is WebRequest web)
			{
				hostName = web.RequestUri.Host;
				return true;
			}
			else
			{
				hostName = null;
				return false;
			}
		} // func TryGetHostName

		private static void LogCertificateFailure(LoggerProxy log, LogMsgType type, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors, string message)
		{
			var m = log.CreateScope(type);

			m.WriteLine(message);
			m.WriteLine();
			m.WriteLine("Remote Certificate:");
			if (certificate != null)
			{
				m.WriteLine("  Subject: {0}", certificate.Subject);
				m.WriteLine("  CertHash: {0}", certificate.GetCertHashString());
				m.WriteLine("  Expiration: {0}", certificate.GetExpirationDateString());
				m.WriteLine("  Serial: {0}", certificate.GetSerialNumberString());
				m.WriteLine("  Algorithm: {0}", certificate.GetKeyAlgorithmParametersString());
			}
			else
			{
				m.WriteLine("  <null>"); // no chance to skip
			}

			m.WriteLine("Chain:");

			var i = 0;
			if (chain != null)
			{
				foreach (var c in chain.ChainElements)
				{
					m.Write("  - {0}", c.Certificate?.Subject);
					if (i < chain.ChainStatus.Length)
						m.WriteLine(" --> {0}, {1}", chain.ChainStatus[i].Status, chain.ChainStatus[i].StatusInformation);
					else
						m.WriteLine();
					i++;
				}
			}
			else
				m.WriteLine("  <null>");
		} // proc LogCertificateFailure

		private static bool CheckCertifcateByRule(LoggerProxy log, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors, XElement x)
		{
			var allow = x.GetAttribute("allow", true);
			LogCertificateFailure(
			   log,
			   allow ? LogMsgType.Debug : LogMsgType.Error,
			   certificate, chain, sslPolicyErrors,
			   $"Remote certification rule {x.GetAttribute("id", "<id>")} used ({(allow ? "allow" : "deny")}."
		   );
			return allow;
		} // func CheckCertifcateByRule

		public bool CheckServerCertificate(LoggerProxy log, object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
		{
			// check for allowed certifcates
			if (certificate != null) // check for white/black listed certifcates
			{
				var hashString = new Lazy<string>(certificate.GetCertHashString);
				var commonName = new Lazy<string>(() => certificate.GetCommonName());

				var xServer = Config.Element(xnServer);
				if (xServer != null)
				{
					foreach (var x in xServer.Elements(xnServerCertificate))
					{
						var hash = x.GetAttribute("hash", null);
						var host = x.GetAttribute("cn", null);

						if (hash != null && String.Compare(hashString.Value, hash, StringComparison.OrdinalIgnoreCase) == 0)
							return CheckCertifcateByRule(log ?? Log, certificate, chain, sslPolicyErrors, x);
						else if (host != null && Procs.ValidateHostName(host, commonName.Value))
							return CheckCertifcateByRule(log ?? Log, certificate, chain, sslPolicyErrors, x);
					}
				}
			}

			// default checks
			switch (sslPolicyErrors)
			{
				case SslPolicyErrors.RemoteCertificateNameMismatch:
					if (certificate != null
						&& TryGetHostName(sender, out var hostName)
						&& certificate.ValidateHostName(hostName))
						return true;
					else
						goto default;
				case SslPolicyErrors.None:
					return true;
				case SslPolicyErrors.RemoteCertificateNotAvailable:
				case SslPolicyErrors.RemoteCertificateChainErrors:
				default:
					LogCertificateFailure(log ?? Log, LogMsgType.Error, certificate, chain, sslPolicyErrors, $"Remote certification validation failed ({sslPolicyErrors}).");
					return false;
			}
		} // func CheckServerCertificate

		private bool CheckServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
			=> CheckServerCertificate(null, sender, certificate, chain, sslPolicyErrors);

		#endregion

		#region -- IServiceProvider Member --------------------------------------------

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

		#region -- IDEServerResolver members ------------------------------------------

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
			lock (searchPaths)
			{
				path = GetAssemblyNormalizedPath(path);
				if (searchPaths.Exists(cur => String.Compare(cur, path, StringComparison.OrdinalIgnoreCase) == 0))
					return;

				searchPaths.Add(path);
			}
		} // proc AddAssemblyPath

		private void RemoveAssemblyPath(string path)
		{
			lock (searchPaths)
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
						{
							try
							{
								var testName = AssemblyName.GetAssemblyName(fi.FullName);
								if (String.Compare(testName.Name, fileName.Name, StringComparison.OrdinalIgnoreCase) == 0)
									fileList.Add(new KeyValuePair<AssemblyName, FileInfo>(testName, fi));
							}
							catch (Exception e)
							{
								Log.Warn(String.Format("Assembly '{0}' not loaded.", fi.FullName), e);
							}
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

		#region -- IDEBaseLog members -------------------------------------------------

		int IDEBaseLog.TotalLogCount
		{
			get => propertyLogCount?.Value ?? 0;
			set
			{
				if (propertyLogCount != null)
					propertyLogCount.Value = value;
			}
		} // prop TotalLogCount

		#endregion

		#region -- Lua Runtime --------------------------------------------------------

		#region -- class DELuaRuntime -------------------------------------------------

		private sealed class DELuaRuntime : LuaGlobal
		{
			private readonly DEServer server;
			private readonly DELuaIO io;

			public DELuaRuntime(Lua lua, DEServer server)
				: base(lua)
			{
				this.server = server ?? throw new ArgumentNullException(nameof(server));
				this.io = new DELuaIO();
			} // ctor

			/// <summary>Throw a exception.</summary>
			/// <param name="value"></param>
			/// <param name="message"></param>
			/// <returns></returns>
			[LuaMember("assert")]
			private new object LuaAssert(object value, string message)
			{
				if (!value.ChangeType<bool>())
					throw new LuaAssertRuntimeException(message ?? "Assertion failed!", 1, true);
				return value;
			} // func LuaAssert

			/// <summary>Throw a user error.</summary>
			/// <param name="message"></param>
			/// <param name="level"></param>
			[LuaMember("error")]
			public static new void LuaError(object message, int level)
			{
				// this method is needed to overwrite Lua-default
				LuaError(message, null, level);
			} // func LuaError

			/// <summary>Throw a user error.</summary>
			/// <param name="message"></param>
			/// <param name="arg1"></param>
			[LuaMember("error")]
			public static void LuaError(object message, object arg1, object arg2)
			{
				var level = 1;

				if (arg1 is int i && i > 1)  // validate stack trace level
					level = i;
				else if (arg2 is int i2 && i2 > 1)
					level = i2;

				if (message is Exception ex) // throw exception
				{
					if (arg1 is string text)
						throw new LuaUserRuntimeException(text, ex);
					else
						throw ex;
				}
				else if (message is string text) // generate exception with message
				{
					if (arg1 is Exception innerException)
						throw new LuaUserRuntimeException(text, innerException);
					else
						throw new LuaUserRuntimeException(text, level, true);
				}
				else
				{
					var messageText = message?.ToString() ?? "Internal error.";
					if (arg1 is Exception innerException)
						throw new LuaRuntimeException(messageText, innerException);
					else
						throw new LuaRuntimeException(messageText, level, true);
				}
			} // proc LuaError

			[LuaMember("await")]
			public LuaResult LuaAwait(object func)
			{
				int GetTaskType()
				{
					var t = func.GetType();
					if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>) && t.GetGenericArguments()[0].IsPublic)
						return 0;
					else if (typeof(Task).IsAssignableFrom(t))
						return 1;
					else
						return -1;
				};

				switch (func)
				{
					case null:
						throw new ArgumentNullException(nameof(func));
					case Task t:
						t.AwaitTask();
						switch (GetTaskType())
						{
							case 0:
								var genericArguments = t.GetType().GetGenericArguments();
								if (genericArguments[0] == typeof(LuaResult))
									return ((Task<LuaResult>)t).Result;
								else
								{
									dynamic d = t;
									return new LuaResult(d.Result);
								}
							case 1:
								return LuaResult.Empty;
							default:
								throw new NotSupportedException($"Could not await for task ({func.GetType().Name}).");
						}
					default:
						throw new ArgumentException($"The type '{func.GetType().Name}' is not awaitable.");
				}
			} // func LuaAwait

			#region -- Password - Handling --------------------------------------------

			private SecureString GetSecureString(object value, string parameterName)
			{
				switch (value)
				{
					case null:
						return null;
					case SecureString ss:
						return ss;
					case string s:
						return s.CreateSecureString();
					default:
						throw new ArgumentException("String expected", parameterName);
				}
			} // func GetSecureString

			/// <summary>Crypt a password.</summary>
			/// <param name="password">Password to crypt. Can be a plain text string or a secure string.</param>
			/// <param name="passwordType">Password type, (win64, win0x, usr64, usr0x or plain)</param>
			/// <returns>Crypt string.</returns>
			[LuaMember]
			public string EncodePassword(object password, string passwordType = null)
				=> Passwords.EncodePassword(GetSecureString(password, nameof(password)), passwordType);

			/// <summary>Decrypt a password.</summary>
			/// <param name="passwordValue">Crypt string to decode.</param>
			/// <returns>Plain password as string.</returns>
			[LuaMember]
			public string DecodePassword(string passwordValue)
				=> Passwords.DecodePassword(passwordValue).AsPlainText();

			/// <summary>Decrypt a password.</summary>
			/// <param name="passwordValue">Crypt string to decode.</param>
			/// <returns>Plain password as string.</returns>
			[LuaMember]
			public SecureString DecodePasswordSecure(string passwordValue)
				=> Passwords.DecodePassword(passwordValue);

			/// <summary>Create a password hash.</summary>
			/// <param name="password">Password to hash. Can be a plain text string or a secure string.</param>
			/// <returns>The password-hash as base64.</returns>
			[LuaMember]
			public string EncodePasswordHash(object password)
				=> Convert.ToBase64String(Passwords.HashPassword(GetSecureString(password, nameof(password))));

			/// <summary>Compare a password with a password-hash.</summary>
			/// <param name="password">Password as plain text.</param>
			/// <param name="passwordHash">Secret hash value.</param>
			/// <returns><c>true</c>, if secret and hash are equal.</returns>
			[LuaMember]
			public bool ComparePasswordHash(string password, string passwordHash)
				=> Passwords.PasswordCompare(password, passwordHash);

			#endregion

			#region -- Scope - Handling -----------------------------------------------

			/// <summary>Access the current scope.</summary>
			/// <returns>Returns the current scope or throws an exception.</returns>
			[LuaMember]
			public object GetCurrentScope()
				=> DEScope.GetScope(true);

			/// <summary>Access the current scope.</summary>
			/// <returns>Returns the current scope or <c>null</c>.</returns>
			[LuaMember]
			public object TryGetCurrentScope()
				=> DEScope.GetScope(false);

			/// <summary>Get a service of the current scope.</summary>
			/// <param name="serviceType">Type of the service</param>
			/// <returns>The service instance or an exception.</returns>
			[LuaMember]
			public object GetScopeService(object serviceType)
				=> DEScope.GetScopeService(ProcsDE.GetServiceType(serviceType, true), true);

			/// <summary>Get a service of the current scope.</summary>
			/// <param name="serviceType">Type of the service</param>
			/// <returns>The service instance or <c>null</c>.</returns>
			[LuaMember]
			public object TryGetScopeService(object serviceType)
				=> DEScope.GetScopeService(ProcsDE.GetServiceType(serviceType, false), false);

			[LuaMember]
			public object CreateCommonScope()
				=> new DECommonScope(GetCurrentScope() as IServiceProvider ?? server, false, null);

			#endregion

			[LuaMember("format")]
			private string LuaFormat(string text, params object[] args)
				=> String.Format(text, args);

			[LuaMember("LogMsgType")]
			private LuaType LuaLogMsgType => LuaType.GetType(typeof(LogMsgType));

			[LuaMember("UseNode")]
			private LuaResult UseNode(string path, object code, DEConfigItem item = null)
			{
				// find the node for execution
				if (String.IsNullOrEmpty(path))
					item = item ?? server;
				else if (path[0] == '/')
					item = ProcsDE.UseNode(server, path, 1);
				else
					item = ProcsDE.UseNode(item ?? server, path, 0);

				// execute code on node
				using (item.EnterReadLock())
					return new LuaResult(Lua.RtInvoke(
						code ?? throw new ArgumentNullException(nameof(code)),
						item
					));
			} // func UseNode

			protected override void OnPrint(string text)
				=> server.Log.LogMsg(LogMsgType.Debug, text);

			[LuaMember]
			public DELuaIO IO => io;
		} // class DELuaRuntime

		#endregion

		#region -- class DELuaIO ------------------------------------------------------

		/// <summary>IO package for lua. It gives access to the static methods from <see cref="DEFile"/>.</summary>
		/// <remarks>Does not support the file handles, because of the async.</remarks>
		public sealed class DELuaIO : LuaTable
		{
			private static LuaResult SafeIO(Func<LuaResult> action)
			{
				try
				{
					return action();
				}
				catch (IOException e)
				{
					return new LuaResult(false, e);
				}
			} // func SafeIO

			/// <summary>Get filename from an object.</summary>
			/// <param name="file"></param>
			/// <returns></returns>
			public static string GetFullFileName(object file)
				=> file is FileSystemInfo fsi ? fsi.FullName : file as string;

			/// <summary>Open a lua file handle for a file. The file will be added to the current scope. Files they 
			/// are open for write, the changes to the file will be added to the transaction.</summary>
			/// <param name="filename">Name of the file to open or create.</param>
			/// <param name="mode">Mode to open.
			/// `r`
			/// :  Open the file for read access.
			/// `w`
			/// :  Open the file for write access.
			/// `b`
			/// :  Use binary access to the file.
			/// `t`
			/// :  Enforce the transaction.
			/// `m`
			/// :  Transaktion log is only written in memory.
			/// </param>
			/// <param name="encoding">Char encoding for the text access.</param>
			/// <returns><see cref="LuaFile"/>-Handle.</returns>
			[LuaMember("open")]
			public LuaResult Open(object filename, string mode = "r", Encoding encoding = null)
			{
				try
				{
					var forWrite = mode.IndexOfAny(new char[] { '+', 'w' }) > 0;
					var forTrans = mode.IndexOf('t') >= 0;
					if (forTrans && forWrite)
					{
						return new LuaResult(
							LuaFileStream.OpenFile(
								OpenRaw(GetFullFileName(filename), mode.IndexOf('m') >= 0), encoding
							)
						);
					}
					else // only read, no transaction
					{
						var file = LuaFileStream.OpenFile(GetFullFileName(filename), mode, encoding ?? Encoding.UTF8);
						DEScope.GetScopeService<IDECommonScope>(forTrans)?.RegisterDispose(file);
						return new LuaResult(file);
					}
				}
				catch (Exception e)
				{
					return new LuaResult(null, e.Message);
				}
			} // func Open

			/// <summary>Open a stream to an file and actived write transactions.</summary>
			/// <param name="filename">Name of the file.</param>
			/// <param name="inMemory">Transaktion log is only written in memory.</param>
			/// <returns>A <see cref="Stream"/> that supports transactions.</returns>
			[LuaMember("openraw")]
			public DETransactionStream OpenRaw(object filename, bool inMemory)
			{
				var stream = inMemory
					? DEFile.OpenInMemoryAsync(GetFullFileName(filename)).AwaitTask()
					: DEFile.OpenCopyAsync(GetFullFileName(filename)).AwaitTask();

				DEScope.GetScopeService<IDECommonScope>(true).RegisterDispose(stream);

				return stream;
			} // func OpenRaw

			/// <summary>Create a new tmp file.</summary>
			/// <param name="encoding">Char encoding for the text access.</param>
			/// <returns><see cref="LuaFile"/>-Handle.</returns>
			[LuaMember("tmpfilenew")]
			public LuaFile OpenTemp(Encoding encoding)
			{
				var file = LuaTempFile.Create(Path.GetTempFileName(), encoding ?? Encoding.UTF8);

				DEScope.GetScopeService<IDECommonScope>(true).RegisterDispose(file);

				return file;
			} // func OpenTemp

			/// <summary>Delete a file within a transaction.</summary>
			/// <param name="fileName"></param>
			/// <param name="throwException"></param>
			/// <returns></returns>
			[LuaMember("delete")]
			public static LuaResult DeleteFile(object fileName, bool throwException = true)
			{
				if (throwException)
				{
					if (fileName is FileInfo fi)
						DEFile.DeleteAsync(fi).AwaitTask();
					else
						DEFile.DeleteAsync(GetFullFileName(fileName)).AwaitTask();
					return new LuaResult(true);
				}
				else
					return SafeIO(() => DeleteFile(fileName, true));
			} // func DeleteFile

			private static DEFileTargetExists GetTargetExists(object targetExists, bool throwException)
			{
				switch (targetExists)
				{
					case null:
						return throwException ? DEFileTargetExists.Error : DEFileTargetExists.Ignore;
					case bool k:
						return k ? DEFileTargetExists.KeepTarget : DEFileTargetExists.Error;
					case DEFileTargetExists r:
						return r;
					case int i:
						return (DEFileTargetExists)i;
					case string s:
						if (s.Length > 0)
						{
							switch (Char.ToLower(s[0]))
							{
								case 'e':
									return DEFileTargetExists.Error;
								case 'i':
									return DEFileTargetExists.Ignore;
								case 'k':
									return DEFileTargetExists.KeepTarget;
								case 'a':
									return DEFileTargetExists.OverwriteAlways;
								case 'n':
									return DEFileTargetExists.OverwriteNewer;
							}
						}
						goto default;
					default:
						if (throwException)
							throw new ArgumentOutOfRangeException(nameof(targetExists), targetExists, "Could not interpret argument.");
						return DEFileTargetExists.Ignore;
				}
			} // func GetTargetExists

			/// <summary>Copy a file within a transaction.</summary>
			/// <param name="sourceFileName"></param>
			/// <param name="destinationName"></param>
			/// <param name="throwException"></param>
			/// <param name="targetExists"></param>
			/// <returns></returns>
			[LuaMember("copy")]
			public static LuaResult CopyFile(object sourceFileName, object destinationName, bool throwException = true, object targetExists = null)
			{
				if (throwException)
				{
					var onExists = GetTargetExists(targetExists, throwException);
					var destinationFileName = sourceFileName is FileInfo fiSourceFile && destinationName is FileSystemInfo fsiDestination
						? DEFile.CopyAsync(fiSourceFile, fsiDestination, onExists, null).AwaitTask().FullName
						: DEFile.CopyAsync(GetFullFileName(sourceFileName), GetFullFileName(destinationName), onExists, null).AwaitTask();

					return new LuaResult(true, destinationFileName);
				}
				else
					return SafeIO(() => CopyFile(sourceFileName, destinationName, true, targetExists));
			} // func CopyFile

			/// <summary>Move a file within a transaction.</summary>
			/// <param name="sourceFileName"></param>
			/// <param name="destinationName"></param>
			/// <param name="throwException"></param>
			/// <param name="targetExists"></param>
			/// <returns></returns>
			[LuaMember("move")]
			public static LuaResult MoveFile(object sourceFileName, object destinationName, bool throwException = true, object targetExists = null)
			{
				if (throwException)
				{
					var onExists = GetTargetExists(targetExists, throwException);
					var destinationFileName = sourceFileName is FileInfo fiSourceFile && destinationName is FileSystemInfo fsiDestination
							? DEFile.MoveAsync(fiSourceFile, fsiDestination, onExists, null).AwaitTask().FullName
							: DEFile.MoveAsync(GetFullFileName(sourceFileName), GetFullFileName(destinationName), onExists, null).AwaitTask();

					return new LuaResult(true, destinationFileName);
				}
				else
					return SafeIO(() => MoveFile(sourceFileName, destinationName, true, targetExists));
			} // func MoveFile

			/// <summary>Test if the file exists</summary>
			/// <param name="fileName"></param>
			/// <returns></returns>
			[LuaMember("fileExists")]
			public static LuaResult Exists(object fileName)
				=> new LuaResult(fileName is FileInfo fi ? fi.Exists : File.Exists(fileName.ToString()));

			/// <summary>Run a process and redirect output.</summary>
			/// <param name="program"></param>
			/// <param name="mode"></param>
			/// <returns></returns>
			[LuaMember("popen")]
			public static LuaFile OpenProcess(string program, string mode = "r")
				=> new LuaFilePackage().popen(program, mode);

			/// <summary>Translate a local path to an remotePath.</summary>
			/// <param name="localPath"></param>
			/// <returns></returns>
			[LuaMember]
			public static LuaResult LocalToUnc(string localPath)
			{
				var r = ShareInfo.TryLocalToUnc(localPath, out var uncPath);
				return new LuaResult(uncPath, r);
			} // func LocalToUnc

			/// <summary>Translate a remote path to an unc path.</summary>
			/// <param name="uncPath"></param>
			/// <returns></returns>
			[LuaMember]
			public static LuaResult UncToLocal(string uncPath)
			{
				var r = ShareInfo.TryUncToLocal(uncPath, out var localPath);
				return new LuaResult(localPath, r);
			} // func LocalToUnc

			/// <summary>Return all shares.</summary>
			[LuaMember]
			public static IEnumerable<ShareInfo> UncShares => ShareInfo.Shares;

			/// <summary></summary>
			/// <param name="path"></param>
			/// <returns></returns>
			[LuaMember]
			public string IncludeTrailingBackslash(string path)
				=> Procs.IncludeTrailingBackslash(path);

			/// <summary></summary>
			/// <param name="path"></param>
			/// <returns></returns>
			[LuaMember]
			public string ExcludeTrailingBackslash(string path)
				=> Procs.ExcludeTrailingBackslash(path);
		} // func DELuaIO

		#endregion

		#region -- class DELuaHttp ----------------------------------------------------

		public sealed class DELuaHttp : LuaTable
		{
			#region -- GetWebRequestScope ---------------------------------------------

			private static IDEWebRequestScope CheckContextArgument(IDEWebRequestScope r)
				=> r ?? throw new ArgumentNullException(nameof(r), $"In the current context {nameof(IDEWebRequestScope)} is missing.");

			[LuaMember]
			public static IDEWebRequestScope GetWebRequestScope()
				=> CheckContextArgument(DEScope.GetScopeService<IDEWebRequestScope>(true));

			#endregion

			#region -- Text Reader/Writer ---------------------------------------------

			[LuaMember]
			public static TextWriter CreateTextWriter(string mimeType = MimeTypes.Text.Plain)
			{
				var r = GetWebRequestScope();
				return r.GetOutputTextWriter(mimeType);
			} // func CreateTextReader

			[LuaMember]
			public static void SetText(string content, string mimeType = MimeTypes.Text.Plain)
			{
				using var tw = CreateTextWriter(mimeType);
				tw.Write(content);
			} // proc SetText

			[LuaMember]
			public static TextReader CreateTextReader()
			{
				var r = GetWebRequestScope();
				return r.GetInputTextReader();
			} // func CreateTextReader

			[LuaMember]
			public static string GetText()
			{
				using var tr = CreateTextReader();
				return tr.ReadToEnd();
			} // func GetText

			#endregion

			#region -- Xml Reader/Writer ----------------------------------------------

			/// <summary>Creates a XmlWriter for the output stream</summary>
			/// <returns></returns>
			[LuaMember]
			public static XmlWriter CreateXmlWriter()
				=> XmlWriter.Create(CreateTextWriter(MimeTypes.Text.Xml), Procs.XmlWriterSettings);

			/// <summary>Writes the XElement in the output stream.</summary>
			/// <param name="x"></param>
			[LuaMember]
			public static void WriteXml(XElement x)
			{
				using var xml = CreateXmlWriter();
				x.WriteTo(xml);
			} // proc WriteXml

			/// <summary>Creates a XmlReader for the input stream.</summary>
			/// <returns></returns>
			[LuaMember]
			public static XmlReader CreateXmlReader()
				=> XmlReader.Create(CreateTextReader(), Procs.XmlReaderSettings);

			/// <summary>Gets the input stream as an XElement.</summary>
			/// <returns></returns>
			[LuaMember]
			public static XElement GetXml()
				=> GetWebRequestScope().GetXml();

			#endregion

			#region -- Table Reader/Writer --------------------------------------------

			/// <summary>Write the table in the output stream.</summary>
			/// <param name="t"></param>
			[LuaMember]
			public static void WriteTable(LuaTable t)
			{
				var r = GetWebRequestScope();
				r.WriteLuaTable(t);
			} // proc WriteTable

			/// <summary>Gets the input stream as an lua-table.</summary>
			/// <returns></returns>
			[LuaMember]
			public static LuaTable GetTable()
				=> GetWebRequestScope().GetTable();

			#endregion

			#region -- Error, SetStatus -----------------------------------------------

			/// <summary>Raise <see cref="HttpResponseException"/>.</summary>
			/// <param name="code"></param>
			/// <param name="text"></param>
			[LuaMember]
			public void Error(HttpStatusCode code, string text = null, Exception innerException = null)
				=> throw new HttpResponseException(code, text, innerException);

			/// <summary>Setzt den Status des Http-Requests.</summary>
			/// <param name="code"></param>
			/// <param name="text"></param>
			[LuaMember]
			public void SetStatus(HttpStatusCode code, string text = null)
				=> GetWebRequestScope().SetStatus(code, text);

			/// <summary>Setzt des Staus des Http-Requests auf ein Redirect.</summary>
			/// <param name="url"></param>
			/// <param name="statusDescription"></param>
			[LuaMember]
			public void Redirect(string url, string statusDescription = null)
				=> GetWebRequestScope().Redirect(url, statusDescription);

			#endregion

			#region -- Client ---------------------------------------------------------

			[LuaMember]
			public object CreateClient(string baseUri)
			{
				var uri = new Uri(baseUri, UriKind.Absolute);
				if (uri.Scheme == "http" || uri.Scheme == "https")
					return DEHttpClient.Create(uri);
				else if (uri.Scheme == "ftp" || uri.Scheme == "ftps")
					return new FtpClient(uri);
				else
					throw new ArgumentException("Invalid scheme");
			} // func CreateClient

			#endregion
		} // class DELuaHttp

		#endregion

		private LuaGlobal luaRuntime = null;
		private readonly DELuaHttp luaHttp = new DELuaHttp();

		/// <summary>Obsolete</summary>
		/// <param name="target"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		[LuaMember("safecall"), Obsolete("NeoLua do end exception handling.")]
		private LuaResult LuaSafeCall(object target, params object[] args)
		{
			try
			{
				return new LuaResult(true, Lua.RtInvoke(target, args));
			}
			catch (TargetInvocationException e)
			{
				return new LuaResult(false, e.InnerException.Message, e.InnerException);
			}
			catch (Exception e)
			{
				return new LuaResult(false, e.Message, e);
			}
		} // func LuaSafeCall

		internal void UpdateLuaRuntime(Lua lua)
			=> this.luaRuntime = new DELuaRuntime(lua, this);

		protected override object OnIndex(object key)
			=> base.OnIndex(key) ?? luaRuntime?.GetValue(key);

		/// <summary>Basic lua runtime methods.</summary>
		[LuaMember("Basic")]
		public LuaTable LuaRuntime { get => luaRuntime; set { } }
		/// <summary>Library for easy creation of http-results or requests.</summary>
		[LuaMember]
		public LuaTable Http { get => luaHttp; set { } }

		#endregion

		string IDEServer.LogPath => logPath;
		IDEConfigurationService IDEServer.Configuration => configuration;

		/// <summary>Get access to the background worker queue.</summary>
		public IDEServerQueue Queue => queue;
		/// <summary>Icon of the DES.</summary>
		public override string Icon => "/images/des16.png";

		// -- Static --------------------------------------------------------------

		/// <summary>Current active running instance.</summary>
		public static DEServer Current { get; private set; }
	} // class DEServer
}
