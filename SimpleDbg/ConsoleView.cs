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
using System.IO;
using System.Linq;
using System.Text;
using Neo.Console;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class SimpleDebugCommandHistory ----------------------------------------

	internal sealed class SimpleDebugCommandHistory : IConsoleReadLineHistory
	{
		private readonly FileInfo historyFile;
		private readonly List<string> history = new List<string>();
		private long historySize = 0L;
		private DateTime lastReadStamp;
		private int lastCheckTick;

		public SimpleDebugCommandHistory()
		{
			historyFile = new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TecWare", "SimpleDebug.txt"));

			Read();
		} // ctor

		private void Read()
		{
			historySize = 0;
			history.Clear();

			historyFile.Refresh();
			if (!historyFile.Exists)
				return;

			using (var sr = new StreamReader(historyFile.FullName, true))
			{
				var currentCommand = new StringBuilder();
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					if (String.IsNullOrEmpty(line))
					{
						if (currentCommand.Length > 0)
							AppendCore(currentCommand.ToString());
						currentCommand.Clear();
					}
					else
					{
						if (currentCommand.Length > 0)
							currentCommand.AppendLine();
						currentCommand.Append(line);
					}
				}

				if (currentCommand.Length > 0)
					AppendCore(currentCommand.ToString());
			}

			lastReadStamp = historyFile.LastWriteTimeUtc;
			lastCheckTick = Environment.TickCount;
		} // proc Read

		private void CheckForReload()
		{
			if (unchecked(Environment.TickCount - lastCheckTick) < 1000)
				return;

			// refresh file
			historyFile.Refresh();
			if (historyFile.LastWriteTimeUtc > lastReadStamp)
				Read();
			lastCheckTick = Environment.TickCount;
		} // proc CheckForReload

		private void Save()
		{
			if (!historyFile.Directory.Exists)
				historyFile.Directory.Create();

			using (var sw = new StreamWriter(historyFile.FullName, false, Encoding.UTF8))
			{
				var first = true;
				foreach (var cur in history)
				{
					if (first)
						first = false;
					else
						sw.WriteLine(); // command seperator

					foreach (var (startAt, len) in cur.SplitNewLinesTokens())
					{
						if (len == 0)
							sw.WriteLine("\t");
						else
							sw.WriteLine(cur.Substring(startAt, len));
					}
				}
			}

			historyFile.Refresh();
			lastReadStamp = historyFile.LastWriteTimeUtc;
			lastCheckTick = Environment.TickCount;
		} // proc Save

		private void RemoveAt(int idx)
		{
			if (idx < history.Count)
			{
				historySize -= history[idx].Length;
				history.RemoveAt(idx);
			}
		} // proc RemoveAt

		private void AppendCore(string command)
		{
			// remove beginning items
			while (historySize > 0x40000)
				RemoveAt(0);

			history.Add(command);
			historySize += command.Length;
		} // proc AppendCore

		public void Append(string command)
		{
			CheckForReload();

			while (true)
			{
				var idx = history.FindIndex(c => String.Compare(c, command, StringComparison.OrdinalIgnoreCase) == 0);
				if (idx == -1)
					break;
				RemoveAt(idx);
			}

			AppendCore(command);

			Save();
		} // proc Append

		public IEnumerator<string> GetEnumerator()
			=> history.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() 
			=> GetEnumerator();

		public int Count
		{
			get
			{
				CheckForReload();
				return history.Count;
			}
		} // prop Count

		public string this[int index] =>history[index];
	} // class SimpleDebugCommandHistory

	#endregion

	#region -- class SimpleDebugConsoleReadLineManager --------------------------------

	internal sealed class SimpleDebugConsoleReadLineManager : IConsoleReadLineManager, IConsoleReadLineScanner, IConsoleReadLineHistory
	{
		private readonly SimpleDebugCommandHistory history;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public SimpleDebugConsoleReadLineManager(SimpleDebugCommandHistory history)
		{
			this.history = history ?? throw new ArgumentNullException(nameof(history));
		} // ctor

		#endregion

		#region -- IConsoleReadLineManager implementation -----------------------------

		public bool CanExecute(string command)
			=> (command.Length > 0 && command[0] == ':') || command.EndsWith(Environment.NewLine);

		public string GetPrompt()
			=> "> ";

		#endregion

		#region -- IConsoleReadLineScanner implementation -----------------------------

		public int GetNextToken(int offset, string text, bool reverse)
			=> -1;

		private static ConsoleColor GetTokenColor(LuaToken typ)
		{
			switch (typ)
			{
				case LuaToken.KwAnd:
				case LuaToken.KwBreak:
				case LuaToken.KwCast:
				case LuaToken.KwConst:
				case LuaToken.KwDo:
				case LuaToken.KwElse:
				case LuaToken.KwElseif:
				case LuaToken.KwEnd:
				case LuaToken.KwFalse:
				case LuaToken.KwFor:
				case LuaToken.KwForEach:
				case LuaToken.KwFunction:
				case LuaToken.KwGoto:
				case LuaToken.KwIf:
				case LuaToken.KwIn:
				case LuaToken.KwLocal:
				case LuaToken.KwNil:
				case LuaToken.KwNot:
				case LuaToken.KwOr:
				case LuaToken.KwRepeat:
				case LuaToken.KwReturn:
				case LuaToken.KwThen:
				case LuaToken.KwTrue:
				case LuaToken.KwUntil:
				case LuaToken.KwWhile:
				case LuaToken.DotDotDot:
					return ConsoleColor.White;
				case LuaToken.Comment:
				case LuaToken.InvalidComment:
					return ConsoleColor.Green;
				case LuaToken.String:
				case LuaToken.InvalidString:
					return ConsoleColor.DarkYellow;
				case LuaToken.Assign:
				case LuaToken.BitAnd:
				case LuaToken.BitOr:
				case LuaToken.BracketClose:
				case LuaToken.BracketCurlyClose:
				case LuaToken.BracketCurlyOpen:
				case LuaToken.BracketOpen:
				case LuaToken.BracketSquareClose:
				case LuaToken.BracketSquareOpen:
				case LuaToken.Colon:
				case LuaToken.Comma:
				case LuaToken.Dot:
				case LuaToken.DotDot:
				case LuaToken.Equal:
				case LuaToken.Greater:
				case LuaToken.GreaterEqual:
				case LuaToken.Lower:
				case LuaToken.LowerEqual:
				case LuaToken.Minus:
				case LuaToken.NotEqual:
				case LuaToken.Percent:
				case LuaToken.Semicolon:
				case LuaToken.ShiftLeft:
				case LuaToken.ShiftRight:
				case LuaToken.Slash:
				case LuaToken.SlashShlash:
				case LuaToken.Star:
					return ConsoleColor.DarkGray;
				case LuaToken.Number:
					return ConsoleColor.DarkCyan;
				case LuaToken.InvalidChar:
					return ConsoleColor.Red;
				default:
					return ConsoleColor.Gray;
			}
		} // func GetTokenColor

		public void Scan(IConsoleReadLineScannerSource source)
		{
			if (source.LineCount == 0)
				return;

			var cmdLine = source[0];
			if (cmdLine.StartsWith(":")) // command
			{
				var startOfArgs = cmdLine.IndexOf(' ');
				if (startOfArgs == -1)
					startOfArgs = cmdLine.Length;
				source.AppendToken(0, 0, 0, startOfArgs, ConsoleColor.White);
				source.AppendToken(0, startOfArgs, 0, cmdLine.Length, ConsoleColor.Gray);
			}
			else
			{
				using (var lex = LuaLexer.Create("cmd.lua", source.TextReader, true, 0, 0, 0))
				{
					lex.Next();
					while (lex.Current.Typ != LuaToken.Eof)
					{
						source.AppendToken(lex.Current.Start.Line, lex.Current.Start.Col, lex.Current.End.Line, lex.Current.End.Col, GetTokenColor(lex.Current.Typ));

						lex.Next();
					}
				}
			}
		} // proc Scan

		#endregion

		#region -- IConsoleReadLineHistory implementation -----------------------------

		IEnumerator<string> IEnumerable<string>.GetEnumerator()
			=> history.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator()
			=> history.GetEnumerator();

		string IReadOnlyList<string>.this[int index]
			=> history[index];

		int IReadOnlyCollection<string>.Count
			=> history.Count;

		#endregion
	} // class SimpleDebugConsoleReadLineManager

	#endregion

	#region -- enum ConnectionState ---------------------------------------------------

	public enum ConnectionState
	{
		None = 0,
		Connecting = 1,
		ConnectedHttp = 2,
		ConnectedDebug = 4,
		ConnectedEvent = 8
	} // enum ConnectionState

	#endregion

	#region -- class ConnectionStateOverlay -------------------------------------------

	internal sealed class ConnectionStateOverlay : ConsoleOverlay
	{
		private ConnectionState currentState = ConnectionState.None;
		private string currentPath = "/";

		public ConnectionStateOverlay(ConsoleApplication app)
		{
			Application = app ?? throw new ArgumentNullException(nameof(app));
			ZOrder = 1000;

			// create char buffer
			Resize(20, 1);
			Left = -Width;
			Top = 0;

			// position
			Position = ConsoleOverlayPosition.Window;
			OnResize();
		} // ctor

		protected override void OnRender()
		{
			ConsoleColor GetStateColor()
			{
				if ((currentState & ConnectionState.ConnectedDebug) != 0)
					return ConsoleColor.DarkGreen;
				else if ((currentState & ConnectionState.ConnectedHttp) != 0)
					return ConsoleColor.DarkBlue;
				else
					return ConsoleColor.DarkRed;
			} // func GetStateColor

			var stateText = currentState == ConnectionState.None ? "None..." : (currentState == ConnectionState.Connecting ? "Connecting..." : currentPath);
			if (stateText.Length > 18)
				stateText = " ..." + stateText.Substring(stateText.Length - 15, 15) + " ";
			else
				stateText = " " + stateText.PadRight(19);

			Content.Write(0, 0, stateText, null, ConsoleColor.White, GetStateColor());
		} // proc OnRender

		private void SetStateCore(ConnectionState state, bool? set)
		{
			var newState = set.HasValue
				? (set.Value ? state | currentState : ~state & currentState)
				: state;

			if (newState != currentState)
			{
				currentState = newState;
				Invalidate();
			}
		} // proc SetStateCore

		private void SetPathCore(string newPath)
		{
			if(newPath != currentPath)
			{
				currentPath = newPath;
				Invalidate();
			}
		} // proc SetPathCore

		public void SetState(ConnectionState state, bool? set)
			=> Application?.Invoke(() => SetStateCore(state, set));

		public void SetPath(string path)
			=> Application?.Invoke(() => SetPathCore(path));
	} // class ConnectionStateOverlay

	#endregion

	#region -- class SelectListOverlay ------------------------------------------------

	internal sealed class SelectListOverlay : ConsoleDialogOverlay
	{
		private readonly KeyValuePair<object, string>[] values;
		private int selectedIndex = -1;
		private string title;

		public SelectListOverlay(ConsoleApplication app, IEnumerable<KeyValuePair<object, string>> values)
		{
			this.values = values.ToArray() ?? throw new ArgumentNullException(nameof(values));

			Application = app ?? throw new ArgumentNullException(nameof(values));

			var windowWidth = app.WindowRight - app.WindowLeft + 1;
			var windowHeight = app.WindowBottom - app.WindowTop + 1;

			var maxWidth = this.values.Max(GetLineLength) + 2;
			var maxHeight = this.values.Length + 1;

			Position = ConsoleOverlayPosition.Window;
			Resize(
				Math.Min(windowWidth, maxWidth),
				Math.Min(windowHeight, maxHeight)
			);

			Left = (windowWidth - Width) / 2;
			Top = (windowHeight - Height) / 2;
		} // ctor

		protected override void OnRender()
		{
			RenderTitle("Use");

			for (var i = 0; i < values.Length; i++)
			{
				var top = i + 1;
				Content.Set(0, top, ' ', background: BackgroundColor);
				var foregroundColor = i == selectedIndex ? ConsoleColor.Black : ForegroundColor;
				var backgroundColor = i == selectedIndex ? ConsoleColor.Cyan : BackgroundColor;
				var (endLeft, endTop) = Content.Write(1, top, values[i].Value, foreground: foregroundColor, background: backgroundColor);
				if (endLeft < Width)
					Content.Fill(endLeft, top, Width - 2, top, ' ', background: backgroundColor);
				Content.Set(Width - 1, top, ' ', background: BackgroundColor);
			}
		} // proc OnRender

		public override bool OnHandleEvent(EventArgs e)
		{
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.Key)
				{
					case ConsoleKey.DownArrow:
						SelectIndex(selectedIndex + 1);
						return true;
					case ConsoleKey.UpArrow:
						SelectIndex(selectedIndex - 1);
						return true;
				}
			}

			return base.OnHandleEvent(e);
		} // func OnHandleEvent

		private void SelectValue(object key)
			=> SelectIndex(Array.FindIndex(values, v => Equals(v.Key, key)));

		public void SelectIndex(int newIndex)
		{
			if (newIndex == -1)
			{
				selectedIndex = -1;
				Invalidate();
			}
			else if (newIndex >= 0 && newIndex < values.Length && newIndex != selectedIndex)
			{
				selectedIndex = newIndex;
				Invalidate();
			}
		} // proc SelectIndex

		protected override void OnAccept()
		{
			if (selectedIndex == -1)
				return;
			base.OnAccept();
		} // proc OnAccept

		private static int GetLineLength(KeyValuePair<object, string> p)
			=> p.Value?.Length ?? 0;

		public string Title
		{
			get => title;
			set
			{
				if (title != value)
				{
					title = value;
					Invalidate();
				}
			}
		} // proc Title

		public object SelectedValue
		{
			get => selectedIndex >= 0 && selectedIndex < values.Length ? values[selectedIndex].Key : null;
			set => SelectValue(value);
		} // prop SelectedValue
	} // class UseListOverlay 

	#endregion

	#region -- class ConsoleView ------------------------------------------------------

	internal static class ConsoleView
	{
		#region -- WriteError, WriteWarning -------------------------------------------

		public static void WriteWarning(this ConsoleApplication app, string message)
			=> WriteLine(app, ConsoleColor.DarkYellow, message);

		public static void WriteError(this ConsoleApplication app, string message)
			=> WriteLine(app, ConsoleColor.Red, message);

		public static void WriteError(this ConsoleApplication app, Exception exception, string message = null)
		{
			if (exception == null)
				return;

			if (!String.IsNullOrEmpty(message))
				WriteError(app, message);

			if (exception is AggregateException aggEx)
			{
				foreach (var ex in aggEx.InnerExceptions)
					WriteError(app, ex);
			}
			else
			{
				// write exception
				using (app.Color(ConsoleColor.Red))
				{
					if (exception is DebugSocketException cde)
						app.WriteLine($"[R:{cde.ExceptionType}]");
					else
						app.WriteLine($"[{exception.GetType().Name}]");

					app.WriteLine($"  {exception.Message}");
				}

				// chain exceptions
				WriteError(app, exception.InnerException);
			}
		} // proc WriteError

		#endregion

		#region -- WriteLine ----------------------------------------------------------

		public static void WriteObject(this ConsoleApplication app, object o)
			=> app.WriteLine(o == null ? "<null>" : o.ToString());

		public static void Write(this ConsoleApplication app, ConsoleColor[] colors, string[] parts)
		{
			for (var i = 0; i < parts.Length; i++)
			{
				if (parts[i] == null)
					continue;
				using (app.Color(colors[i]))
					app.Write(parts[i]);
			}
		} // proc Write

		public static void WriteLine(this ConsoleApplication app, ConsoleColor color, string text)
		{
			using (app.Color(color))
				app.WriteLine(text);
		} // proc WriteLine

		public static void WriteLine(this ConsoleApplication app, ConsoleColor[] colors, string[] parts, bool rightAlign = false)
		{
			if (rightAlign)
				MoveRight(app, parts.Sum(c => c?.Length ?? 0) + 1);

			Write(app, colors, parts);
			app.WriteLine();
		} // proc WriteLine

		public static void MoveRight(this ConsoleApplication app, int right)
			=> app.CursorLeft = app.WindowRight - right + 1;

		#endregion
	} // class ConsoleView

	#endregion
}
