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
using Neo.Console;
using Neo.IronLua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
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
				var first = true;
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					if (String.IsNullOrEmpty(line))
					{
						if (currentCommand.Length > 0)
							AppendCore(currentCommand.ToString());
						currentCommand.Clear();
						first = true;
					}
					else
					{
						if (first)
							first = false;
						else
							currentCommand.AppendLine();
						if (line != "\t")
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

		/// <summary>Remove history item</summary>
		/// <param name="idx"></param>
		public void RemoveAt(int idx)
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

		public string this[int index] => history[index];
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
}
