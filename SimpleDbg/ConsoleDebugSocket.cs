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
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Neo.Console;
using TecWare.DE.Networking;

namespace TecWare.DE.Server
{
	#region -- class ConsoleDebugSocket -----------------------------------------------

	internal sealed class ConsoleDebugSocket : DebugSocket
	{
		private readonly ConsoleApplication app;
		private readonly Stopwatch startUp;

		public ConsoleDebugSocket(ConsoleApplication app, DEHttpClient http)
			: base(http)
		{
			this.app = app ?? throw new ArgumentNullException(nameof(app));

			startUp = Stopwatch.StartNew();
			DefaultTimeout = 0;
		} // ctor

		protected override void OnCurrentUsePathChanged()
			=> Program.PostNewUsePath(CurrentUsePath);

		protected override Task OnConnectionEstablishedAsync()
		{
			Program.SetConnectionState(ConnectionState.ConnectedDebug, true);
			return base.OnConnectionEstablishedAsync();
		} // proc OnConnectionEstablishedAsync

		protected override Task OnConnectionLostAsync()
		{
			Program.SetConnectionState(ConnectionState.ConnectedDebug, false);
			return base.OnConnectionLostAsync();
		} // proc OnConnectionLostAsync

		protected override Task<bool> OnConnectionFailureAsync(Exception e)
		{
			app.WriteError(e, "Connection failed.");
			return Task.FromResult(false);
		} // proc OnConnectionFailure

		protected override void OnMessage(char type, string message)
		{
			IDisposable SetColorByType()
			{
				switch (type)
				{
					case 'E':
						return app.Color(ConsoleColor.DarkRed);
					case 'W':
						return app.Color(ConsoleColor.DarkYellow);
					case 'I':
						return app.Color(ConsoleColor.White);
					default:
						return app.Color(ConsoleColor.Gray);
				}
			} // func SetColorByType

			using (SetColorByType())
				app.WriteLine(message);
		} // proc OnMessage

		protected override void OnStartScript(DebugRunScriptResult.Script script, string message)
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

			app.WriteLine(
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

		protected override void OnTestResult(DebugRunScriptResult.Test test, string message)
		{
			var success = test.Success;
			app.WriteLine(
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

		protected override Task OnCommunicationExceptionAsync(Exception e)
		{
			app.WriteError(e, "Communication exception.");
			return Task.CompletedTask;
		} // proc OnCommunicationExceptionAsync
	} // class ConsoleDebugSocket

	#endregion
}
