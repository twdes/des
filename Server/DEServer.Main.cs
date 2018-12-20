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
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using CommandLine;
using CommandLine.Text;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	internal partial class DEServer
	{
		private const string servicePrefix = "Tw_DES_";

		#region -- interface IServiceLog ----------------------------------------------

		private interface IServiceLog
		{
			void LogMessage(EventLogEntryType type, string message, int id, short category, byte[] rawData);
			void RequestAdditionalTime(int milliseconds);
		} // interface IServiceLog

		#endregion

		#region -- class Service ------------------------------------------------------

		private sealed class Service : ServiceBase, IServiceLog
		{
			private readonly DEServer app;
			private EventLog log;

			public Service(string serviceName, DEServer app)
			{
				this.app = app ?? throw new ArgumentNullException(nameof(app));
				this.app.ServiceLog = this;

				this.ServiceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
				this.AutoLog = true;
				this.CanHandlePowerEvent = false;
				this.CanHandleSessionChangeEvent = false;
				this.CanPauseAndContinue = false;
				this.CanShutdown = false;
				this.CanStop = true;
			} // ctor

			protected override void OnStart(string[] args)
			{
				base.OnStart(args);
				app.OnStart();
			} // proc OnStart

			protected override void OnStop()
			{
				app.OnStop();
				base.OnStop();
			} // proc OnStop

			void IServiceLog.LogMessage(EventLogEntryType type, string message, int id, short category, byte[] rawData)
			{
				if (String.IsNullOrEmpty(message))
					return;

				try
				{
					if (log == null)
					{
						if (!EventLog.SourceExists(ServiceName))
							EventLog.CreateEventSource(ServiceName, "Application");
						log = new EventLog("Application") { Source = ServiceName };
					}

					// Die Nachrichten sind längen beschränkt, wir schneiden den Rest einfach ab.
					if (message.Length > 20000)
						message = message.Substring(0, 20000) + Environment.NewLine + "[Message truncated]";

					log.WriteEntry(message, type, id, category, rawData);
				}
				catch (Exception e)
				{
					Debug.Print(e.GetMessageString());
				}
			}
		} // class Service

		#endregion

		#region -- class ConsoleLog ---------------------------------------------------

		private sealed class ConsoleLog : IServiceLog
		{
			public void LogMessage(EventLogEntryType type, string message, int id, short category, byte[] rawData)
			{
				switch (type)
				{
					case EventLogEntryType.Error:
					case EventLogEntryType.FailureAudit:
						Console.ForegroundColor = ConsoleColor.Red;
						Console.Error.WriteLine(message);
						break;
					case EventLogEntryType.SuccessAudit:
					case EventLogEntryType.Information:
						Console.ForegroundColor = ConsoleColor.Gray;
						Console.WriteLine(message);
						break;
					case EventLogEntryType.Warning:
						Console.ForegroundColor = ConsoleColor.DarkGreen;
						Console.WriteLine(message);
						break;
				}
				Console.ForegroundColor = ConsoleColor.Gray;
			} // proc LogMessage

			public void RequestAdditionalTime(int milliseconds)
				=> LogMessage(EventLogEntryType.Information, String.Format("Wait additional: {0}ms", milliseconds), 0, 0, null);
		} // class ConsoleLog

		#endregion

		#region -- class DebugLog -----------------------------------------------------

		private sealed class DebugLog : IServiceLog
		{
			private readonly Action<ConsoleColor, string> writeMessage;

			public DebugLog(MethodInfo writeMessageMethodInfo)
			{
				writeMessage = (Action<ConsoleColor, string>)Delegate.CreateDelegate(typeof(Action<ConsoleColor, string>), writeMessageMethodInfo);
			} // ctor

			public void LogMessage(EventLogEntryType type, string message, int id, short category, byte[] rawData)
			{
				switch (type)
				{
					case EventLogEntryType.Error:
					case EventLogEntryType.FailureAudit:
						writeMessage(ConsoleColor.Red, message);
						break;
					case EventLogEntryType.SuccessAudit:
					case EventLogEntryType.Information:
						writeMessage(ConsoleColor.Gray, message);
						break;
					case EventLogEntryType.Warning:
						writeMessage(ConsoleColor.DarkGreen, message);
						break;
				}
			} // proc LogMessage

			public void RequestAdditionalTime(int milliseconds)
				=> LogMessage(EventLogEntryType.Information, String.Format("Wait additional: {0}ms", milliseconds), 0, 0, null);
		} // class DebugLog

		#endregion

		#region -- class ServerOptions ------------------------------------------------

		private abstract class ServerOptions
		{
			private string configurationFile = null;
			private string serviceName = null;

			protected ServerOptions()
			{
			} // ctor

			protected ServerOptions(ServerOptions options)
			{
				configurationFile = options.configurationFile;
				serviceName = options.serviceName;
				Properties = options.Properties; // use the reference 
			} // ctor

			public void Validate()
			{
				if (!ValidateServiceName(ServiceName))
					throw new ArgumentException("Invalid service name.");
				if (!File.Exists(ConfigurationFile))
					throw new ArgumentException("Konfigurationsdatei nicht gefunden.");
			} // proc Validate

			[Option('c', "config", HelpText = "Path to the configuration file.", Required = true)]
			public string ConfigurationFile
			{
				get => configurationFile;
				set => configurationFile = String.IsNullOrEmpty(value) ? null : Path.GetFullPath(value);
			} // prop ConfigurationFile

			private string GetDefaultServiceName() => String.IsNullOrEmpty(ConfigurationFile) ? null : Path.GetFileNameWithoutExtension(ConfigurationFile);

			[Option('n', "name", HelpText = "Name of the service.")]
			public string ServiceName
			{
				get => serviceName ?? GetDefaultServiceName();
				set => serviceName = GetDefaultServiceName() == value ? null : value;
			} // prop ServiceName		 

			[Value(0, HelpText = "Properties for the configuration parser (e.g. key0=v0 key1=v1).", MetaName = "properties")]
			public IEnumerable<string> Properties { get; set; }
		} // class ServerOptions

		#endregion

		#region -- class RunOptions ---------------------------------------------------

		[Verb("run", HelpText = "Executes a configuration.")]
		private sealed class RunOptions : ServerOptions
		{
			public RunOptions()
			{
			} // ctor

			public RunOptions(ServerOptions options)
				: base(options)
			{
			} // ctor

			[Option('v', "verbose", Default = false, HelpText = "Starts the service in console mode.")]
			public bool Verbose { get; set; } = false;
		} // class RunOptions

		#endregion

		#region -- class RegisterOptions ----------------------------------------------

		[Verb("register", HelpText = "Installs a configuration as a service.")]
		private class RegisterOptions : ServerOptions
		{
		} // class RegisterOptions

		#endregion

		#region -- class UnregisterOptions --------------------------------------------

		[Verb("unregister", HelpText = "Uninstalls the configuration from the service controll manager.")]
		private sealed class UnregisterOptions
		{
			[Option("name", HelpText = "Name of the service.", Required = true)]
			public string ServiceName { get; set; }
		} // class UnregisterOptions

		#endregion

		private IServiceLog serviceLog = null;

		#region -- LogMsg methods -----------------------------------------------------

		/// <summary>Writes a exception to the event protocol.</summary>
		/// <param name="e"></param>
		public void LogMsg(Exception e)
			=> LogMsg(EventLogEntryType.Error, e.GetMessageString());

		/// <summary>Writes a event to the windows log.</summary>
		/// <param name="type"></param>
		/// <param name="sMessage"></param>
		/// <param name="id"></param>
		/// <param name="category"></param>
		/// <param name="rawData"></param>
		public void LogMsg(EventLogEntryType type, string sMessage, int id = 0, short category = 0, byte[] rawData = null)
			=> serviceLog?.LogMessage(type, sMessage, id, category, rawData);

		#endregion

		/// <summary>Access to the service log.</summary>
		private IServiceLog ServiceLog { get => serviceLog; set => serviceLog = value; }

		// -- Static ----------------------------------------------------------

		#region -- Service registration -----------------------------------------------

		private static void RegisterService(string name, string commandLine)
		{
			var hScm = NativeMethods.OpenSCManager(null, null, 0x01 | 0x02); // SC_MANAGER_CREATE_SERVICE
			if (hScm == IntPtr.Zero)
				throw new Win32Exception();

			try
			{
				commandLine = String.Concat("\"", Assembly.GetExecutingAssembly().Location, "\" ", commandLine);

				var serviceName = servicePrefix + name;
				var hService = NativeMethods.OpenService(hScm, serviceName, 0x01 | 0x02);
				if (hService == IntPtr.Zero)
				{
					hService = NativeMethods.CreateService(hScm,
							 serviceName,
							 "Data Exchange Server (" + name + ")",
							 0x1FF, // Full access
							 0x10, // own process
							 3, // manuell start
							 1, // error normal
							 commandLine,
							 null,
							 null,
							 "HTTP", // Http.sys
							 null,
							 null);
				}
				else
				{
					if (!NativeMethods.ChangeServiceConfig(hService, 0x20, 3, 1, commandLine, null, IntPtr.Zero, "HTTP".ToArray(), null, null, "Data Exchange Server (" + name + ")"))
						throw new Win32Exception();
				}
				if (hService == IntPtr.Zero)
					throw new Win32Exception();

				try
				{
					var s = new NativeMethods.SERVICE_DESCRIPTION
					{
						description = Marshal.StringToHGlobalUni("Data Exchange Server is the backend for the CPS infrastructure.")
					};
					try
					{
						if (!NativeMethods.ChangeServiceConfig2(hService, 1, ref s))
							throw new Win32Exception();
					}
					finally
					{
						Marshal.FreeHGlobal(s.description);
					}
				}
				finally
				{
					NativeMethods.CloseServiceHandle(hService);
				}
			}
			finally
			{
				NativeMethods.CloseServiceHandle(hScm);
			}
		} // proc RegisterService

		private static void UnregisterService(string name)
		{
			var hScm = NativeMethods.OpenSCManager(null, null, 0x01); // SC_MANAGER_CONNECT
			if (hScm == IntPtr.Zero)
				throw new Win32Exception();

			try
			{
				var hService = NativeMethods.OpenService(hScm, servicePrefix + name, 0x00010000); // DELETE
				if (hService != IntPtr.Zero)
					try
					{
						if (!NativeMethods.DeleteService(hService))
							throw new Win32Exception();
					}
					finally
					{
						NativeMethods.CloseServiceHandle(hService);
					}
			}
			finally
			{
				NativeMethods.CloseServiceHandle(hScm);
			}
		} // proc UnregisterService

		private static bool ValidateServiceName(string sName)
		{
			const string csValidChar = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

			for (var i = 0; i < sName.Length; i++)
			{
				if (csValidChar.IndexOf(sName[i]) == -1)
					return false;
			}

			return true;
		} // func ValidateServiceName

		#endregion

		#region -- InvokeDebugger -----------------------------------------------------

		private bool InvokeDebugger()
		{
			// wait for service
			if (!IsInitializedAsync().Result)
				return false;

			// load debugger implementation
			var simpleDbgAssembly = ResolveAssembly("DESimpleDbg", typeof(DEServer).Assembly);
			if (simpleDbgAssembly == null)
			{
				LogMsg(EventLogEntryType.Warning, "Could not locate DESimpleDbg.exe.");
				return false;
			}

			var debuggerProgram = simpleDbgAssembly.GetType("TecWare.DE.Server.Program", true);

			var runProgramAsync = debuggerProgram.GetRuntimeMethod("RunDebugProgram", new Type[] { typeof(Uri), typeof(ICredentials), typeof(bool) })
				?? throw new ArgumentException("RunDebugProgramAsync not found.");
			var writeMessage = debuggerProgram.GetRuntimeMethod("WriteMessage", new Type[] { typeof(ConsoleColor), typeof(string) })
				?? throw new ArgumentException("WriteMessage not found.");

			// check debugging
			var luaEngine = this.GetService<IDELuaEngine>(true);
			if (!luaEngine.IsDebugAllowed)
			{
				LogMsg(EventLogEntryType.Warning, "Debugging is deactivated.");
				return false;
			}

			var http = this.GetService<IDEHttpServer>(true);
			var uri = new Uri(http.DefaultBaseUri, ((IDEConfigItem)luaEngine).Name);

			// switch logging
			ServiceLog = new DebugLog(writeMessage);

			// invoke the debugger
			runProgramAsync.Invoke(null, new object[] { uri, null, true });
			return true;
		} // proc InvokeDebugger

		#endregion

		#region -- Main ---------------------------------------------------------------

		public static void AddToProcessEnvironment(string path)
		{
			var pathList = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process).Split(';');
			if (Array.FindIndex(pathList, p => String.Compare(p, path, StringComparison.OrdinalIgnoreCase) == 0) == -1)
				Environment.SetEnvironmentVariable("PATH", String.Join(";", pathList) + ";" + path);
		} // proc AddToProcessEnvironment

		/// <summary>Eintrittspunkt in die Anwendung.</summary>
		/// <param name="args">Parameter die übergeben werden. Ungenutzt.</param>
		public static void Main(string[] args)
		{
#if DEBUG
			var readlineAtTheEnd = false;
#endif

			var printHeader = new Action(() =>
			{
				Console.WriteLine(HeadingInfo.Default.ToString());
				Console.WriteLine(CopyrightInfo.Default.ToString());
#if DEBUG
				readlineAtTheEnd = true;
#endif
			});

			try
			{
				var parser = new Parser(s =>
				{
					s.CaseSensitive = false;
					s.IgnoreUnknownArguments = false;
					s.HelpWriter = null;
					s.ParsingCulture = CultureInfo.InvariantCulture;
				});

				// work with arguments
				var r = parser.ParseArguments(args, new Type[] { typeof(RunOptions), typeof(RegisterOptions), typeof(UnregisterOptions) });
				r.MapResult<RunOptions, RegisterOptions, UnregisterOptions, bool>(
					opts =>  // run
					{
						// print heading
						if (opts.Verbose)
							printHeader();

						// validate arguments
						opts.Validate();

						// execute the service
						var app = new DEServer(opts.ConfigurationFile, opts.Properties);
						if (opts.Verbose) // Run the console version of the service
						{
							app.ServiceLog = new ConsoleLog();
							app.OnStart();
							Console.WriteLine("Service is started.");
							if (!app.InvokeDebugger())
								Console.ReadLine();
							app.OnStop();
						}
						else
						{
							ServiceBase.Run(new Service(servicePrefix + opts.ServiceName, app)); // Start as a windows service
						}

						return true;
					},
					opts => // register
					{
						// print heading
						printHeader();

						// validate arguments
						opts.Validate();

						// form the run command line
						var runOpts = new RunOptions(opts);
						var serviceCommandLine = parser.FormatCommandLine(runOpts, o => { o.PreferShortName = true; });

						// register the service
						RegisterService(opts.ServiceName, serviceCommandLine);
						Console.WriteLine("Service '{0}{1}' created/modified.", servicePrefix, opts.ServiceName);

						return true;
					},
					opts => // unregister
					{
						// print heading
						printHeader();

						UnregisterService(opts.ServiceName);
						Console.WriteLine("Service '{0}{1}' removed.", servicePrefix, opts.ServiceName);

						return true;
					},
					errors =>
					{
						// print help
						var help = CommandLine.Text.HelpText.AutoBuild(r);
						help.Copyright = typeof(DEServer).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

						if (errors.FirstOrDefault(e => e is HelpVerbRequestedError) != null)
						{
							help.AddPreOptionsLine(Environment.NewLine + "Usage:");
							help.AddPreOptionsLine("  DEServer.exe run -v -c [configuration file] -n [name] {properties}");
							help.AddPreOptionsLine("  DEServer.exe register -c [configuration file] -n [name] {properties}");
							help.AddPreOptionsLine("  DEServer.exe unregister --name [name] ");
						}
						if (errors.FirstOrDefault(e => e is VersionRequestedError) != null)
						{
							void AddVersionForAssembly(Assembly assembly)
							{
								var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString();
								var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";
								help.AddPostOptionsLine($"  {assembly.GetName().Name}: {informationalVersion} ({fileVersion})");
							}
							help.AddPostOptionsLine("Assembly version:");
							AddVersionForAssembly(typeof(DEServer).Assembly);
							AddVersionForAssembly(typeof(Procs).Assembly);
							AddVersionForAssembly(typeof(Neo.IronLua.Lua).Assembly);
						}

						Console.WriteLine(help.ToString());
						return false;
					}
				);
			}
			catch (Exception e)
			{
				Console.WriteLine(e.GetMessageString());
#if DEBUG
				if (readlineAtTheEnd)
					Console.ReadLine();
#endif
			}
		} // proc Main

		#endregion
	} // class DEServer
}
