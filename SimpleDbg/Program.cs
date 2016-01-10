using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommandLine;
using Neo.IronLua;
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

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class Program
	{
		private static readonly DebugView view = new DebugView();
		private static readonly Regex commandSyntax = new Regex(@"\:(?<cmd>\w+)(?:\s+(?<args>(?:\`[^\`]*\`)|(?:[^\s]*)))*", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static StringBuilder currentCommand = new StringBuilder(); // holds the current script
		private static ClientDebugSession session;

		#region -- Main, RunDebugProgram --------------------------------------------------

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
					catch (Exception e)
					{
						view.WriteError(e);
#if DEBUG
						Console.ReadLine();
#endif
					}
					return 0;
				},
				errors =>
				{
					foreach (var e in errors)
						Console.WriteLine(e.Tag.ToString());
					return 1;
				});
		} // func Main

		private static async Task RunDebugProgram(SimpleDebugArguments arguments)
		{
			// simple wait
			if (arguments.Wait > 0)
				await Task.Delay(arguments.Wait);

			// connection
			view.WriteLine("Connecting...");
			session = new ClientDebugSession(new Uri(arguments.Uri));
			await session.ConnectAsync();

			// start request loop
			while (true)
			{
				// read command
				view.Write("> ");
				var line = Console.ReadLine();

				if (line.Length == 0) // nothing
				{
					if (currentCommand.Length == 0) // no current script
						currentCommand.AppendLine();
					else
					{
						await SendCommand(currentCommand.ToString());
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

				//await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(cmd)), WebSocketMessageType.Text, true, CancellationToken.None);
			}
		} // proc RunDebugProgram

		#endregion

		#region -- RunCommand -------------------------------------------------------------

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

		#region -- ShowHelp ---------------------------------------------------------------

		[InteractiveCommand("help", Short = "h", HelpText = "Shows this text.")]
		private static void ShowHelp()
		{
			var ti = typeof(Program).GetTypeInfo();

			view.WriteLine("Data Exchange Debugger");
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
			}

			view.WriteLine();
		} // proc ShowHelp

		#endregion

		#region -- Clear, Quit ------------------------------------------------------------

		[InteractiveCommand("clear", HelpText = "Clears the current command buffer.")]
		private static void ClearCommandBuffer()
		{
			currentCommand.Clear();
		} // proc ClearCommandBuffer

		[InteractiveCommand("quit", Short = "q", HelpText = "Exit the application.")]
		private static void DummyQuit() { }

		#endregion

		private static void WriteReturn(IEnumerable<ClientMemberValue> r)
		{
			foreach (var v in r)
			{
				lock (view.SyncRoot)
				{
					Console.Write(v.Name);
					Console.Write(": ");
					using (view.SetColor(ConsoleColor.DarkGray))
					{
						Console.Write("(");
						Console.Write(v.TypeAsString);
						Console.Write(")");
					}
					Console.WriteLine(v.ValueAsString);
				}
			}
		} // proc WriteReturn

		private static async Task SendCommand(string commandText)
		{
			WriteReturn(await session.SendExecuteAsync(commandText));
		} // proc SendCommand

		[InteractiveCommand("use", HelpText = "Activates a new global space, on which the commands are executed.")]
		private static async Task SendUseNode(string node = null)
		{
			var p = await session.SendUseAsync(node ?? String.Empty);
			view.WriteLine($"Current Node: {p}");
		} // func SendUseNode

		[InteractiveCommand("members", Short = "m", HelpText = "Lists the current available global variables.")]
		private static async Task SendVariables()
		{
			WriteReturn(await session.SendMembersAsync(String.Empty));
		} // proc SendVariables
	} // class Program
}
