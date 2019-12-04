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
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Neo.Console;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- struct ConsoleToken ----------------------------------------------------

	public struct ConsoleToken
	{
		public ConsoleToken(StringBuilder content, int offset, int length, ConsoleColor color)
		{
			Content = content;
			Offset = offset;
			Length = length;
			Color = color;
		} // ctor

		public StringBuilder Content { get; }
		public int Offset { get; }
		public int Length { get; }
		public ConsoleColor Color { get; }

		public string Text => Content.ToString(Offset, Length);
	} // struct ConsoleToken

	#endregion

	#region -- class ConsoleReadLineOverlay -------------------------------------------

	#region -- interface IConsoleReadLineManager --------------------------------------

	/// <summary>Basic ReadLine interface.</summary>
	public interface IConsoleReadLineManager
	{
		/// <summary>Leave the read line.</summary>
		/// <param name="command">Currently, collected text.</param>
		/// <returns></returns>
		bool CanExecute(string command);
		/// <summary>Get prompt for the line.</summary>
		/// <returns>Prompt or nothing.</returns>
		string GetPrompt();
	} // interface IConsoleReadLineManager

	#endregion

	#region -- interface IConsoleReadLineScannerSource --------------------------------

	/// <summary>Text buffer access (implemented by read line).</summary>
	public interface IConsoleReadLineScannerSource
	{
		/// <summary>Set color for a specific text part.</summary>
		/// <param name="lineStart"></param>
		/// <param name="columnStart"></param>
		/// <param name="lineEnd"></param>
		/// <param name="columnEnd"></param>
		/// <param name="color"></param>
		void AppendToken(int lineStart, int columnStart, int lineEnd, int columnEnd, ConsoleColor color);

		/// <summary>Sequential text buffer of the current text buffer.</summary>
		TextReader TextReader { get; }

		/// <summary>Content of a line.</summary>
		/// <param name="lineIndex">Index of the line</param>
		/// <returns></returns>
		string this[int lineIndex] { get; }
		/// <summary>Number of lines.</summary>
		int LineCount { get; }
	} // interface IConsoleReadLineScannerSource

	#endregion

	#region -- interface IConsoleReadLineScanner --------------------------------------

	/// <summary>Colorization implementation.</summary>
	public interface IConsoleReadLineScanner
	{
		/// <summary>Starts the colorization.</summary>
		/// <param name="source">Text buffer source.</param>
		void Scan(IConsoleReadLineScannerSource source);

		/// <summary>Get the next offset for strg+cursor key.</summary>
		/// <param name="offset"></param>
		/// <param name="text"></param>
		/// <param name="left"></param>
		/// <returns>-1 for not implemented.</returns>
		int GetNextToken(int offset, string text, bool left);
	} // interface IConsoleReadLineScanner

	#endregion

	#region -- interface IConsoleReadLineHistory --------------------------------------

	/// <summary>Command history</summary>
	public interface IConsoleReadLineHistory : IReadOnlyList<string>
	{
	} // interface IConsoleReadLineHistory

	#endregion

	public sealed class ConsoleReadLineOverlay : ConsoleFocusableOverlay
	{
		#region -- class SingleLineManager --------------------------------------------

		private sealed class SingleLineManager : IConsoleReadLineManager
		{
			private readonly string prompt;

			public SingleLineManager(string prompt)
			{
				this.prompt = prompt ?? String.Empty;
			} // ctor

			public bool CanExecute(string command)
				=> true;

			public string GetPrompt()
				=> prompt;

			public static IConsoleReadLineManager Default { get; } = new SingleLineManager(String.Empty);
		} // class SingleLineManager

		#endregion

		#region -- class InputLine ----------------------------------------------------

		private sealed class InputLine
		{
			private readonly string prompt;
			public readonly StringBuilder content = new StringBuilder();
			private int height = 1;

			public ConsoleToken[] tokenCache = null;

			public InputLine(string prompt)
			{
				this.prompt = prompt ?? String.Empty;
			} // ctor

			#region -- Insert, Remove -------------------------------------------------

			public void InsertLine(int index, StringBuilder text)
			{
				for (var i = 0; i < text.Length; i++)
					content.Insert(index++, text[i]);
				ClearTokenCache();
			} // proc AppendLine

			public void InsertLine(ref int index, string text, int startAt, int count)
			{
				var endAt = startAt + count;
				for (var i = startAt; i < endAt; i++)
				{
					if (text[i] == '\t')
					{
						content.Insert(index++, ' ');
						content.Insert(index++, ' ');
						content.Insert(index++, ' ');
						content.Insert(index++, ' ');
					}
					else if (!Char.IsControl(text[i]))
						content.Insert(index++, text[i]);
				}
				ClearTokenCache();
			} // proc AppendLine

			public bool Insert(ref int index, char c, bool overwrite)
			{
				if (index < content.Length)
				{
					if (overwrite)
						content[index] = c;
					else
						content.Insert(index, c);
					return ClearTokenCache();
				}
				else
				{
					index = content.Length;
					content.Append(c);
					return ClearTokenCache();
				}
			} // func Insert

			public bool Remove(int index)
			{
				if (content.Length > 0 && index < content.Length)
				{
					content.Remove(index, 1);
					return ClearTokenCache();
				}
				else
					return false;
			} // func Remove

			public void FixLineEnd(ref int currentLineOffset)
			{
				if (currentLineOffset > content.Length)
					currentLineOffset = content.Length;
			} // func FixLineEnd

			public int UpdateHeight(int maxWidth)
			{
				var totalLength = TotalLineLength;
				if (totalLength == 0)
					height = 1;
				else
				{
					var h = totalLength / maxWidth;
					if (h == 0)
						height = 1;
					else
					{
						var r = totalLength / maxWidth;
						height = r == 0 ? h : h + 1;
					}
				}
				return height;
			} // func GetHeight

			#endregion

			public bool ClearTokenCache()
			{
				tokenCache = null;
				return true;
			} // func ClearTokenCache

			public void SetDefaultToken()
			{
				tokenCache = new ConsoleToken[] { new ConsoleToken(content, 0, content.Length, ConsoleColor.Gray) };
			} // proc SetDefault

			public bool IsTokenCacheEmpty => tokenCache == null;

			public string Prompt => prompt;
			public string Content => content.ToString();

			public int LineHeight => height;

			public int ContentLength => content.Length;
			public int TotalLineLength => prompt.Length + content.Length;
		} // class InputLine

		#endregion

		#region -- class InputLineScannerSource ---------------------------------------

		private sealed class InputLineScannerSource : TextReader, IConsoleReadLineScannerSource
		{
			private int currentLineIndex = 0;
			private int currentLineOffset = 0;

			private int lastLineIndex = 0;
			private int lastColumnIndex = 0;
			private ConsoleColor currentColor = ConsoleColor.Gray;
			private List<ConsoleToken> lineTokens = new List<ConsoleToken>();

			private readonly List<InputLine> lines;

			public InputLineScannerSource(List<InputLine> lines)
			{
				this.lines = lines ?? throw new ArgumentNullException(nameof(lines));
			} // ctor

			public override void Close()
			{
				try
				{
					var lastLineIndex = lines.Count - 1;
					EmitCurrentColor(lastLineIndex, lines[lastLineIndex].ContentLength);
					if (lineTokens.Count > 0)
						EmitTokens();
				}
				finally
				{
					base.Close();
				}
			} // proc Close

			public override int Read()
			{
				if (currentLineIndex < lines.Count)
				{
					var len = lines[currentLineIndex].ContentLength;
					if (currentLineOffset < len)
						return lines[currentLineIndex].Content[currentLineOffset++];
					else
					{
						currentLineOffset = 0;
						currentLineIndex++;
						return '\n';
					}
				}
				else
					return -1;
			} // func Read

			public override int Read(char[] buffer, int index, int count)
			{
				// todo: is not used, currently.
				return base.Read(buffer, index, count);
			} // func Read

			public override int ReadBlock(char[] buffer, int index, int count)
				=> Read(buffer, index, count);

			public override Task<int> ReadAsync(char[] buffer, int index, int count)
				=> Task.FromResult(Read(buffer, index, count));

			public override Task<int> ReadBlockAsync(char[] buffer, int index, int count)
				=> Task.FromResult(Read(buffer, index, count));

			public void AppendToken(int lineStart, int columnStart, int lineEnd, int columnEnd, ConsoleColor color)
			{
				if (color != currentColor)
				{
					EmitCurrentColor(lineStart, columnStart);
					currentColor = color;
					lastLineIndex = lineStart;
					lastColumnIndex = columnStart;
				}
			} // proc UpdateToken

			private void EmitCurrentColor(int lineTo, int columnTo)
			{
				int len;

				while (lastLineIndex < lineTo) // fill lines with current color
				{
					var cnt = lines[lastLineIndex].content;
					len = cnt.Length - lastColumnIndex;
					if (len > 0)
						lineTokens.Add(new ConsoleToken(cnt, lastColumnIndex, len, currentColor));

					EmitTokens();
				}

				// create current token
				len = columnTo - lastColumnIndex;
				if (len > 0 && lastLineIndex < lines.Count)
				{
					var cnt = lines[lastLineIndex].content;
					lineTokens.Add(new ConsoleToken(cnt, lastColumnIndex, len, currentColor));
					lastColumnIndex = columnTo;
				}
			} // proc EmitCurrentColor

			private void EmitTokens()
			{
				lines[lastLineIndex].tokenCache = lineTokens.ToArray();
				lineTokens.Clear();

				lastLineIndex++;
				lastColumnIndex = 0;
			} // proc EmitTokens

			TextReader IConsoleReadLineScannerSource.TextReader => this;

			public string this[int lineIndex] => lines[lineIndex].Content;
			public int LineCount => lines.Count;
		} // class InputLineScannerSource

		#endregion

		private readonly IConsoleReadLineManager manager;
		private readonly TaskCompletionSource<string> commandAccepted;

		private int currentLineIndex = 0;
		private int currentLineOffset = 0;
		private bool overwrite = false;
		private readonly List<InputLine> lines = new List<InputLine>();

		private string lastInputCommand = null;
		private int lastLineIndex = 0;
		private int lastLineOffset = 0;
		private int currentHistoryIndex = -1;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public ConsoleReadLineOverlay(IConsoleReadLineManager manager, TaskCompletionSource<string> commandAccepted = null)
		{
			this.manager = manager ?? SingleLineManager.Default;
			this.commandAccepted = commandAccepted;

			this.lines.Add(new InputLine(this.manager.GetPrompt()));
		} // ctor

		#endregion

		#region -- OnResize, OnRender -------------------------------------------------

		protected override void OnParentResize()
		{
			base.OnParentResize();
			Invalidate();
		} // proc OnParentResize

		protected override void OnRender()
		{
			var w = Application.BufferWidth;

			// build new buffer the can hold the text
			var totalHeight = 0;
			var reLex = false;
			var scan = manager as IConsoleReadLineScanner;
			for (var i = 0; i < lines.Count; i++)
			{
				totalHeight += lines[i].UpdateHeight(w);
				if (lines[i].IsTokenCacheEmpty)
				{
					if (scan == null)
						lines[i].SetDefaultToken();
					else
						reLex = true;
				}
			}
			Resize(w, totalHeight);

			if (reLex)
			{
				using (var source = new InputLineScannerSource(lines))
				{
					scan.Scan(source);
					source.Close();
				}
			}

			var top = 0;

			for (var i = 0; i < lines.Count; i++)
			{
				var l = lines[i];

				// first write the prompt
				var (endLeft, endTop) = Content.Write(0, top, l.Prompt);

				// enforce tokens
				if (l.IsTokenCacheEmpty)
					l.SetDefaultToken();
				// write tokens
				foreach (var tok in l.tokenCache)
					(endLeft, endTop) = Content.Write(endLeft, endTop, tok.Text, lineBreakTo: 0, foreground: tok.Color);

				if (endLeft < w)
					Content.Fill(endLeft, endTop, w - 1, endTop, ' ');

				// update cursor
				if (i == currentLineIndex)
				{
					var cursorIndex = currentLineOffset + l.Prompt.Length;
					if (cursorIndex > l.TotalLineLength)
						cursorIndex = l.TotalLineLength;
					SetCursor(cursorIndex % w, top + cursorIndex / w, overwrite ? 100 : 25);
				}

				top = endTop + 1;
			}
		} // proc OnRender

		#endregion

		#region -- Last input command -------------------------------------------------

		private void ClearLastInputCommand()
		{
			currentHistoryIndex = -1;
			lastLineIndex = 0;
			lastLineOffset = 0;
			lastInputCommand = null;
		} // proc ClearLastInputCommand

		private void SaveLastInputCommand()
		{
			lastLineIndex = currentLineIndex;
			lastLineOffset = currentLineOffset;
			lastInputCommand = Command;
		} // proc SaveLastInputCommand

		private void ResetLastInputCommand()
		{
			Command = lastInputCommand;
			currentLineIndex = lastLineIndex;
			currentLineOffset = lastLineOffset;
			ClearLastInputCommand();
		} // proc ResetLastInputCommand

		#endregion

		#region -- OnHandleEvent ------------------------------------------------------

		private string executeCommandOnKeyUp = null;

		public override bool OnHandleEvent(EventArgs e)
		{
			// handle key down events
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.KeyChar)
				{
					#region -- Return --
					case '\r':
						var command = Command;
						if (currentLineIndex >= lines.Count - 1 && manager.CanExecute(command))
						{
							executeCommandOnKeyUp = command;
						}
						else
						{
							InsertNewLine();
							Invalidate();
						}
						return true;
					#endregion
					#region -- Escape --
					case '\x1B':
						if (lastInputCommand != null)
						{
							ResetLastInputCommand();
							Invalidate();
						}
						else if (currentLineIndex > 0 || currentLineOffset > 0)
						{
							currentLineIndex = 0;
							currentLineOffset = 0;
							Invalidate();
						}
						else if (CurrentLine.ContentLength > 0 && lines.Count > 0)
						{
							Command = String.Empty;
							Invalidate();
						}
						return true;
					#endregion
					#region -- Backspace --
					case '\b':
						CurrentLine.FixLineEnd(ref currentLineOffset);
						if (currentLineOffset > 0)
						{
							ClearLastInputCommand();
							currentLineOffset--;
							if (CurrentLine.Remove(currentLineOffset))
								Invalidate();
						}
						else if (currentLineIndex > 0)
						{
							ClearLastInputCommand();

							if (currentLineIndex >= lines.Count)
								currentLineIndex = lines.Count - 1;

							var prevLine = lines[currentLineIndex - 1];
							var currLine = CurrentLine;

							currentLineOffset = prevLine.ContentLength;

							if (currLine.ContentLength > 0) // copy content
								prevLine.InsertLine(currentLineOffset, currLine.content);

							lines.RemoveAt(currentLineIndex);
							currentLineIndex--;

							Invalidate();
						}
						return true;
					#endregion
					#region -- Tab --
					case '\t':
						for (var i = 0; i < 4; i++)
						{
							if (CurrentLine.Insert(ref currentLineOffset, ' ', overwrite))
							{
								ClearLastInputCommand();
								currentLineOffset++;
								Invalidate();
							}
						}
						return true;
					#endregion
					default:
						if (!Char.IsControl(keyDown.KeyChar))
						{
							#region -- Char --
							if (CurrentLine.Insert(ref currentLineOffset, keyDown.KeyChar, overwrite))
							{
								ClearLastInputCommand();
								currentLineOffset++;
								Invalidate();
							}
							#endregion
							return true;
						}
						else
						{
							switch (keyDown.Key)
							{
								#region -- Delete --
								case ConsoleKey.Delete:
									if (currentLineOffset >= CurrentLine.ContentLength)
									{
										ClearLastInputCommand();

										if (currentLineIndex < lines.Count - 1)
										{
											CurrentLine.InsertLine(currentLineOffset, lines[currentLineIndex + 1].content);
											lines.RemoveAt(currentLineIndex + 1);
											Invalidate();
										}
									}
									else if (CurrentLine.Remove(currentLineOffset))
									{
										ClearLastInputCommand();
										Invalidate();
									}
									return true;
								#endregion
								#region -- Up,Down,Left,Right --
								case ConsoleKey.UpArrow:
									if (currentLineIndex > 0)
									{
										if (currentLineIndex >= lines.Count)
										{
											if (lines.Count > 1)
												currentLineIndex = lines.Count - 2;
											else
												currentLineIndex = 0;
										}
										else
											currentLineIndex--;
										Invalidate();
									}

									InvalidateCursor();
									return true;
								case ConsoleKey.DownArrow:
									if (currentLineIndex < lines.Count - 1)
									{
										currentLineIndex++;
										Invalidate();
									}
									else if (currentLineIndex >= lines.Count)
										currentLineIndex = lines.Count - 1;

									InvalidateCursor();
									return true;
								case ConsoleKey.LeftArrow:
									if ((keyDown.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
									{
										MoveCursorByToken(CurrentLine.Content, true);
										InvalidateCursor();
										return true;
									}
									else if (currentLineOffset > 0)
									{
										if (currentLineOffset < CurrentLine.ContentLength)
											currentLineOffset--;
										else
											currentLineOffset = CurrentLine.ContentLength - 1;
									}
									else
										currentLineOffset = 0;

									Invalidate();
									InvalidateCursor();
									return true;
								case ConsoleKey.RightArrow:
									if ((keyDown.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
									{
										MoveCursorByToken(CurrentLine.Content, false);
										InvalidateCursor();
										return true;
									}
									else if (currentLineOffset < CurrentLine.ContentLength)
										currentLineOffset++;
									else
										currentLineOffset = CurrentLine.ContentLength;

									Invalidate();
									InvalidateCursor();
									return true;
									#endregion
							}
						}
						break;
				}
			}
			else if (e is ConsoleKeyUpEventArgs keyUp)
			{
				switch (keyUp.Key)
				{
					#region -- Home,End,Up,Down,PageUp,PageDown --
					case ConsoleKey.Enter:
						if (executeCommandOnKeyUp != null)
						{
							ClearLastInputCommand();

							if (commandAccepted != null)
								commandAccepted.SetResult(executeCommandOnKeyUp);

							executeCommandOnKeyUp = null;
						}
						break;
					case ConsoleKey.PageUp:
						MoveHistory(false);
						return true;
					case ConsoleKey.UpArrow:
						{
							if ((keyUp.KeyModifiers & ConsoleKeyModifiers.AltPressed) != 0)
							{
								MoveHistory(false);
								return true;
							}
						}
						break;
					case ConsoleKey.PageDown:
						MoveHistory(true);
						return true;
					case ConsoleKey.DownArrow:
						{
							if (manager is IConsoleReadLineHistory history && (keyUp.KeyModifiers & ConsoleKeyModifiers.AltPressed) != 0)
							{
								MoveHistory(true);
								return true;
							}
						}
						break;
					case ConsoleKey.End:
						if ((keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
							currentLineIndex = lines.Count - 1;
						currentLineOffset = CurrentLine.ContentLength;
						Invalidate();
						return true;
					case ConsoleKey.Home:
						if ((keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
							currentLineIndex = 0;
						currentLineOffset = 0;
						Invalidate();
						return true;
					#endregion
					#region -- Insert --
					case ConsoleKey.Insert:
						overwrite = !overwrite;
						Invalidate();
						return true;
					case ConsoleKey.V:
						if ((keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
						{
							var clipText = GetClipboardText();
							var first = true;
							foreach (var (startAt, len) in Procs.SplitNewLinesTokens(clipText))
							{
								if (first)
									first = false;
								else // new line
									InsertNewLine();

								CurrentLine.InsertLine(ref currentLineOffset, clipText, startAt, len);
								Invalidate();
							}
							return true;
						}
						break;
					#endregion
					default:
						{
							//if (manager is IConsoleReadLineHistory history && (keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0 && keyUp.Key == ConsoleKey.R)
							//	;
						}
						break;
				}
			}

			return base.OnHandleEvent(e);
		} // proc OnHandleEvent

		private static Task DoClipboardActionAsync(ParameterizedThreadStart action, object state)
		{
			var apartmentState = Thread.CurrentThread.GetApartmentState();
			if (apartmentState == ApartmentState.STA)
			{
				action(state);
				return Task.CompletedTask;
			}
			else
			{
				var thread = new Thread(action);
				thread.SetApartmentState(ApartmentState.STA);
				thread.Start(state);
				return Task.Run(new Action(thread.Join));
			}
		} // proc DoClipboardActionAsync

		public static Task SetClipboardTextAsync(string text)
			=> DoClipboardActionAsync(WriteClipboardText, text);

		public static string GetClipboardText()
		{
			var sb = new StringBuilder();
			DoClipboardActionAsync(ReadClipboardText, sb).Wait();
			return sb.ToString();
		} // proc GetClipboardText

		[STAThread]
		private static void WriteClipboardText(object state)
			=> System.Windows.Forms.Clipboard.SetText((string)state);

		[STAThread]
		private static void ReadClipboardText(object state)
			=> ((StringBuilder)state).Append(System.Windows.Forms.Clipboard.GetText());

		private void InsertNewLine()
		{
			ClearLastInputCommand();

			var initalText = String.Empty;
			var currentLine = CurrentLine;

			if (currentLineOffset < currentLine.ContentLength)
			{
				var removeLength = currentLine.ContentLength - currentLineOffset;
				initalText = currentLine.content.ToString(currentLineOffset, removeLength);
				currentLine.content.Remove(currentLineOffset, removeLength);
				currentLine.ClearTokenCache();
			}

			var line = new InputLine(manager.GetPrompt());
			lines.Insert(++currentLineIndex, line);
			currentLineOffset = 0;

			if (initalText.Length > 0)
			{
				var idx = currentLineOffset;
				line.InsertLine(ref idx, initalText, 0, initalText.Length);
			}
		} // proc InsertNewLine

		private static bool IsNewCharGroup(int idx, string text, bool leftMove, ref int state)
		{
			int GetCharGroup(char c)
			{
				switch (c)
				{
					case '[':
					case ']':
						return 12;
					case '(':
					case ')':
						return 13;
					case '{':
					case '}':
						return 14;
					default:
						if (Char.IsLetterOrDigit(c))
							return 10;
						else if (Char.IsSymbol(c))
							return 11;
						else
							return 1;
				}
			}

			switch (state)
			{
				case 0: // set char group
					if (Char.IsWhiteSpace(text[idx]))
						state = leftMove ? 0 : 1;
					else
						state = GetCharGroup(text[idx]);
					return false;
				case 1: // skip spaces
					return !Char.IsWhiteSpace(text[idx]);
				default:
					if (!leftMove && Char.IsWhiteSpace(text[idx]))
					{
						state = 1;
						return false;
					}
					return state != GetCharGroup(text[idx]);
			}
		} // proc IsNewCharGroup

		private int GetNextTokenDefault(int offset, string text, bool leftMove)
		{
			var state = 0;
			if (leftMove)
			{
				if (offset <= 0)
					return -1;

				var idx = offset;
				while (idx > 0)
				{
					idx--;
					if (IsNewCharGroup(idx, text, leftMove, ref state))
					{
						idx++;
						break;
					}
				}
				return idx;
			}
			else
			{
				if (offset >= text.Length)
					return -1;

				var idx = offset;
				while (idx < text.Length)
				{
					if (IsNewCharGroup(idx, text, leftMove, ref state))
						break;
					idx++;
				}

				return idx;
			}
		} // func GetNextTokenDefault

		private void MoveCursorByToken(string content, bool leftMove)
		{
			var nextIndex = -1;
			if (manager is IConsoleReadLineScanner scan)
				nextIndex = scan.GetNextToken(currentLineOffset, content, leftMove);

			if (nextIndex < 0)
				nextIndex = GetNextTokenDefault(currentLineOffset, content, leftMove);

			if (nextIndex >= 0 && nextIndex <= content.Length)
				currentLineOffset = nextIndex;

			Invalidate();
		} // proc MoveCursorByToken

		private void MoveHistory(bool forward)
		{
			if (!(manager is IConsoleReadLineHistory history)
				|| history.Count == 0)
				return;

			if (currentHistoryIndex == -1)
			{
				if (forward)
					return;

				SaveLastInputCommand();
				currentHistoryIndex = history.Count - 1;

				Command = history[currentHistoryIndex];
			}
			else
			{
				if (forward)
				{
					if (currentHistoryIndex >= history.Count - 1)
						ResetLastInputCommand();
					else
						Command = history[++currentHistoryIndex];
				}
				else
				{
					if (currentHistoryIndex > 0)
						Command = history[--currentHistoryIndex];
				}
			}
		} // proc MoveHistory

		private InputLine CurrentLine => lines[currentLineIndex >= lines.Count ? lines.Count - 1 : currentLineIndex];

		#endregion

		public string Command
		{
			get
			{
				var cmd = new string[lines.Count];
				for (var i = 0; i < cmd.Length; i++)
					cmd[i] = lines[i].Content.ToString();
				return String.Join(Environment.NewLine, cmd);
			}
			set
			{
				lines.Clear();
				foreach (var (startAt, len) in Procs.SplitNewLinesTokens(value))
				{
					var line = new InputLine(manager.GetPrompt());
					line.content.Append(value, startAt, len);
					lines.Add(line);
				}
				if (lines.Count == 0)
					lines.Add(new InputLine(manager.GetPrompt()));

				currentLineIndex = lines.Count - 1;
				currentLineOffset = lines[currentLineIndex].ContentLength;

				Invalidate();
			}
		} // prop Command

		public static IConsoleReadLineManager CreatePrompt(string prompt)
			=> new SingleLineManager(prompt);
	} // class ConsoleReadLineOverlay

	#endregion

	#region -- class ConsoleReadSecureStringOverlay -----------------------------------

	public sealed class ConsoleReadSecureStringOverlay : ConsoleFocusableOverlay, IDisposable
	{
		private readonly string prompt;
		private readonly SecureString content = new SecureString();
		private readonly TaskCompletionSource<SecureString> commandAccepted;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public ConsoleReadSecureStringOverlay(string prompt, TaskCompletionSource<SecureString> commandAccepted = null)
		{
			this.prompt = prompt ?? String.Empty;
			this.commandAccepted = commandAccepted;
		} // ctor

		public void Dispose()
		{
			content.Dispose();
		} // proc Dispose

		#endregion

		#region -- OnResize, OnRender -------------------------------------------------

		protected override void OnRender()
		{
			var w = Application.BufferWidth;

			// build new buffer the can hold the text
			Resize(w, 1);

			var (endLeft, endTop) = Content.Write(0, 0, prompt);
			if (endTop == 0)
			{
				(endLeft, endTop) = Content.Write(endLeft, endTop, new string('*', content.Length));
				if (endTop == 0)
				{
					Content.Fill(endLeft, endTop, w - 1, 0, ' ');
					SetCursor(endLeft, endTop, 25);
				}
			}
		} // proc OnRender

		#endregion

		#region -- OnHandleEvent ------------------------------------------------------

		public override bool OnHandleEvent(EventArgs e)
		{
			// handle key down events
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.KeyChar)
				{
					case '\r':
						if (commandAccepted != null)
							commandAccepted.SetResult(GetSecureString());
						return true;
					case '\b':
						if (content.Length > 0)
						{
							content.RemoveAt(content.Length - 1);
							Invalidate();
						}
						return true;
					default:
						if (!Char.IsControl(keyDown.KeyChar))
						{
							content.AppendChar(keyDown.KeyChar);
							Invalidate();
						}
						else
						{
							switch (keyDown.Key)
							{
								case ConsoleKey.Delete:
									if (content.Length > 0)
									{
										content.Clear();
										Invalidate();
									}
									break;
							}
						}
						return true;
				}
			}
			else
				return base.OnHandleEvent(e);
		} // proc OnHandleEvent

		#endregion

		public SecureString GetSecureString()
		{
			var result = content.Copy();
			result.MakeReadOnly();
			return result;
		} // proc GetSecureString
	} // class ConsoleReadSecureStringOverlay

	#endregion

	#region -- class ReadLineOverlay --------------------------------------------------

	public class ReadLineOverlay : ConsoleFocusableOverlay
	{
		public event EventHandler TextChanged;

		private readonly StringBuilder text = new StringBuilder();
		private int offset = 0;
		private int insertAt = 0;

		public ReadLineOverlay()
		{
			Position = ConsoleOverlayPosition.Window;
		}

		protected override void OnRender()
		{
			var j = offset;
			for (var i = 0; i < Width; i++)
			{
				var c = j < text.Length ? text[j] : ' ';
				Content.Set(i, 0, c, ForegroundColor, BackgroundColor);
				j++;
			}
		} // proc OnRender

		private void InvalidateText()
		{
			TextChanged?.Invoke(this, EventArgs.Empty);
			Invalidate();
		} // proc InvalidateText

		private void InvalidateCursorIntern()
		{
			if (insertAt < offset)
			{
				offset = insertAt;
				Invalidate();
			}
			if (insertAt > offset + Width - 1)
			{
				offset = insertAt - Width + 1;
				Invalidate();
			}

			SetCursor(insertAt - offset, 0, 25);
		} // proc InvalidateCursorIntern

		public override bool OnHandleEvent(EventArgs e)
		{
			// handle key down events
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.KeyChar)
				{
					case '\b':
						if (insertAt > 0)
						{
							text.Remove(insertAt - 1, 1);
							insertAt--;
							InvalidateCursorIntern();
							InvalidateText();
						}
						return true;
					default:
						if (!Char.IsControl(keyDown.KeyChar))
						{
							text.Insert(insertAt++, keyDown.KeyChar);
							InvalidateCursorIntern();
							InvalidateText();
						}
						else
						{
							switch (keyDown.Key)
							{
								case ConsoleKey.LeftArrow:
									if (insertAt > 0)
									{
										insertAt--;
										InvalidateCursorIntern();
									}
									break;
								case ConsoleKey.RightArrow:
									if(insertAt < text.Length)
									{
										insertAt++;
										InvalidateCursorIntern();
									}
									break;
								case ConsoleKey.Home:
									insertAt = 0;
									InvalidateCursorIntern();
									break;
								case ConsoleKey.End:
									insertAt = text.Length;
									InvalidateCursorIntern();
									break;
								case ConsoleKey.Delete:
									if (insertAt < text.Length)
									{
										text.Remove(insertAt, 1);
										InvalidateText();
									}
									break;
							}
						}
						return true;
				}
			}
			else 
				return base.OnHandleEvent(e);
		} // func OnHandleEvent

		public string Text => text.ToString();
	} // class ReadLineOverlay

	#endregion
}
