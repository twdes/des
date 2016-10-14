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
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
				
		public ConsoleDebugSession(DebugView view, Uri serverUri) 
			: base(serverUri)
		{
			this.view = view;

			this.DefaultTimeout = 10000;
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

		protected override bool OnConnectionFailure(Exception e)
		{
			var innerException = e.InnerException as WebException;
			ClientAuthentificationInformation authentificationInfo = ClientAuthentificationInformation.Ntlm;
			if (innerException != null && ClientAuthentificationInformation.TryGet(innerException, ref authentificationInfo, false)) // is this a authentification exception
			{
				currentCredentials = Program.GetCredentialsFromUserAsync(authentificationInfo.Realm).Result;
				return true;
			}
			else
			{
				view.WriteError(e, "Connection failed.");
				return false;
			}
		} // proc OnConnectionFailure

		protected override void OnCommunicationException(Exception e)
			=> view.WriteError(e, "Communication exception.");
	} // class ConsoleDebugSession

	#endregion

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class Program
	{
		private static readonly DebugView view = new DebugView();
		private static readonly Regex commandSyntax = new Regex(@"\:(?<cmd>\w+)(?:\s+(?<args>(?:\`[^\`]*\`)|(?:[^\s]*)))*", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static StringBuilder currentCommand = new StringBuilder(); // holds the current script
		private static ConsoleDebugSession session;

		private static TaskCompletionSource<ICredentials> credentialGet = null;
		private static ClientDebugException lastRemoteException = null;

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

			// connection
			view.IsConnected = false;
			session = new ConsoleDebugSession(view, new Uri(arguments.Uri));
			
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
		} // proc RunDebugProgram

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
					return null;

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

			return new NetworkCredential(userName, sec);
		} // GetCredentialsFromUser

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

		private static void WriteReturn(string indent, IEnumerable<ClientMemberValue> r)
		{
			foreach (var v in r)
			{
				lock (view.SyncRoot)
				{
					Console.Write(indent);
					Console.Write(v.Name);
					if (v.Value is IEnumerable<ClientMemberValue>)
						WriteReturn(indent + "    ", (IEnumerable<ClientMemberValue>)v.Value);
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

		private static async Task SendCommand(string commandText)
		{
			WriteReturn(String.Empty, await session.SendExecuteAsync(commandText));
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
			WriteReturn(String.Empty, await session.SendMembersAsync(String.Empty));
		} // proc SendVariables

		private static void PrintList(string indent, XElement x)
		{
			foreach (var c in x.Elements("n"))
			{
				Console.WriteLine("{0}{1}: {2}", indent, c.GetAttribute("name", String.Empty), c.GetAttribute("displayName", String.Empty));
				PrintList(indent + "    ", c);
			}
		} // proc PrintList

		[InteractiveCommand("list", HelpText = "Lists the current nodes.")]
		private static async Task SendList(bool recursive = false)
		{
			var x = await session.SendListAsync(recursive);
			using (view.LockScreen())
				PrintList(String.Empty, x);
		} // func SendList

		[InteractiveCommand("timeout", Short = "t", HelpText = "Sets the timeout in ms.")]
		private static void SetTimeout(int timeout = -1)
		{
			if (timeout >= 0)
				session.DefaultTimeout = timeout;
			view.WriteLine($"Timeout is: {session.DefaultTimeout:N0}ms");
		} // func SendList

		private static void SetLastRemoteException(Exception e)
		{
			lastRemoteException = null;
			while (e != null)
			{
				var re = e as ClientDebugException;
				if (re != null)
				{
					lastRemoteException = re;
					return;
				}
				e = e.InnerException;
			}
		} // proc SetLastRemoteException

		[InteractiveCommand("lastex", HelpText = "Detail for the last remote exception.")]
		private static void LastException()
		{
			if (lastRemoteException != null)
			{
				view.WriteError(lastRemoteException);
				view.WriteLine();
				view.WriteError(lastRemoteException.StackTrace);
			}
		} // proc ClearCommandBuffer

	} // class Program
}
