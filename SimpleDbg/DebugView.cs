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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DebugView
	{
		private readonly object screenLock = new object();
		private bool isConnected = false;
		private string usePath = String.Empty;

		public DebugView()
		{
			UpdateSateText();
		} // ctor

		public IDisposable LockScreen()
		{
			Monitor.Enter(screenLock);
			return new DisposableScope(
				() =>
				{
					Monitor.Exit(screenLock);
					UpdateSateText();
				}
			);
		} // func LockScreen
		
		public void WriteLine()
		{
			WriteLine(String.Empty);
		} // proc WriteLine

		public void WriteLine(string text)
		{
			using (LockScreen())
				Console.Out.WriteLine(text);
		} // proc WriteLine

		public void WriteObject(object o)
		{
			if (o == null)
				WriteLine("<null>");
			else
				WriteLine(o.ToString());
		} // proc WriteObject

		public void WriteError(Exception exception, string message = null)
		{
			if (exception == null)
				return;

			if (!String.IsNullOrEmpty(message))
			{
				using (SetColor(ConsoleColor.DarkRed))
					Console.WriteLine(message);
			}

			var aggEx = exception as AggregateException;
			if (aggEx == null)
			{
				// write exception
				using (LockScreen())
				{
					using (SetColor(ConsoleColor.DarkRed))
					{
						Console.WriteLine($"[{exception.GetType().Name}]");
						Console.WriteLine($"  {exception.Message}");
					}
				}

				// chain exceptions
				WriteError(exception.InnerException);
			}
			else
			{
				foreach (var ex in aggEx.InnerExceptions)
					WriteError(ex);
			}
		} // proc WriteError

		public void WriteError(string text)
		{
			using (LockScreen())
				Console.Error.WriteLine(text);
		} // proc WriteError

		public void Write(string text)
			=> Write(text, ConsoleColor.Gray);

		public void Write(string text, ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
		{
			using (LockScreen())
			{
				using (SetColor(foreground, background))
					Console.Write(text);
			}
		} // proc WriteLine

		#region -- SetColor ---------------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ResetColors : IDisposable
		{
			public ResetColors(ConsoleColor foregroundColor, ConsoleColor backgroundColor)
			{
				this.OldBackgroundColor = Console.BackgroundColor;
				this.OldForegroundColor = Console.ForegroundColor;
				Console.BackgroundColor = backgroundColor;
				Console.ForegroundColor = foregroundColor;
			} // ctor

			public void Dispose()
			{
				Console.ForegroundColor = this.OldForegroundColor;
				Console.BackgroundColor = this.OldBackgroundColor;
			} // proc Dispose

			public ConsoleColor OldForegroundColor { get; }
			public ConsoleColor OldBackgroundColor { get; }
		} // class ResetColors

		public IDisposable SetColor(ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
			=> new ResetColors(foreground, background);

		#endregion

		#region -- SetCursor --------------------------------------------------------------

		private sealed class ResetCursor : IDisposable
		{			
			public ResetCursor(int left, int  top, bool visible)
			{
				this.OldCursorLeft = Console.CursorLeft;
				this.OldCursorTop = Console.CursorTop;
				this.OldVisible = Console.CursorVisible;

				Console.CursorLeft = left;
				Console.CursorTop = top;
				Console.CursorVisible = visible;
			} // ctor

			public void Dispose()
			{
				Console.CursorLeft = OldCursorLeft;
				Console.CursorTop = OldCursorTop;
				Console.CursorVisible = OldVisible;
			} // proc Dispose

			public int OldCursorLeft { get; }
			public int OldCursorTop { get; }
			public bool OldVisible { get; }
		} // class ResetCursor

		public IDisposable SetCursor(int? left = null, int? top = null, bool? visible = null)
			=> new ResetCursor(left ?? Console.CursorLeft, top ?? Console.CursorTop, visible ?? Console.CursorVisible);

		#endregion

		private void UpdateSateText()
		{
			using (SetColor(ConsoleColor.White, isConnected ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed))
			using (SetCursor(Console.WindowWidth - 20, Console.WindowTop, false))
			{
				var stateText = isConnected ? usePath : "Connecting...";

				if (stateText.Length > 18)
					stateText = " ..." + stateText.Substring(stateText.Length - 15, 15) + " ";
				else
					stateText = " " + stateText.PadRight(19);

				Console.Write(stateText);
			}
		} // proc UpdateStateText

		public bool IsConnected
		{
			get { return isConnected; }
			set
			{
				if (isConnected != value)
				{
					isConnected = value;
					UpdateSateText();
				}
			}
		} // prop IsConnected

		public string UsePath
		{
			get { return usePath; }
			set
			{
				if (usePath != value)
				{
					usePath = value;
					UpdateSateText();
				}
			}
		} // prop UsePath

		public object SyncRoot => screenLock;
	} // class DebugView
}
