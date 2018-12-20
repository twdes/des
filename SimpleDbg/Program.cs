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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommandLine;
using CommandLine.Text;
using Neo.Console;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class SimpleDebugArguments ---------------------------------------------

	/// <summary>Command line arguments for data exchange simple debug.</summary>
	public class SimpleDebugArguments
	{
		[Option('o', HelpText = "Uri to the server.", Required = true)]
		public string Uri { get; set; }
		[Option('u', "username", HelpText = "Authentification user", Required = false)]
		public string UserName { get; set; }
		[Option('p', "password", HelpText = "Authentification user", Required = false)]
		public string Password { get; set; }

		[Option("wait", HelpText = "Wait time before, the connection will be established (in ms).")]
		public int Wait { get; set; } = 0;
	} // class SimpleDebugArguments

	#endregion

	#region -- class InteractiveCommandAttribute --------------------------------------

	/// <summary>Interactive command attribute</summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class InteractiveCommandAttribute : Attribute
	{
		public InteractiveCommandAttribute(string name)
		{
			this.Name = name ?? throw new ArgumentNullException(nameof(name));
		} // ctor

		public string Name { get; }
		public string Short { get; set; }
		public string HelpText { get; set; }
	} // class InteractiveCommandAttribute

	#endregion

	/// <summary></summary>
	public static class Program
	{
		private static readonly Regex commandSyntax = new Regex(@"\:(?<cmd>\w+)(?:\s+(?<args>(?:\`[^\`]*\`)|(?:[^\s]*)))*", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static ConsoleApplication app = ConsoleApplication.Current;

		private static DebugRunScriptResult lastScriptResult = null;
		private static DebugSocketException lastRemoteException = null;

		#region -- Main, RunDebugProgram ----------------------------------------------

		public static int Main(string[] args)
		{
			var parser = new Parser(
				s =>
				{
					s.CaseSensitive = false;
					s.IgnoreUnknownArguments = false;
				});

			var r = parser.ParseArguments<SimpleDebugArguments>(args);

			return r.MapResult(
				options =>
				{
					var app = ConsoleApplication.Current;
					try
					{
						return app.Run(
							() => RunDebugProgramAsync(
								new Uri(options.Uri),
								options.UserName == null ? null : UserCredential.Create(options.UserName, options.Password),
								options.Wait,
								false
							)
						);
					}
					catch (TaskCanceledException) { }
					catch (Exception e)
					{
						app.WriteError(e, "Input loop failed. Application is aborted.");
#if DEBUG
						Console.ReadLine();
#endif
					}
					return -1;
				},
				errors =>
				{
					var ht = HelpText.AutoBuild(r);
					Console.WriteLine(ht.ToString());

					return 1;
				}
			);
		} // proc Main

		/// <summary>Gets called from InvokeDebugger</summary>
		/// <param name="uri"></param>
		/// <param name="credentials"></param>
		/// <param name="inProcess"></param>
		public static void RunDebugProgram(Uri uri, ICredentials credentials, bool inProcess)
			=> ConsoleApplication.Current.Run(() => RunDebugProgramAsync(uri, credentials, 0, inProcess));

		/// <summary>Gets called for the server.</summary>
		public static void WriteMessage(ConsoleColor foreground, string text)
		{
			app.Invoke(
				() =>
				{
					using (app.Color(foreground))
						app.WriteLine(text);
				}
			);
		} // proc WriteMessage

		#endregion

		#region -- RunDebugProgramAsync -----------------------------------------------

		private static async Task<int> RunDebugProgramAsync(Uri uri, ICredentials credentials, int wait, bool inProcess)
		{
			// write preamble
			if (!inProcess)
			{
				app.WriteLine(HeadingInfo.Default.ToString());
				app.WriteLine(CopyrightInfo.Default.ToString());
			}

			// wait for the program start
			if (wait > 0)
				await Task.Delay(wait);

			//app.ReservedBottomRowCount = 1; // reserve for log?

			connectionStateOverlay = new ConnectionStateOverlay(app);
			if (uri != null)
				BeginConnection(uri, credentials);

			try
			{
				while (true)
				{
					var line = await app.ReadLineAsync(new SimpleDebugConsoleReadLineManager()) ?? String.Empty;
					if (line.Length == 0)
						continue; // skip empty command
					else if (line[0] == ':') // interactive command
					{
						var m = commandSyntax.Match(line);
						if (m.Success)
						{
							// get the command
							var cmd = m.Groups["cmd"].Value;

							if (String.Compare(cmd, "q", StringComparison.OrdinalIgnoreCase) == 0 ||
								String.Compare(cmd, "quit", StringComparison.OrdinalIgnoreCase) == 0)
								return 0; // exit
							else
							{
								var args = m.Groups["args"];
								var argArray = new string[args.Captures.Count];
								for (var i = 0; i < args.Captures.Count; i++)
									argArray[i] = CleanArgument(args.Captures[i].Value ?? String.Empty);

								try
								{
									await RunCommandAsync(cmd, argArray);
								}
								catch (Exception e)
								{
									app.WriteError(e);
								}
							}
						}
						else
							app.WriteError("Command parse error."); // todo: error
					}
					else // server command
					{
						try
						{
							await SendCommandAsync(line);
						}
						catch (Exception e)
						{
							SetLastRemoteException(e);
							app.WriteError(e);
						}
					}
				}
			}
			finally
			{
				connectionStateOverlay.Application = null;
			}
		} // func RunDebugProgramAsync

		#endregion

		#region -- Http Connection ----------------------------------------------------

		private static readonly object lockHttpConnection = new object();
		private static Task httpConnectionTask = null;
		private static CancellationTokenSource httpConnectionCancellation = null;

		private static DEHttpClient http = null;
		private static DebugSocket debugSocket = null;
		private static DEHttpEventSocket eventSocket = null;
		private static ConnectionStateOverlay connectionStateOverlay = null;

		private static void BeginConnection(Uri uri, ICredentials credentials)
		{
			httpConnectionCancellation = new CancellationTokenSource();
			httpConnectionTask = HttpConnectionAsync(uri, credentials, httpConnectionCancellation.Token);
		} // proc BeginConnection

		private static void EndConnection()
		{
			throw new NotImplementedException();
		} // proc EndConnection

		private static async Task HttpConnectionAsync(Uri uri, ICredentials credentials, CancellationToken cancellationToken)
		{
			var localHttp = DEHttpClient.Create(uri, credentials);
			var tryCurrentCredentials = 0;
			while (!cancellationToken.IsCancellationRequested)
			{
				// show connection state
				if (!IsHttpConnected)
					connectionStateOverlay.SetState(ConnectionState.Connecting, null);

				// try read server info, check for connect
				try
				{
					// try check user agains action
					var xRootNode = await localHttp.GetXmlAsync("?action=list&recursive=false");
					if (!IsHttpConnected)
						SetHttpConnection(localHttp, cancellationToken);
				}
				catch (HttpResponseException e)
				{
					if (e.StatusCode == HttpStatusCode.Unauthorized // login needed
						|| e.StatusCode == HttpStatusCode.Forbidden) // better user needed
					{
						if (IsHttpConnected)
							SetHttpConnection(null, cancellationToken);

						if (tryCurrentCredentials < 3)
						{
							if (tryCurrentCredentials > 0)
								await Task.Delay(3000);

							credentials = CredentialCache.DefaultCredentials;
							tryCurrentCredentials++;
						}
						else
						{
							// request credentials
							app.WriteLine();
							app.WriteLine("Credentials needed (empty for integrated login)");
							var userName = await app.ReadLineAsync(ConsoleReadLineOverlay.CreatePrompt("User: "));
							if (!String.IsNullOrEmpty(userName))
							{
								var password = await app.ReadSecureStringAsync("Password: ");
								credentials = UserCredential.Create(userName, password);
							}
							else
								credentials = CredentialCache.DefaultCredentials;
						}

						// recreate http
						localHttp?.Dispose();
						localHttp = DEHttpClient.Create(uri, credentials);
					}
					else
						app.WriteError(e);
				}
				catch (HttpRequestException e)
				{
					if (IsHttpConnected)
						SetHttpConnection(null, cancellationToken);
					app.WriteError(e);
				}

				// show waiting
				if (!IsHttpConnected)
					connectionStateOverlay.SetState(ConnectionState.None, null);
				await Task.Delay(1000, cancellationToken);
			}
		} // proc HttpConnectionAsync

		private static void SetHttpConnection(DEHttpClient newHttp, CancellationToken cancellationToken)
		{
			lock (lockHttpConnection)
			{
				if(newHttp!= http)
				{
					http?.Dispose();
					debugSocket?.Dispose();
					eventSocket?.Dispose();
				}
				http = newHttp;
				StartSocket(debugSocket = http != null ? new ConsoleDebugSocket(app, http) : null, cancellationToken);
				StartSocket(eventSocket = http != null ? new ConsoleEventSocket(app, http) : null, cancellationToken);

				SetConnectionState(ConnectionState.ConnectedHttp, true);
			}
		} // proc SetHttpConnection

		private static void StartSocket(DEHttpSocketBase socket, CancellationToken cancellationToken)
		{
			if (socket == null)
				return;

			cancellationToken.Register(socket.Dispose);

			socket.RunProtocolAsync()
				.ContinueWith(t => app.WriteError(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
		} // proc StartSocket

		public static bool TryGetHttp(out DEHttpClient http)
		{
			lock (lockHttpConnection)
			{
				http = Program.http;
				return http != null;
			}
		} // func TryGetHttp

		public static DEHttpClient GetHttp()
			=> TryGetHttp(out var http) ? http : throw new ArgumentException("Http connection?");

		public static bool TryGetDebug(out DebugSocket socket)
		{
			lock (lockHttpConnection)
			{
				socket = debugSocket;
				return socket != null && socket.IsConnected;
			}
		} // func TryGetDebug

		public static DebugSocket GetDebug()
			=> TryGetDebug(out var socket) ? socket : throw new ArgumentException("Debug connection?");

		public static bool TryGetEvent(out DEHttpEventSocket socket)
		{
			lock (lockHttpConnection)
			{
				socket = eventSocket;
				return socket != null && socket.IsConnected;
			}
		} // func TryGetEvent

		public static DEHttpEventSocket GetEvent()
			=> TryGetEvent(out var socket) ? socket : throw new ArgumentException("Socket connection?");

		public static void SetConnectionState(ConnectionState state, bool set)
			=> connectionStateOverlay.SetState(state, set);

		public static void PostNewUsePath(string path)
		{
			connectionStateOverlay.SetPath(path);
		} // proc PostNewUsePath

		private static bool IsHttpConnected
		{
			get
			{
				lock (lockHttpConnection)
					return http != null;
			}
		} // func IsHttpConnected

		#endregion

		#region -- RunCommandAsync ----------------------------------------------------

		private static string CleanArgument(string value)
			=> value.Length > 1 && value[0] == '`' && value[value.Length - 1] == '`' ? value.Substring(1, value.Length - 2) : value;

		public static async Task RunCommandAsync(string cmd, string[] argArray)
		{
			var ti = typeof(Program).GetTypeInfo();

			var mi = (from c in ti.GetRuntimeMethods()
					  let attr = c.GetCustomAttribute<InteractiveCommandAttribute>()
					  where c.IsStatic && attr != null && (String.Compare(attr.Name, cmd, StringComparison.OrdinalIgnoreCase) == 0 || String.Compare(attr.Short, cmd, StringComparison.OrdinalIgnoreCase) == 0)
					  select c).FirstOrDefault();

			if (mi == null)
				throw new Exception($"Command '{cmd}' not found.");
			
			var parameterInfo = mi.GetParameters();
			var parameters = new object[parameterInfo.Length];

			if (parameterInfo.Length > 0) // bind arguments
			{
				for (var i = 0; i < parameterInfo.Length; i++)
				{
					if (i < argArray.Length) // convert argument
						parameters[i] = Procs.ChangeType(argArray[i], parameterInfo[i].ParameterType);
					else
						parameters[i] = parameterInfo[i].DefaultValue;
				}
			}

			// execute command
			object r;
			if (mi.ReturnType == typeof(Task)) // async
			{
				// invoke task
				var t = (Task)mi.Invoke(null, parameters);
				await t;

				// get result
				var resultProperty = t.GetType().GetProperty("Result");
				if (t.GetType().IsGenericTypeDefinition && t.GetType().GetGenericTypeDefinition() == typeof(Task<>))
					r = resultProperty.GetValue(t, null);
				else
					r = null;
			}
			else
				r = mi.Invoke(null, parameters);

			if (r != null)
				app.WriteObject(r);
		} // proc RunCommand

		#endregion

		#region -- ShowHelp -----------------------------------------------------------

		[InteractiveCommand("help", Short = "h", HelpText = "Shows this text.")]
		private static void ShowHelp()
		{
			var ti = typeof(Program).GetTypeInfo();
			var assembly = typeof(Program).Assembly;

			app.WriteLine($"Data Exchange Debugger {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
			app.WriteLine(assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
			app.WriteLine();
			assembly = typeof(Lua).Assembly;
			var informationalVersionLua = assembly.GetName().Version.ToString();
			var fileVersionLua = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";
			app.WriteLine($"NeoLua {informationalVersionLua} ({fileVersionLua})");
			app.WriteLine(assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
			app.WriteLine();

			foreach (var cur in
				from c in ti.GetRuntimeMethods()
				let attr = c.GetCustomAttribute<InteractiveCommandAttribute>()
				where c.IsStatic && attr != null
				orderby attr.Name
				select new Tuple<MethodInfo, InteractiveCommandAttribute>(c, attr))
			{
				app.Write("  ");
				app.Write(cur.Item2.Name);
				if (!String.IsNullOrEmpty(cur.Item2.Short))
				{
					app.Write(" [");
					app.Write(cur.Item2.Short);
					app.Write("]");
				}
				app.WriteLine();
				app.Write("    ");
				using (app.Color(ConsoleColor.DarkGray))
					app.WriteLine(cur.Item2.HelpText);

				foreach (var pi in cur.Item1.GetParameters())
				{
					app.WriteLine(
						new ConsoleColor[]
						{
							ConsoleColor.Gray,
							ConsoleColor.Gray,
							ConsoleColor.DarkGray,
							ConsoleColor.Gray
						},
						new string[]
						{
							"      ",
							pi.Name,
							$" ({LuaType.GetType(pi.ParameterType).AliasName ?? pi.ParameterType.Name}): ",
							pi.GetCustomAttribute<DescriptionAttribute>()?.Description
						}
					);
				}
			}

			app.WriteLine();
		} // proc ShowHelp

		#endregion

		#region -- Quit ---------------------------------------------------------------
		
		[InteractiveCommand("quit", Short = "q", HelpText = "Exit the application.")]
		private static void DummyQuit() { }

		#endregion

		#region -- List ---------------------------------------------------------------
		
		private static void PrintList(string indent, XElement x, int rlevel)
		{
			if (rlevel > 0)
			{
				foreach (var c in x.Elements("item"))
				{
					var name = c.GetAttribute("name", String.Empty);
					app.WriteLine(
						new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.DarkGray, ConsoleColor.DarkGray },
						new string[] { indent, name, " : ", c.GetAttribute("displayname", String.Empty) }
					);
					PrintList(indent +  "    ", c, rlevel - 1);
				}
			}
		} // proc PrintList

		[InteractiveCommand("list", HelpText = "Lists the current nodes.")]
		private static async Task SendListAsync(
			[Description("true to retrieve all sub nodes")]
			bool recursive = false
		)
		{
			var rlevel = recursive ? 1000 : 1;
			var x = await GetHttp().GetXmlAsync(HttpStuff.MakeRelativeUri(
				new PropertyValue("action", "list"),
				new PropertyValue("published", "false"),
				new PropertyValue("rlevel", rlevel))
			);
			PrintList(String.Empty, x, rlevel);
		} // func SendListAsync

		#endregion

		#region -- WriteReturn --------------------------------------------------------

		#region -- class TableColumn --------------------------------------------------

		private sealed class TableColumn
		{
			private const string nullValue = "-NULL-";
			private const string errValue = "-ERR-";

			private readonly string name;
			private readonly string type;
			private int width;
			private readonly Func<DebugMemberValue, string> formatValue;

			public TableColumn(DebugMemberValue mv)
			{
				name = mv.Name;
				type = mv.TypeName;

				var tc = mv.Type != null ? Type.GetTypeCode(mv.Type) : TypeCode.Object;
				switch (tc)
				{
					case TypeCode.SByte:
						width = 10;
						formatValue = Int8Value;
						break;
					case TypeCode.Byte:
						width = 10;
						formatValue = UInt8Value;
						break;
					case TypeCode.Int16:
						width = 10;
						formatValue = Int16Value;
						break;
					case TypeCode.UInt16:
						width = 10;
						formatValue = UInt16Value;
						break;
					case TypeCode.Int32:
						width = 10;
						formatValue = Int32Value;
						break;
					case TypeCode.UInt32:
						width = 10;
						formatValue = UInt32Value;
						break;
					case TypeCode.Int64:
						width = 10;
						formatValue = Int64Value;
						break;
					case TypeCode.UInt64:
						width = 10;
						formatValue = UInt64Value;
						break;

					case TypeCode.Boolean:
						width = 5;
						formatValue = BooleanValue;
						break;

					default:
						width = -1;
						formatValue = ToStringValue;
						break;
				}
			} // ctor

			public string FormatValue(DebugMemberValue mv)
				=> formatValue(mv);

			private string ToStringValue(DebugMemberValue mv)
				=> mv.ValueAsString;

			private string FormatInteger(long? n)
			{
				var s = n.HasValue ? n.ToString() : nullValue;
				return s.Length > width
					? errValue
					: s.PadLeft(width);
			} // func FormatInteger

			private string Int8Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((sbyte)mv.Value));

			private string UInt8Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((byte)mv.Value));

			private string Int16Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((short)mv.Value));

			private string UInt16Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((ushort)mv.Value));

			private string Int32Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((int)mv.Value));

			private string UInt32Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((uint)mv.Value));

			private string Int64Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((long)mv.Value));

			private string UInt64Value(DebugMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?(unchecked((long)(ulong)mv.Value)));

			private string BooleanValue(DebugMemberValue mv)
				=> mv.Value == null ? nullValue : ((bool)mv.Value ? "true" : "false");

			public bool IsVariable => width < 0;

			public string Name => name;
			public string TypeName => type;

			public int Width { get => width; set => width = value; }
		} // class TableColumn

		#endregion

		private static void WriteTableMeasureColumns(DebugMemberValue[] sampleRow, out TableColumn[] columns)
		{
			var maxWidth = Console.WindowWidth - 1;
			const int minColWith = 10;
			var variableColumns = 0;
			var fixedWidth = 0;
			var columnsList = new List<TableColumn>(sampleRow.Length);

			for (var i = 0; i < sampleRow.Length; i++)
			{
				columnsList.Add(new TableColumn(sampleRow[i]));
				if (columnsList[i].IsVariable)
					variableColumns++;
				else
					fixedWidth += columnsList[i].Width + 1;
			}

			// calc variable column with
			if (variableColumns > 0)
			{
				var variableWidth = maxWidth - fixedWidth;
				if (variableWidth > 0)
				{
					var varColumnWidth = (variableWidth / variableColumns) - 1;
					if (varColumnWidth < minColWith)
						varColumnWidth = minColWith;
					foreach (var c in columnsList)
					{
						if (c.IsVariable)
							c.Width = varColumnWidth;
					}
				}
				else
				{
					foreach (var c in columnsList)
					{
						if (c.IsVariable)
							c.Width = 10;
					}
				}
			}

			// clear invisible columns
			var currentWidth = 0;
			for (var i = 0; i < columnsList.Count; i++)
			{
				var col = columnsList[i];
				if (currentWidth < maxWidth)
				{
					var newCurrentWidth = currentWidth + col.Width + 1;
					if (newCurrentWidth > maxWidth)
						col.Width = maxWidth - currentWidth;

					currentWidth = newCurrentWidth;
				}
				else
				{
					columnsList.RemoveRange(i, columnsList.Count - i);
					break;
				}
			}
			columns = columnsList.ToArray();
		} // proc WriteTableMeasureColumns

		private static string TableStringPad(string value, int maxWidth)
		{
			if (String.IsNullOrEmpty(value))
				return new string(' ', maxWidth);
			else if (value.Length > maxWidth)
			{
				return maxWidth < 10
					? value.Substring(0, maxWidth)
					: value.Substring(0, maxWidth - 3) + "...";
			}
			else
				return value.PadRight(maxWidth);
		} // func TableStringPad

		private static void WriteRow<T>(TableColumn[] columns, T[] values, Func<TableColumn, T, string> getValue)
		{
			for (var i = 0; i < columns.Length; i++)
			{
				if (i > 0)
					app.Write(" ");
				var col = columns[i];
				app.Write(TableStringPad(getValue(col, values[i]), col.Width));
			}
			app.WriteLine();
		} // proc WriteRow

		private static void WriteTable(IEnumerable<DebugMemberValue[]> list)
		{

			var columns = (TableColumn[])null;
			foreach (var r in list)
			{
				if (columns == null)
				{
					WriteTableMeasureColumns(r, out columns);

					// header
					WriteRow(columns, columns, (_, c) => c.Name);
					// type
					using (app.Color(ConsoleColor.DarkGray))
						WriteRow(columns, columns, (_, c) => c.TypeName);
					// sep
					WriteRow(columns, columns, (_, c) => new string('-', c.Width));
				}

				WriteRow(columns, r, (_, c) => _.FormatValue(c));
			}
		} // proc WriteTable

		private static void WriteReturn(string indent, IEnumerable<DebugMemberValue> r)
		{
			foreach (var v in r)
			{
				app.Write(indent);
				app.Write(v.Name);
				if (v.IsValueList && v.Value is IEnumerable<DebugMemberValue[]> list)
				{
					app.WriteLine();
					app.WriteLine();
					WriteTable(list);
					app.WriteLine();
				}
				else if (v.IsValueArray && v.Value is IEnumerable<DebugMemberValue> array)
				{
					app.WriteLine();
					WriteReturn(indent + "    ", array);
				}
				else
				{
					app.Write(": ");
					using (app.Color(ConsoleColor.DarkGray))
					{
						app.Write("(");
						app.Write(v.TypeName);
						app.Write(")");
					}
					app.WriteLine(v.ValueAsString);
				}
			}
		} // proc WriteReturn

		#endregion

		#region -- SendCommand --------------------------------------------------------

		private static async Task SendCommandAsync(string commandText)
		{
			var r = await GetDebug().ExecuteAsync(commandText);

			WriteReturn(String.Empty, r);

			var parts = new string[7];
			var colors = new ConsoleColor[]
			{
				ConsoleColor.DarkGreen,

				ConsoleColor.DarkGreen,
				ConsoleColor.Green,
				ConsoleColor.Green,

				ConsoleColor.DarkGreen,
				ConsoleColor.Green,
				ConsoleColor.Green
			};
			parts[0] = "==> ";

			if (r.CompileTime > 0)
			{
				parts[1] = "compile: ";
				parts[2] = r.CompileTime.ToString("N0");
				parts[3] = " ms ";
			}

			parts[4] = "run: ";
			parts[5] = r.RunTime.ToString("N0");
			parts[6] = " ms";

			app.WriteLine(colors, parts, true);
		} // proc SendCommandAsync

		#endregion

		#region -- SendRecompile ------------------------------------------------------

		[InteractiveCommand("recompile", HelpText = "Force a recompile and rerun of all script files (if the are outdated).")]
		private static async Task SendRecompileAsync()
		{
			var r = await GetDebug().RecompileAsync();

			var scripts = 0;
			var failed = 0;
			var first = true;
			foreach (var c in r)
			{
				if (first)
					first = false;
				else
					app.Write(", ");
				using (app.Color(c.failed ? ConsoleColor.Red : ConsoleColor.Gray))
					app.Write(c.scriptId);

				if (c.failed)
					failed++;
				scripts++;
			}
			if (!first)
				app.WriteLine();

			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.Green,
					ConsoleColor.Red
				},
				new string[]
				{
					"==> recompile: ",
					$"{scripts:N0} scripts",
					failed > 0 ? $", {failed:N0} failed" : null
				},
				true
			);
		}  // proc SendRecompileAsync

		#endregion

		#region -- SendRunScript ------------------------------------------------------

		[InteractiveCommand("run", HelpText = "Executes a test script and stores the result.")]
		private static async Task SendRunScript(
			[Description("optional filter expression to select one or more scripts")]
			string scriptFilter = null,
			[Description("filter expression to select one or more tests")]
			string methodFilter = null
		)
		{
			var testCount = 0;
			var failedTests = 0;
			var failedScripts = 0;

			if (methodFilter == null)
			{
				methodFilter = scriptFilter;
				scriptFilter = null;
			}

			lastScriptResult = await GetDebug().RunScriptAsync(scriptFilter, methodFilter);

			foreach (var s in lastScriptResult.Scripts)
			{
				if (!s.Success)
					failedScripts++;

				foreach (var t in s.Tests)
				{
					testCount++;
					if (!t.Success)
						failedTests++;
				}
			}

			if (failedScripts > 0)
			{
				app.WriteLine(
					new ConsoleColor[]
					{
						ConsoleColor.Red,
						ConsoleColor.DarkRed,
					},
					new string[]
					{
						$"{failedScripts:N0}",
						" scripts failed!"
					}
				);
			}

			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
					ConsoleColor.Gray,

					ConsoleColor.DarkRed,
					ConsoleColor.Red,
					ConsoleColor.DarkRed,
				},
				new string[]
				{
					"==> run ",
					$"{testCount:N0}",
					" tests",

					failedTests > 0 ? " (" : null,
					failedTests > 0 ? $"{failedTests:N0}" : null,
					failedTests > 0 ? " failed)" : null
				},
				true
			);
		} // func SendRunScript

		#endregion

		#region -- ViewRunScriptResult ------------------------------------------------

		private static void NoResult()
			=> app.WriteLine(new ConsoleColor[] { ConsoleColor.DarkGray }, new string[] { "==> no result" });

		[InteractiveCommand("scripts", HelpText = "Show the prev. executed test results.")]
		private static void ViewScriptResult(
			[Description("filter expression to select one or more scripts")]
			string filter = null
		)
		{
			if (lastScriptResult == null)
			{
				NoResult();
				return;
			}

			var filterFunc = Procs.GetFilerFunction(filter, true);
			var selectedScripts = lastScriptResult.Scripts.Where(s => filterFunc(s.ScriptId));
			var firstScripts = selectedScripts.Take(2).ToArray();
			if (firstScripts.Length == 0) // no scripts
				NoResult();
			else if (firstScripts.Length > 1) // script table
				WriteTable(selectedScripts.Select(s => s.Format()));
			else // detail
			{
				var firstScript = firstScripts[0];
				var parts = new string[7];

				parts[0] = firstScript.ScriptId;

				if (firstScript.Success)
				{
					if (firstScript.CompileTime > 0)
					{
						parts[1] = " (compile: ";
						parts[2] = $"{firstScript.CompileTime:N0} ms";
						parts[3] = ", run: ";
					}
					else
						parts[3] = " (run: ";
					parts[4] = $"{firstScript.RunTime:N0} ms";
					parts[5] = ")";
				}
				else
					parts[6] = " failed.";

				app.WriteLine(
					new ConsoleColor[]
					{
						ConsoleColor.White,

						ConsoleColor.DarkGreen,
						ConsoleColor.Green,
						ConsoleColor.DarkGreen,
						ConsoleColor.Green,
						ConsoleColor.DarkGreen,

						ConsoleColor.Red
					},
					parts
				);

				if (firstScript.Exception != null)
				{
					app.WriteLine();
					WriteLastExceptionCore(firstScript.Exception);
				}
				else
				{
					app.WriteLine();
					WriteTable(firstScript.Tests.Select(t => t.Format()));

					var totalDuration = firstScript.Tests.Sum(t => t.Duration);

					app.WriteLine();
					app.WriteLine(
						new ConsoleColor[]
						{
							ConsoleColor.Gray,
							ConsoleColor.White,
							ConsoleColor.DarkGreen,
							ConsoleColor.Green,
							ConsoleColor.DarkRed,
							ConsoleColor.Red,
						},
						new string[]
						{
							"==> total:",
							$"{totalDuration:N0} ms",
							", passed: ",
							$"{firstScript.Passed:N0}",
							", failed: ",
							$"{firstScript.Failed:N0}"
						},
						true
					);

				}
			}
		} // proc ViewScriptResult

		[InteractiveCommand("tests", HelpText = "Show the prev. executed test results.")]
		private static void ViewTestResult(
			[Description("optional filter expression to select one or more scripts")]
			string scriptFilter = null,
			[Description("filter expression to select one or more tests")]
			string methodFilter = null
		)
		{
			if (lastScriptResult == null)
			{
				NoResult();
				return;
			}

			Func<DebugRunScriptResult.Test, bool> filterFunc;
			if (methodFilter != null) // filter script and tests
			{
				var filterScriptFunc = Procs.GetFilerFunction(scriptFilter, true);
				var filterTestFunc = Procs.GetFilerFunction(methodFilter, true);

				filterFunc = t => filterScriptFunc(t.Script?.ScriptId ?? String.Empty) && filterTestFunc(t.Name);
			}
			else // filter tests only
			{
				methodFilter = scriptFilter;
				scriptFilter = null;

				var filterTestFunc = Procs.GetFilerFunction(methodFilter, true);
				filterFunc = t => filterTestFunc(t.Name);
			}

			var selectedTests = lastScriptResult.AllTests.Where(filterFunc);
			var firstTests = selectedTests.Take(2).ToArray();
			if (firstTests.Length == 0)
				NoResult();
			else if (firstTests.Length > 1) // print table
				WriteTable(selectedTests.Select(t => t.Format()));
			else // print detail
			{
				var firstTest = firstTests[0];
				var parts = new string[7];

				parts[0] = firstTest.Script?.ScriptId;
				parts[1] = ": ";
				parts[2] = firstTest.Name;
				if (firstTest.Success)
				{
					parts[3] = " (time: ";
					parts[4] = $"{firstTest.Duration} ms";
					parts[5] = ")";
				}
				else
					parts[6] = " failed.";


				app.WriteLine(
					new ConsoleColor[]
					{
						ConsoleColor.White,
						ConsoleColor.Gray,
						ConsoleColor.White,
						ConsoleColor.Gray,
						ConsoleColor.White,
						ConsoleColor.Gray,
						ConsoleColor.Red
					},
					parts
				);
				if (firstTest.Exception != null)
				{
					app.WriteLine();
					WriteLastExceptionCore(firstTest.Exception);
				}
			}
		} // func ViewTestResult

		#endregion

		#region -- SendUseNode --------------------------------------------------------

		[InteractiveCommand("use", HelpText = "Activates a new global space, on which the commands are executed.")]
		private static async Task SendUseNodeAsync(
			[Description("absolute or relative path")]
			string node = null
		)
		{
			var p = await GetDebug().UseAsync(node ?? String.Empty);
			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[] { "==> Current Node: ", p },
				true
			);
		} // func SendUseNodeAsync

		#endregion

		#region -- SendVariables ------------------------------------------------------

		[InteractiveCommand("members", Short = "m", HelpText = "Lists the current available global variables.")]
		private static async Task VariablesAsync()
		{
			WriteReturn(String.Empty, await GetDebug().MembersAsync(String.Empty));
		} // proc VariablesAsync

		#endregion

		#region -- BeginScope, CommitScope, RollbackScope -----------------------------

		[InteractiveCommand("begin", HelpText = "Starts a new transaction scope.")]
		private static async Task BeginScopeAsync()
		{
			var userName = await GetDebug().BeginScopeAsync();
			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[]
				{
					"==> new scope for ",
					userName
				},
				true
			);
		} // proc BeginScopeAsync

		[InteractiveCommand("commit", HelpText = "Commits the current scope and creates a new one.")]
		private static async Task CommitScopeAsync()
		{
			var userName = await GetDebug().CommitScopeAsync();
			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[]
				{
					"==> commit scope of ",
					userName
				},
				true
			);
		} // proc CommitScopeAsync

		[InteractiveCommand("rollback", HelpText = "Rollbacks the current scope and creates a new one.")]
		private static async Task RollbackScopeAsync()
		{
			var userName = await GetDebug().RollbackScopeAsync();
			app.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[]
				{
					"==> rollback scope of ",
					userName
				},
				true
			);
		} // proc RollbackScopeAsync

		#endregion

		#region -- LastException ------------------------------------------------------

		private static void SetLastRemoteException(Exception e)
		{
			lastRemoteException = null;
			while (e != null)
			{
				if (e is DebugSocketException re)
				{
					lastRemoteException = re;
					return;
				}
				e = e.InnerException;
			}
		} // proc SetLastRemoteException

		private static void WriteLastExceptionCore(Exception ex)
		{
			if (ex != null)
			{
				app.WriteError(ex);
				app.WriteLine();
				app.WriteError(ex.StackTrace);

				if (ex is DebugSocketException cde)
				{
					foreach (var innerException in cde.InnerExceptions)
					{
						app.WriteLine();
						app.WriteError("== Inner Exception ==");
						WriteLastExceptionCore(innerException);
					}
				}
			}
		} // WriteLastExceptionCore

		[InteractiveCommand("lastex", HelpText = "Detail for the last remote exception.")]
		private static void WriteLastException()
			=> WriteLastExceptionCore(lastRemoteException);

		#endregion
	} // class Program
}