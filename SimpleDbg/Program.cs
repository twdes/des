using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class SimpleDebugArguments
	{
		[Option('o', HelpText = "Uri to the server.", Required = true)]
		public string Uri { get; set; }

		[Option("wait", HelpText = "Wait time before, the connection will be established (in ms).")]
		public int Wait { get; set; } = 0;
	} // class SimpleDebugArguments

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class Program
	{
		public static int Main(string[] args)
		{
			var parser = new Parser(
				s =>
				{
					s.CaseSensitive = false;
					s.IgnoreUnknownArguments = false;
				});

			var  r =parser.ParseArguments<SimpleDebugArguments>(args);

			return r.MapResult<SimpleDebugArguments, int>(
				options =>
				{
					if (options.Wait > 0)
						Thread.Sleep(options.Wait);

					try
					{
						Task.Factory.StartNew(RunDebugProgram(options).Wait).Wait();
					}
					catch (AggregateException e)
					{
						Console.WriteLine(e.InnerException.Message);
					}
					catch (Exception e)
					{
						Console.WriteLine(e.Message);
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
			var socket = new ClientWebSocket();

			// connection
			
			socket.Options.AddSubProtocol("dedbg");
			await socket.ConnectAsync(new Uri(arguments.Uri), CancellationToken.None);

			// start request loop
			while (socket.State == WebSocketState.Open)
			{
				Console.Write("> ");
				var cmd = Console.ReadLine();
				await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(cmd)), WebSocketMessageType.Text, true, CancellationToken.None);
			}

			/*
			 * Zwei modi: command
			            > interactive
		*/

		} // proc RunDebugProgram
	} // class Program
}
