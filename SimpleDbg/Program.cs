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
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommandLine;
using CommandLine.Text;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class SimpleDebugArguments -----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class SimpleDebugArguments
	{
		[Option('o', HelpText = "Uri to the server.", Required = true)]
		public string Uri { get; set; }

		[Option("wait", HelpText = "Wait time before, the connection will be established (in ms).")]
		public int Wait { get; set; } = 0;
	} // class SimpleDebugArguments

	#endregion

	#region -- class InteractiveCommandAttribute ----------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class InteractiveCommandAttribute : Attribute
	{
		public InteractiveCommandAttribute(string name)
		{
			this.Name = name;
		} // ctor

		public string Name { get; }
		public string Short { get; set; }
		public string HelpText { get; set; }
	} // class InteractiveCommandAttribute

	#endregion

	#region -- class ConsoleDebugSession ------------------------------------------------

	internal sealed class ConsoleDebugSession : ClientDebugSession
	{
		private readonly DebugView view;
		private readonly Stopwatch startUp;

		public ConsoleDebugSession(DebugView view, Uri serverUri, bool inProcess)
			: base(serverUri)
		{
			this.view = view;
			this.startUp = Stopwatch.StartNew();

			this.DefaultTimeout = inProcess ? 0 : 10000;
		} // ctor

		private ICredentials currentCredentials = CredentialCache.DefaultCredentials;

		protected override ICredentials GetCredentials()
			=> currentCredentials;

		protected override void OnCurrentUsePathChanged()
			=> view.UsePath = CurrentUsePath;

		protected override void OnConnectionEstablished()
		{
			view.IsConnected = true;
		} // proc 

		protected override void OnConnectionLost()
			=> view.IsConnected = false;

		protected override async Task<bool> OnConnectionFailureAsync(Exception e)
		{
			var innerException = e.InnerException as WebException;
			var authentificationInfo = ClientAuthentificationInformation.Ntlm;
			if (innerException != null && ClientAuthentificationInformation.TryGet(innerException, ref authentificationInfo, false)) // is this a authentification exception
			{
				if (startUp.ElapsedMilliseconds < 10000) // try it for at least 10sec
				{
					await Task.Delay(1000);
					return true;
				}
				else
					currentCredentials = await Program.GetCredentialsFromUserAsync(authentificationInfo.Realm);
				return true;
			}
			else
			{
				view.WriteError(e, "Connection failed.");
				return false;
			}
		} // proc OnConnectionFailure

		protected override void OnMessage(char type, string message)
		{
			IDisposable SetColorByType()
			{
				switch (type)
				{
					case 'E':
						return view.SetColor(ConsoleColor.DarkRed);
					case 'W':
						return view.SetColor(ConsoleColor.DarkYellow);
					case 'I':
						return view.SetColor(ConsoleColor.White);
					default:
						return view.SetColor(ConsoleColor.Gray);
				}
			} // func SetColorByType

			using (view.LockScreen())
			using (SetColorByType())
			{
				view.WriteLine(message);
			}
		} // proc OnMessage

		protected override void OnStartScript(ClientRunScriptResult.Script script, string message)
		{
			var parts = new string[10];

			parts[0] = ">> ";
			parts[1] = script.ScriptId;

			if (script.Success)
			{
				if (script.CompileTime > 0)
				{
					parts[2] = " (compile: ";
					parts[3] = $"{script.CompileTime:N0} ms";
					parts[4] = ", run: ";
				}
				else
					parts[4] = " (run: ";
				parts[5] = $"{script.RunTime:N0} ms";
				parts[6] = ")";
			}
			else
			{
				parts[7] = " (Error: ";
				parts[8] = message;
				parts[9] = ")";
			}

			view.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.White,
					ConsoleColor.White,

					ConsoleColor.DarkGreen,
					ConsoleColor.Green,
					ConsoleColor.DarkGreen,
					ConsoleColor.Green,
					ConsoleColor.DarkGreen,

					ConsoleColor.DarkRed,
					ConsoleColor.Red,
					ConsoleColor.DarkRed,
				},
				parts
			);
		} // proc OnStartScript

		protected override void OnTestResult(ClientRunScriptResult.Test test, string message)
		{
			var success = test.Success;
			view.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.White,
					ConsoleColor.White,

					ConsoleColor.DarkGreen,
					ConsoleColor.Green,
					ConsoleColor.DarkGreen,


					ConsoleColor.DarkRed,
					ConsoleColor.Red,
					ConsoleColor.DarkRed,
				},
				new string[]
				{
					">>>> ",
					test.Name,

					success ? " (run: " : null,
					success ? $"{test.Duration:N0} ms" : null,
					success ? ")" : null,

					success ? null : " (fail: ",
					success ? null : $"{message}",
					success ? null : ")",
				}
			);
		} // proc OnTestResult

		protected override void OnCommunicationException(Exception e)
			=> view.WriteError(e, "Communication exception.");
	} // class ConsoleDebugSession

	#endregion

	/// <summary></summary>
	public static class Program
	{
		private static readonly DebugView view = new DebugView();
		private static readonly Regex commandSyntax = new Regex(@"\:(?<cmd>\w+)(?:\s+(?<args>(?:\`[^\`]*\`)|(?:[^\s]*)))*", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static StringBuilder currentCommand = new StringBuilder(); // holds the current script
		private static ConsoleDebugSession session;

		private static TaskCompletionSource<ICredentials> credentialGet = null;
		private static ClientDebugException lastRemoteException = null;
		private static ClientRunScriptResult lastScriptResult = null;

		#region -- Main, RunDebugProgram ------------------------------------------------

		public static int Main(string[] args)
		{
			var parser = new Parser(
				s =>
				{
					s.CaseSensitive = false;
					s.IgnoreUnknownArguments = false;
				});

			var r = parser.ParseArguments<SimpleDebugArguments>(args);

			return r.MapResult<SimpleDebugArguments, int>(
				options =>
				{
					try
					{
						Task.Factory.StartNew(RunDebugProgram(options).Wait).Wait();
					}
					catch (TaskCanceledException) { }
					catch (Exception e)
					{
						view.WriteError(e, "Input loop failed. Application is aborted.");
#if DEBUG
						Console.ReadLine();
#endif
					}
					return 0;
				},
				errors =>
				{
					var ht = HelpText.AutoBuild<SimpleDebugArguments>(r);
					Console.WriteLine(ht.ToString());

					return 1;
				});
		} // func Main

		private static async Task RunDebugProgram(SimpleDebugArguments arguments)
		{
			// simple wait
			if (arguments.Wait > 0)
				await Task.Delay(arguments.Wait);

			await RunDebugProgramAsync(new Uri(arguments.Uri), false);
		} // proc RunDebugProgram

		public static async Task RunDebugProgramAsync(Uri uri, bool inProcess)
		{
			// connection
			view.IsConnected = false;
			session = new ConsoleDebugSession(view, uri, inProcess);

			// should be post in the thread context
			Task.Run(() => session.RunProtocolAsync()).ContinueWith(
				t =>
				{
					try
					{
						t.Wait();
					}
					catch (TaskCanceledException) { }
					catch (Exception e)
					{
						var ex = e.GetInnerException();
						if (ex is TaskCanceledException)
							return;

						view.WriteError(ex.ToString());
					}
				}, TaskContinuationOptions.ExecuteSynchronously
			).GetAwaiter();

			// start request loop
			while (true)
			{
				// read command
				view.Write("> ");
				var line = Console.ReadLine();
				if (credentialGet != null)
				{
					var t = credentialGet;
					credentialGet = null;
					t.SetResult(GetCredentialsFromUser((string)t.Task.AsyncState));
				}
				else if (line.Length == 0) // nothing
				{
					if (currentCommand.Length == 0) // no current script
						currentCommand.AppendLine();
					else
					{
						try
						{
							await SendCommand(currentCommand.ToString());
						}
						catch (Exception e)
						{
							SetLastRemoteException(e);
							view.WriteError(e);
						}
						currentCommand.Clear();
					}
				}
				else if (line[0] == ':') // interactive command
				{
					var m = commandSyntax.Match(line);
					if (m.Success)
					{
						// get the command
						var cmd = m.Groups["cmd"].Value;

						if (String.Compare(cmd, "q", StringComparison.OrdinalIgnoreCase) == 0 ||
							String.Compare(cmd, "quit", StringComparison.OrdinalIgnoreCase) == 0)
							break; // exit
						else
						{
							var args = m.Groups["args"];
							var argArray = new string[args.Captures.Count];
							for (var i = 0; i < args.Captures.Count; i++)
								argArray[i] = CleanArgument(args.Captures[i].Value ?? String.Empty);

							try
							{
								RunCommand(cmd, argArray);
							}
							catch (Exception e)
							{
								view.WriteError(e);
							}
						}
					}
					else
						view.WriteError("Command parse error."); // todo: error
				}
				else // add to command buffer
				{
					currentCommand.AppendLine(line);
				}
			}

			// dispose debug session
			session.Dispose();
		} // proc RunDebugProgramAsync

		/// <summary>Gets called for the server.</summary>
		public static void WriteMessage(ConsoleColor foreground, string text)
		{
			view.Write(text, foreground);
			view.WriteLine();
		} // proc WriteMessage

		public static Task<ICredentials> GetCredentialsFromUserAsync(string realm)
		{
			view.Write("Press Return to login");

			credentialGet = new TaskCompletionSource<ICredentials>(realm);
			return credentialGet.Task;
		} // func GetCredentialsFromUserAsync

		private static ICredentials GetCredentialsFromUser(string realm)
		{
			string userName = null;
			var sec = new SecureString();

			using (view.LockScreen())
			{
				Console.WriteLine("Login: {0}", realm);
				Console.Write("User: ");
				userName = Console.ReadLine();
				if (String.IsNullOrEmpty(userName))
					return CredentialCache.DefaultCredentials;

				Console.Write("Password: ");
				while (true)
				{
					var k = Console.ReadKey(true);

					if (k.Key == ConsoleKey.Enter)
						break;
					else if (k.Key == ConsoleKey.Backspace || k.Key == ConsoleKey.Delete)
						sec.Clear();
					else
					{
						Console.Write("*");
						sec.AppendChar(k.KeyChar);
					}
				}
				Console.WriteLine();
			}

			return UserCredential.Wrap(new NetworkCredential(userName, sec));
		} // func GetCredentialsFromUser

		#endregion

		#region -- RunCommand -----------------------------------------------------------

		private static string CleanArgument(string value)
			=> value.Length > 1 && value[0] == '`' && value[value.Length - 1] == '`' ? value.Substring(1, value.Length - 2) : value;

		public static void RunCommand(string cmd, string[] argArray)
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
				var t = (Task)mi.Invoke(null, parameters);
				t.Wait();
				r = null;
			}
			else
				r = mi.Invoke(null, parameters);

			if (r != null)
				view.WriteObject(r);
		} // proc RunCommand

		#endregion

		#region -- ShowHelp -------------------------------------------------------------

		[InteractiveCommand("help", Short = "h", HelpText = "Shows this text.")]
		private static void ShowHelp()
		{
			var ti = typeof(Program).GetTypeInfo();
			using (view.LockScreen())
			{
				var assembly = typeof(Program).Assembly;
				view.WriteLine($"Data Exchange Debugger {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
				view.WriteLine(assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
				view.WriteLine();
				assembly = typeof(Lua).Assembly;
				var informationalVersionLua = assembly.GetName().Version.ToString();
				var fileVersionLua = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";
				view.WriteLine($"NeoLua {informationalVersionLua} ({fileVersionLua})");
				view.WriteLine(assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
				view.WriteLine();

				foreach (var cur in
					from c in ti.GetRuntimeMethods()
					let attr = c.GetCustomAttribute<InteractiveCommandAttribute>()
					where c.IsStatic && attr != null
					orderby attr.Name
					select new Tuple<MethodInfo, InteractiveCommandAttribute>(c, attr))
				{
					view.Write("  ");
					view.Write(cur.Item2.Name);
					if (!String.IsNullOrEmpty(cur.Item2.Short))
					{
						view.Write(" [");
						view.Write(cur.Item2.Short);
						view.Write("]");
					}
					view.WriteLine();
					view.Write("    ");
					using (view.SetColor(ConsoleColor.DarkGray))
						view.WriteLine(cur.Item2.HelpText);

					foreach (var pi in cur.Item1.GetParameters())
					{
						view.WriteLine(
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

				view.WriteLine();
			}
		} // proc ShowHelp

		#endregion

		#region -- Clear, Quit ----------------------------------------------------------

		[InteractiveCommand("clear", HelpText = "Clears the current command buffer.")]
		private static void ClearCommandBuffer()
		{
			currentCommand.Clear();
		} // proc ClearCommandBuffer

		[InteractiveCommand("quit", Short = "q", HelpText = "Exit the application.")]
		private static void DummyQuit() { }

		#endregion

		#region -- WriteReturn ----------------------------------------------------------

		#region -- class TableColumn ----------------------------------------------------

		private sealed class TableColumn
		{
			private const string nullValue = "-NULL-";
			private const string errValue = "-ERR-";

			private readonly string name;
			private readonly string type;
			private int width;
			private readonly Func<ClientMemberValue, string> formatValue;

			public TableColumn(ClientMemberValue mv)
			{
				this.name = mv.Name;
				this.type = mv.TypeName;

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

			public string FormatValue(ClientMemberValue mv)
				=> formatValue(mv);

			private string ToStringValue(ClientMemberValue mv)
				=> mv.ValueAsString;

			private string FormatInteger(long? n)
			{
				var s = n.HasValue ? n.ToString() : nullValue;
				return s.Length > width
					? errValue
					: s.PadLeft(width);
			} // func FormatInteger

			private string Int8Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((sbyte)mv.Value));

			private string UInt8Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((byte)mv.Value));

			private string Int16Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((short)mv.Value));

			private string UInt16Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((ushort)mv.Value));

			private string Int32Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((int)mv.Value));

			private string UInt32Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((uint)mv.Value));

			private string Int64Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?((long)mv.Value));

			private string UInt64Value(ClientMemberValue mv)
				=> FormatInteger(mv.Value == null ? null : new long?(unchecked((long)(ulong)mv.Value)));

			private string BooleanValue(ClientMemberValue mv)
				=> mv.Value == null ? nullValue : ((bool)mv.Value ? "true" : "false");

			public bool IsVariable => width < 0;

			public string Name => name;
			public string TypeName => type;

			public int Width { get => width; set => width = value; }
		} // class TableColumn

		#endregion

		private static void WriteTableMeasureColumns(ClientMemberValue[] sampleRow, out TableColumn[] columns)
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
				return value.Substring(0, maxWidth - 3) + "...";
			else
				return value.PadRight(maxWidth);
		} // func TableStringPad

		private static void WriteRow<T>(TableColumn[] columns, T[] values, Func<TableColumn, T, string> getValue)
		{
			for (var i = 0; i < columns.Length; i++)
			{
				if (i > 0)
					Console.Write(" ");
				var col = columns[i];
				Console.Write(TableStringPad(getValue(col, values[i]), col.Width));
			}
			Console.WriteLine();
		} // proc WriteRow

		private static void WriteTable(IEnumerable<ClientMemberValue[]> list)
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
					using (view.SetColor(ConsoleColor.DarkGray))
						WriteRow(columns, columns, (_, c) => c.TypeName);
					// sep
					WriteRow(columns, columns, (_, c) => new string('-', c.Width));
				}

				WriteRow(columns, r, (_, c) => _.FormatValue(c));
			}
		} // proc WriteTable

		private static void WriteReturn(string indent, IEnumerable<ClientMemberValue> r)
		{
			foreach (var v in r)
			{
				lock (view.SyncRoot)
				{
					Console.Write(indent);
					Console.Write(v.Name);
					if (v.IsValueList && v.Value is IEnumerable<ClientMemberValue[]> list)
					{
						Console.WriteLine();
						Console.WriteLine();
						WriteTable(list);
						Console.WriteLine();
					}
					else if (v.IsValueArray && v.Value is IEnumerable<ClientMemberValue> array)
					{
						Console.WriteLine();
						WriteReturn(indent + "    ", array);
					}
					else
					{
						Console.Write(": ");
						using (view.SetColor(ConsoleColor.DarkGray))
						{
							Console.Write("(");
							Console.Write(v.TypeName);
							Console.Write(")");
						}
						Console.WriteLine(v.ValueAsString);
					}
				}
			}
		} // proc WriteReturn

		#endregion

		#region -- SendCommand ----------------------------------------------------------

		private static async Task SendCommand(string commandText)
		{
			var r = await session.SendExecuteAsync(commandText);
			using (view.LockScreen())
			{
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

				view.WriteLine(colors, parts, true);
			}
		} // proc SendCommand

		#endregion

		#region -- SendRecompile --------------------------------------------------------

		[InteractiveCommand("recompile", HelpText = "Force a recompile and rerun of all script files (if the are outdated).")]
		private static async Task SendRecompile()
		{
			var r = await session.SendRecompileAsync();
			using (view.LockScreen())
			{
				var scripts = 0;
				var failed = 0;
				var first = true;
				foreach (var c in r)
				{
					if (first)
						first = false;
					else
						view.Write(", ");
					using (view.SetColor(c.failed ? ConsoleColor.Red : ConsoleColor.Gray))
						view.Write(c.scriptId);

					if (c.failed)
						failed++;
					scripts++;
				}
				if (!first)
					view.WriteLine();

				view.WriteLine(
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
			}
		}  // proc SendRecompile

		#endregion

		#region -- SendRunScript --------------------------------------------------------

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

			if(methodFilter == null)
			{
				methodFilter = scriptFilter;
				scriptFilter = null;
			}

			lastScriptResult = await session.SendRunScriptAsync(scriptFilter, methodFilter);

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
				view.WriteLine(
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

			view.WriteLine(
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

		#region -- ViewRunScriptResult --------------------------------------------------

		private static void NoResult()
			=> view.WriteLine(new ConsoleColor[] { ConsoleColor.DarkGray }, new string[] { "==> no result" });

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
				using (view.LockScreen())
				{
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

					view.WriteLine(
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
						view.WriteError();
						WriteLastExceptionCore(firstScript.Exception);
					}
					else
					{
						view.WriteLine();
						WriteTable(firstScript.Tests.Select(t => t.Format()));

						var totalDuration = firstScript.Tests.Sum(t => t.Duration);

						view.WriteLine();
						view.WriteLine(
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

			Func<ClientRunScriptResult.Test, bool> filterFunc;
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

				using (view.LockScreen())
				{
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


					view.WriteLine(
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
						view.WriteError();
						WriteLastExceptionCore(firstTest.Exception);
					}
				}
			}
		} // func ViewTestResult

		#endregion

		#region -- SendUseNode ----------------------------------------------------------

		[InteractiveCommand("use", HelpText = "Activates a new global space, on which the commands are executed.")]
		private static async Task SendUseNode(
			[Description("absolute or relative path")]
			string node = null
		)
		{
			var p = await session.SendUseAsync(node ?? String.Empty);
			view.WriteLine(
				new ConsoleColor[] 
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[] { "==> Current Node: ", p },
				true
			);
		} // func SendUseNode

		#endregion

		#region -- SendVariables --------------------------------------------------------

		[InteractiveCommand("members", Short = "m", HelpText = "Lists the current available global variables.")]
		private static async Task SendVariables()
		{
			WriteReturn(String.Empty, await session.SendMembersAsync(String.Empty));
		} // proc SendVariables

		#endregion

		#region -- SendList -------------------------------------------------------------

		private static void PrintList(string indent, XElement x)
		{
			foreach (var c in x.Elements("n"))
			{
				Console.WriteLine("{0}{1}: {2}", indent, c.GetAttribute("name", String.Empty), c.GetAttribute("displayName", String.Empty));
				PrintList(indent + "    ", c);
			}
		} // proc PrintList

		[InteractiveCommand("list", HelpText = "Lists the current nodes.")]
		private static async Task SendList(
			[Description("true to retrieve all sub nodes")]
			bool recursive = false
		)
		{
			var x = await session.SendListAsync(recursive);
			using (view.LockScreen())
				PrintList(String.Empty, x);
		} // func SendList


		#endregion

		#region -- SetTimeout -----------------------------------------------------------

		[InteractiveCommand("timeout", Short = "t", HelpText = "Sets the timeout in ms.")]
		private static void SetTimeout(
			[Description("Optional new timeout in ms")]
			int timeout = -1
		)
		{
			if (timeout >= 0)
				session.DefaultTimeout = timeout;
			view.WriteLine(
				new ConsoleColor[]
				{
					ConsoleColor.Gray,
					ConsoleColor.White,
				},
				new string[]
				{
					"==> timeout is: ",
					$"{session.DefaultTimeout} ms"
				},
				true
			);
		} // func SendList

		#endregion

		#region -- BeginScope, CommitScope, RollbackScope -------------------------------

		[InteractiveCommand("begin", HelpText = "Starts a new transaction scope.")]
		private static async Task BeginScope()
		{
			var userName = await session.SendBeginScopeAsync();
			view.WriteLine(
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
		} // proc BeginScope

		[InteractiveCommand("commit", HelpText = "Commits the current scope and creates a new one.")]
		private static async Task CommitScope()
		{
			var userName = await session.SendCommitScopeAsync();
			view.WriteLine(
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
		} // proc CommitScope

		[InteractiveCommand("rollback", HelpText = "Rollbacks the current scope and creates a new one.")]
		private static async Task RollbackScope()
		{
			var userName = await session.SendRollbackScopeAsync();
			view.WriteLine(
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
		} // proc RollbackScope

		#endregion

		#region -- LastException --------------------------------------------------------

		private static void SetLastRemoteException(Exception e)
		{
			lastRemoteException = null;
			while (e != null)
			{
				if (e is ClientDebugException re)
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
				view.WriteError(ex);
				view.WriteError();
				view.WriteError(ex.StackTrace);

				if (ex is ClientDebugException cde)
				{
					foreach (var innerException in cde.InnerExceptions)
					{
						view.WriteError();
						view.WriteError("== Inner Exception ==");
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
