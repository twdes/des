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

#define DBG_RESIZE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.Console
{
	#region -- enum ConsoleOverlayPosition --------------------------------------------

	public enum ConsoleOverlayPosition
	{
		Console,
		Cursor,
		Window,
		Buffer
	} // enum ConsoleOverlayPosition

	#endregion

	#region -- class ConsoleOverlay ---------------------------------------------------

	public class ConsoleOverlay
	{
		private ConsoleApplication application = null;
		private ConsoleOverlayPosition position = ConsoleOverlayPosition.Buffer;
		private int left = 0;
		private int top = 0;
		private CharBuffer content = null;
		private bool isInvalidate = false;
		private bool isVisible = true;
		private int zOrder = 0;

		public ConsoleOverlay()
		{
		} // ctor

		protected void Resize(int newWidth, int newHeight, bool clear = true)
		{
			if (newWidth == Width && newHeight == Height)
				return;

			if (newWidth <= 0 || newHeight <= 0)
				content = null; // clear buffer
			else
			{
				var newBuffer = new CharBuffer(newWidth, newHeight, ' ');
				if (content != null && !clear)
				{
					// todo: copy
				}
				content = newBuffer;
			}
		} // proc Resize

		public void Invalidate()
		{
			// mark to render
			isInvalidate = true;
			application?.Invalidate();
		} // proc Invalidate

		public void Render(int windowLeft, int windowTop, CharBuffer windowBuffer)
		{
			if (application == null)
				return;

			if (isInvalidate)
			{
				OnRender();
				isInvalidate = false;
			}

			if (Content != null)
				Content.CopyTo(ActualLeft - windowLeft, ActualTop - windowTop, windowBuffer);
		} // proc Render

		protected virtual void OnRender() { }

		public virtual void OnIdle() { }

		public virtual void OnResize() { }

		public virtual bool OnPreHandleKeyEvent(ConsoleKeyEventArgs e)
			=> false;

		public virtual bool OnHandleEvent(EventArgs e)
			=> false;

		protected virtual void OnVisibleChanged() { }

		public void WriteToConsole()
		{
			if (Content != null)
				Application.Write(Content);
		} // proc WriteToConsole

		private void RemoveOverlay()
		{
			if (IsVisible)
				Invalidate();
			application.RemoveOverlay(this);
		} // proc RemoveOverlay

		private void AddOverlay()
		{
			application.AddOverlay(this);
			if (IsVisible)
				Invalidate();
		} // proc AddOverlay

		public ConsoleApplication Application
		{
			get => application;
			set
			{
				if (application != value)
				{
					if (application != null)
						RemoveOverlay();

					application = value;
					if (application != null)
						AddOverlay();
				}
			}
		} // prop Application

		public ConsoleOverlayPosition Position
		{
			get => position;
			set
			{
				if (position != value)
				{
					position = value;
					Invalidate();
				}
			}
		} // proc Position

		public int Left
		{
			get => left;
			set
			{
				if (value != left)
				{
					left = value;
					Invalidate();
				}
			}
		} // prop Left

		public int Top
		{
			get => top;
			set
			{
				if (value != top)
				{
					top = value;
					Invalidate();
				}
			}
		} // prop Top

		public int ActualLeft
		{
			get
			{
				switch (position)
				{
					case ConsoleOverlayPosition.Cursor:
						return Application == null ? 0 : Application.CursorLeft + Left;
					case ConsoleOverlayPosition.Console:
						return 0;
					case ConsoleOverlayPosition.Window:
						if (Application == null)
							return 0;
						else if (Left < 0)
							return Application.WindowRight + Left + 1;
						else
							return Application.WindowLeft + Left;
					default:
						return Left;
				}
			}
		} // prop ActualLeft

		public int ActualTop
		{
			get
			{
				switch (position)
				{
					case ConsoleOverlayPosition.Cursor:
						return Application == null ? 0 : Application.CursorTop + Top;
					case ConsoleOverlayPosition.Console:
						return Application == null ? 0 : (Application.CursorLeft > 0 ? Application.CursorTop + 1 : Application.CursorTop);
					case ConsoleOverlayPosition.Window:
						if (Application == null)
							return 0;
						else if (Top < 0)
							return Application.WindowBottom + Top + 1;
						else
							return Application.WindowTop + Top;
					default:
						return Left;
				}
			}
		} // prop ActualLeft


		public int ActualRight => ActualLeft + Width - 1;
		public int ActualBottom => ActualRight + Height - 1;

		public int Width => content?.Width ?? 0;
		public int Height => content?.Height ?? 0;

		public int ZOrder
		{
			get => zOrder;
			set
			{
				if (value != zOrder)
				{
					zOrder = value;
					if (application != null)
					{
						RemoveOverlay();
						AddOverlay();
					}
				}
			}
		} // prop ZOrder

		public bool IsVisible
		{
			get => isVisible;
			set
			{
				if (isVisible != value)
				{
					isVisible = value;
					OnVisibleChanged();
					Invalidate();
				}
			}
		} // prop IsVisible

		public virtual bool IsRenderable => IsVisible;

		internal CharBuffer Content => content;
	} // class ConsoleOverlay

	#endregion

	#region -- class ConsoleFocusableOverlay ------------------------------------------

	public class ConsoleFocusableOverlay : ConsoleOverlay
	{
		private int cursorLeft = 0;
		private int cursorTop = 0;
		private int cursorVisible = 25;

		protected void SetCursor(int newLeft, int newTop, int newVisible)
		{
			this.cursorLeft = newLeft;
			this.cursorTop = newTop;
			this.cursorVisible = newVisible;
			Invalidate();
		} // pproc SetCursor

		public void Activate()
			=> Application.ActivateOverlay(this);

		public bool IsActive => Application.ActiveOverlay == this;

		public override bool IsRenderable => Position == ConsoleOverlayPosition.Console ? base.IsRenderable && IsActive : base.IsRenderable;

		public int CursorLeft => cursorLeft;
		public int CursorTop => cursorTop;
		public int CursorSize => cursorVisible;
	} // class ConsoleFocusableOverlay

	#endregion

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

	public interface IConsoleReadLineManager
	{
		bool CanExecute(string command);
		string GetPrompt();
	} // interface IConsoleReadLineManager

	public interface IConsoleReadLineScannerSource
	{
		void AppendToken(int lineStart, int columnStart, int lineEnd, int columnEnd, ConsoleColor color);

		TextReader TextReader { get; }

		string this[int lineIndex] { get; }
		int LineCount { get; }
	} // interface IConsoleReadLineScannerSource

	public interface IConsoleReadLineScanner
	{
		void Scan(IConsoleReadLineScannerSource source);
		int GetNextToken(int offset, string text, bool reverse);
	} // interface IConsoleReadLineScanner

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

			public bool Insert(ref int index, char c, bool overwrite)
			{
				if(index < content.Length)
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

			private bool ClearTokenCache()
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
					EmitCurrentColor(lastLineIndex, lines[lastLineIndex].Content.Length);
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
					var len = lines[currentLineIndex].Content.Length;
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

		#region -- Ctor/Dtor ----------------------------------------------------------

		public ConsoleReadLineOverlay(IConsoleReadLineManager manager, TaskCompletionSource<string> commandAccepted = null)
		{
			this.manager = manager ?? SingleLineManager.Default;
			this.commandAccepted = commandAccepted;

			this.lines.Add(new InputLine(this.manager.GetPrompt()));
		} // ctor

		#endregion

		#region -- OnResize, OnRender -------------------------------------------------

		public override void OnResize() 
			=> Invalidate();

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
					(endLeft, endTop) = Content.Write(endLeft, endTop, tok.Text, foreground: tok.Color);

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

		#region -- OnHandleEvent ------------------------------------------------------

		public override bool OnHandleEvent(EventArgs e)
		{
			// handle key down events
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.KeyChar)
				{
					case '\r':
						var command = Command;
						if (currentLineIndex >= lines.Count - 1 && manager.CanExecute(command))
						{
							if (commandAccepted != null)
								commandAccepted.SetResult(command);
						}
						else
						{
							lines.Insert(++currentLineIndex, new InputLine(manager.GetPrompt()));
							currentLineOffset = 0;
							Invalidate();
						}
						return true;
					case '\b':
						CurrentLine.FixLineEnd(ref currentLineOffset);
						if (currentLineOffset > 0)
						{
							currentLineOffset--;
							if (CurrentLine.Remove(currentLineOffset))
								Invalidate();
						}
						return true;
					case '\t':
						{
							for (var i = 0; i < 4; i++)
							{
								if (CurrentLine.Insert(ref currentLineOffset, ' ', overwrite))
								{
									currentLineOffset++;
									Invalidate();
								}
							}
						}
						return true;
					default:
						if (keyDown.KeyChar != '\0')
						{
							if (CurrentLine.Insert(ref currentLineOffset, keyDown.KeyChar, overwrite))
							{
								currentLineOffset++;
								Invalidate();
							}
						}
						else
						{
							switch (keyDown.Key)
							{
								case ConsoleKey.Delete:
									if (CurrentLine.Remove(currentLineOffset))
										Invalidate();
									break;

								case ConsoleKey.UpArrow:
									if (currentLineIndex > 0)
									{
										currentLineIndex--;
										Invalidate();
									}
									break;
								case ConsoleKey.DownArrow:
									if (currentLineIndex < lines.Count - 1)
									{
										currentLineIndex++;
										Invalidate();
									}
									break;
								case ConsoleKey.LeftArrow:
									if (currentLineOffset > 0)
										currentLineOffset--;
									else
										currentLineOffset = 0;
									Invalidate();
									break;
								case ConsoleKey.RightArrow:
									if (currentLineOffset < CurrentLine.Content.Length)
										currentLineOffset++;
									else
										currentLineOffset = CurrentLine.Content.Length;
									Invalidate();
									break;
							}
						}
						return true;
				}
			}
			else if (e is ConsoleKeyUpEventArgs keyUp)
			{
				switch (keyUp.Key)
				{
					case ConsoleKey.End:
						if ((keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
							currentLineIndex = lines.Count - 1;
						currentLineOffset = CurrentLine.Content.Length;
						Invalidate();
						break;
					case ConsoleKey.Home:
						if ((keyUp.KeyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
							currentLineIndex = 0;
						currentLineOffset = 0;
						Invalidate();
						break;
					case ConsoleKey.Insert:
						overwrite = !overwrite;
						Invalidate();
						break;
				}
				return true;
			}
			else
				return base.OnHandleEvent(e);
		} // proc OnHandleEvent

		private InputLine CurrentLine => lines[currentLineIndex];

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

		public override void OnResize()
			=> base.OnResize();

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
						if(content.Length > 0)
						{
							content.RemoveAt(content.Length - 1);
							Invalidate();
						}
						return true;
					default:
						if (keyDown.KeyChar != '\0')
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

	#region -- class ConsoleApplication -----------------------------------------------

	public sealed class ConsoleApplication
	{
		#region -- class ConsoleSynchronizationContext --------------------------------

		private sealed class ConsoleSynchronizationContext : SynchronizationContext
		{
			private ConsoleApplication application;

			public ConsoleSynchronizationContext(ConsoleApplication application)
			{
				this.application = application;
			} // ctor

			public override void Post(SendOrPostCallback d, object state)
				=> application.BeginInvoke(new Action(() => d(state)));

			public override void Send(SendOrPostCallback d, object state)
				=> application.Invoke(new Action(() => d(state)));
		} // class ConsoleSynchronizationContext

		#endregion

		#region -- class ConsoleOutputWriter ------------------------------------------

		private sealed class ConsoleOutputWriter : TextWriter
		{
			private readonly ConsoleApplication application;

			public ConsoleOutputWriter(ConsoleApplication application)
			{
				this.application = application ?? throw new ArgumentNullException(nameof(output));
			} // ctor

			public override void Write(char value)
				=> application.Write(value.ToString());

			public override void Write(string value)
				=> application.Write(value);

			public override void Write(char[] buffer, int index, int count)
				=> application.Write(new string(buffer, index, count));

			public override Encoding Encoding => Encoding.Unicode;
		} // class ConsoleOutputWriter

		#endregion

		public event EventHandler<ConsoleKeyUpEventArgs> ConsoleKeyUp;
		public event EventHandler<ConsoleKeyDownEventArgs> ConsoleKeyDown;
		public event EventHandler<ConsoleMenuEventArgs> ConsoleMenu;
		public event EventHandler<ConsoleBufferSizeEventArgs> ConsoleBufferSize;
		public event EventHandler<ConsoleSetFocusEventArgs> ConsoleFocus;

		private readonly ConsoleOutputBuffer output; // active output buffer for the console
		private readonly ConsoleOutputBuffer activeOutput; // active output buffer, for the window
		private readonly ConsoleInputBuffer input; // active input buffer

		private readonly Stack<Action> eventQueue = new Stack<Action>(); // other events that join in the main thread
		private readonly ManualResetEventSlim eventQueueFilled = new ManualResetEventSlim(false); // marks that events in queue
		private readonly List<ConsoleOverlay> overlays = new List<ConsoleOverlay>(); // first overlay is the active overlay
		private readonly List<ConsoleFocusableOverlay> activeOverlays = new List<ConsoleFocusableOverlay>(); // list of active controls, top most gets keyboard input

		private SmallRect lastWindow;

		private readonly int currentThreadId;

		#region -- Ctor/Dtor ----------------------------------------------------------

		private ConsoleApplication(ConsoleOutputBuffer output = null, ConsoleInputBuffer input = null)
		{
			this.activeOutput = output ?? ConsoleOutputBuffer.GetActiveBuffer();
			this.activeOutput.ConsoleMode = activeOutput.ConsoleMode | 0x0008; //  ENABLE_WINDOW_INPUT
			this.output = activeOutput.Copy(); // create buffer the console content

			lastWindow = activeOutput.GetWindow();

			this.input = input ?? ConsoleInputBuffer.GetStandardInput();

			this.currentThreadId = Thread.CurrentThread.ManagedThreadId;

			SynchronizationContext.SetSynchronizationContext(new ConsoleSynchronizationContext(this));
		} // ctor

		#endregion

		#region -- Thread Handling ----------------------------------------------------

		#region -- class InvokeAction -------------------------------------------------
		
		private sealed class InvokeAction
		{
			private readonly ManualResetEventSlim wait;
			private readonly Action action;

			public InvokeAction(Action action)
			{
				this.action = action;
				this.wait = new ManualResetEventSlim(false);
			} // ctor

			public void Execute()
			{
				try
				{
					action();
				}
				finally
				{
					wait.Set();
				}
			} // proc Execute

			public ManualResetEventSlim WaitHandle => wait;
		} // class InvokeAction

		#endregion

		#region -- class InvokeFunc ---------------------------------------------------

		private sealed class InvokeFunc<T>
		{
			private readonly ManualResetEventSlim wait;
			private readonly Func<T> func;
			private T result;

			public InvokeFunc(Func<T> func)
			{
				this.func = func;
				this.wait = new ManualResetEventSlim(false);
			} // ctor

			public void Execute()
			{
				try
				{
					result = func();
				}
				finally
				{
					wait.Set();
				}
			} // proc Execute

			public T Result => result;
			public ManualResetEventSlim WaitHandle => wait;
		} // class InvokeFunc

		#endregion

		#region -- class InvokeFuncAsync ----------------------------------------------

		private sealed class InvokeFuncAsync<T>
		{
			private readonly Func<T> func;
			private readonly TaskCompletionSource<T> result;

			public InvokeFuncAsync(Func<T> func)
			{
				this.func = func;
				this.result = new TaskCompletionSource<T>();
			} // ctor

			public void Execute()
			{
				try
				{
					result.SetResult(func());
				}
				catch (TaskCanceledException)
				{
					result.SetCanceled();
				}
				catch (Exception e)
				{
					result.SetException(e);
				}
			} // proc Execute

			public Task<T> Result => result.Task;
		} // class InvokeFuncAsync

		#endregion

		public void CheckThreadSynchronization()
		{
			if (IsInvokeRequired)
				throw new InvalidOperationException("This is not the console main thread.");
		} // proc CheckThreadSynchronization

		public bool IsInvokeRequired => currentThreadId != Thread.CurrentThread.ManagedThreadId;

		public void BeginInvoke(Action action)
		{
			lock (eventQueue)
			{
				eventQueue.Push(action);
				eventQueueFilled.Set();
			}
		} // proc Enqueue

		public void Invoke(Action action)
		{
			if (IsInvokeRequired)
			{
				var a = new InvokeAction(action);
				BeginInvoke(a.Execute);
				a.WaitHandle.Wait();
			}
			else
				action();
		} // proc Invoke

		public T Invoke<T>(Func<T> func)
		{
			if (IsInvokeRequired)
			{
				var f = new InvokeFunc<T>(func);
				BeginInvoke(f.Execute);
				f.WaitHandle.Wait();
				return f.Result;
			}
			else
				return func();
		} // proc Invoke

		public Task<T> InvokeAsync<T>(Func<T> func)
		{
			var f = new InvokeFuncAsync<T>(func);
			BeginInvoke(f.Execute);
			return f.Result;
		} // proc Enqueue

		private bool TryDequeue(out Action action)
		{
			lock (eventQueue)
			{
				if (eventQueue.Count > 0)
				{
					action = eventQueue.Pop();
					if (eventQueue.Count == 0)
						eventQueueFilled.Reset();
					return true;
				}
				else
				{
					eventQueueFilled.Reset();
					action = null;
					return false;
				}
			}
		} // func TryDequeue

		#endregion

		#region -- Overlay ------------------------------------------------------------

		internal void AddOverlay(ConsoleOverlay overlay)
		{
			CheckThreadSynchronization();

			if (overlay.Application != this)
				throw new ArgumentException();

			var insertAt = 0;
			var insertZ = overlay.ZOrder;
			while (insertAt < overlays.Count && insertZ > overlays[insertAt].ZOrder)
				insertAt++;
			
			overlays.Insert(insertAt, overlay);
			if (overlay is ConsoleFocusableOverlay fo)
				AddActiveOverlay(fo);
		} // proc AddOverlay

		private void AddActiveOverlay(ConsoleFocusableOverlay overlay)
		{
			if (activeOverlays.Count == 0)
				activeOverlays.Add(overlay); // focus
			else // not auto focus
				activeOverlays.Insert(activeOverlays.Count - 1, overlay);
		} // proc AddActiveOverlay

		internal void RemoveOverlay(ConsoleOverlay overlay)
		{
			CheckThreadSynchronization();

			if (overlay.Application != this)
				throw new ArgumentException();

			if (overlay is ConsoleFocusableOverlay fo)
				RemoveActiveOverlay(fo);
			overlays.Remove(overlay);
		} // proc RemoveOverlay

		private void RemoveActiveOverlay(ConsoleFocusableOverlay overlay)
		{
			CheckThreadSynchronization();

			if (!activeOverlays.Remove(overlay))
				return;

			for (var i = activeOverlays.Count - 1; i >= 0; i--)
			{
				if (activeOverlays[i].IsVisible)
				{
					ActivateOverlay(activeOverlays[i]);
					break;
				}
			}
		} // proc RemoveActiveOverlay

		internal void ActivateOverlay(ConsoleFocusableOverlay overlay)
		{
			if (!overlay.IsVisible)
				throw new InvalidOperationException();

			// move overlay to top
			var currentIndex = activeOverlays.IndexOf(overlay);
			if (currentIndex < activeOverlays.Count - 1)
			{
				activeOverlays.RemoveAt(currentIndex);
				activeOverlays.Add(overlay);
			}

			Invalidate();
		} // proc ActiveOverlay

		#endregion

		#region -- Write --------------------------------------------------------------

		#region -- class ResetColor ---------------------------------------------------

		private sealed class ResetColor : IDisposable
		{
			private readonly ConsoleOutputBuffer output;
			private readonly ConsoleColor currentForegroundColor;
			private readonly ConsoleColor currentBackgroundColor;

			public ResetColor(ConsoleOutputBuffer output)
			{
				this.output = output ?? throw new ArgumentNullException(nameof(output));
				currentForegroundColor = output.CurrentForeground;
				currentBackgroundColor = output.CurrentBackground;
			} // ctor

			public void Dispose()
			{
				output.CurrentForeground= currentForegroundColor;
				output.CurrentBackground = currentBackgroundColor;
			} // proc Dispose
		} // class ResetColor

		#endregion

		public IDisposable Color(ConsoleColor foregroundColor = ConsoleColor.Gray, ConsoleColor backgroundColor = ConsoleColor.Black)
		{
			CheckThreadSynchronization();

			var resetColor = new ResetColor(output);
			output.CurrentBackground = backgroundColor;
			output.CurrentForeground = foregroundColor;
			return resetColor;
		} // func Color

		public void Write(string text)
		{
			CheckThreadSynchronization();

			output.Write(text);

			Invalidate();
		} // proc Write

		public void Write(CharBuffer content)
		{
			output.WriteBuffer(output.CursorLeft, output.CursorTop, content);
			output.SetCursorPosition(0, output.CursorTop + content.Height);
		} // proc Write

		public void WriteLine(string text)
		{
			CheckThreadSynchronization();

			output.Write(text);
			output.Write(Environment.NewLine);
			
			Invalidate();
		} // proc WriteLine

		public void WriteLine()
		{
			CheckThreadSynchronization();

			output.Write(Environment.NewLine);

			Invalidate();
		} // proc WriteLine

		public Task<string> ReadLineAsync(IConsoleReadLineManager manager = null)
		{
			CheckThreadSynchronization();

			var taskComplete = new TaskCompletionSource<string>();
			var r = new ConsoleReadLineOverlay(manager, taskComplete)
			{
				Application = this,
				Position = ConsoleOverlayPosition.Console
			};
			r.Activate();

			return taskComplete.Task.ContinueWith(
				t => { r.WriteToConsole(); r.Application = null; return t.Result; }, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously
			);
		} // func ReadLineAsync

		public Task<SecureString> ReadSecureStringAsync(string prompt)
		{
			CheckThreadSynchronization();

			var taskComplete = new TaskCompletionSource<SecureString>();
			var r = new ConsoleReadSecureStringOverlay(prompt, taskComplete)
			{
				Application = this,
				Position = ConsoleOverlayPosition.Console
			};
			r.Activate();

			return taskComplete.Task.ContinueWith(
				t => { r.WriteToConsole(); r.Application = null; return t.Result; }, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously
			);
		} // func ReadSecureStringAsync

		public int CursorLeft { get => output.CursorLeft; set => output.CursorLeft = value; }
		public int CursorTop { get => output.CursorTop; set => output.CursorTop = value; }

		public int WindowLeft => lastWindow.Left;
		public int WindowTop => lastWindow.Top;
		public int WindowRight => lastWindow.Right;
		public int WindowBottom => lastWindow.Bottom;

		public int BufferWidth => output.Width;
		public int BufferHeight => output.Height;

		#endregion

		#region -- OnRender -----------------------------------------------------------

		private bool isInvalidate = false;

		internal void Invalidate()
		{
			isInvalidate = true;
			eventQueueFilled.Set();
		} // proc Invalidate

		private void OnRender()
		{
			try
			{
				SmallRect window;
				SmallRect afterWindow;
				do
				{
					window = activeOutput.GetWindow();

					// read background console
					var windowBuffer = output.ReadBuffer(window.Left, window.Top, window.Right, window.Bottom);

					// render overlay
					foreach (var o in overlays)
					{
						if (o.IsRenderable)
							o.Render(window.Left, window.Top, windowBuffer);
					}

					// write output
					activeOutput.WriteBuffer(window.Left, window.Top, windowBuffer);
					var activeOverlay = ActiveOverlay;
					if (activeOverlay != null)
					{
						OnRenderCursor(
							activeOverlay.ActualLeft + activeOverlay.CursorLeft, 
							activeOverlay.ActualTop + activeOverlay.CursorTop,
							activeOverlay.CursorSize, 
							activeOverlay.CursorSize > 0,
							out afterWindow
						);
					}
					else
						OnRenderCursor(output.CursorLeft, output.CursorTop, output.CursorSize, output.CursorVisible, out afterWindow);
				}
				while (afterWindow.Left != window.Left || afterWindow.Top != window.Top);
			}
			finally
			{
				isInvalidate = false;
			}
		} // proc OnRender

		private void OnRenderCursor(int left, int top, int cursorSize, bool cursorVisible, out SmallRect window)
		{
			activeOutput.SetCursorPosition(left, top);
			activeOutput.SetCursor(cursorSize, cursorVisible);

			// get new window
			window = activeOutput.GetWindow();
			//var topRow = window.Top - top - ReservedTopRowCount;
			//if (topRow > 0)
			//	activeOutput.SetWindow(left, top - topRow);
			var bottomRow = top  -window.Bottom + ReservedBottomRowCount;
			if (bottomRow > 0)
			{
				activeOutput.ResizeWindow(0, bottomRow, 0, bottomRow);
				window = activeOutput.GetWindow();
			}
		} // proc OnRenderCursor

		#endregion

		#region -- OnHandleEvent ------------------------------------------------------

		public void OnHandleEvent(EventArgs ev)
		{
			CheckThreadSynchronization();
			OnHandleEventUnsafe(ev);
		} // proc OnHandleEvent

		private void OnHandleEventUnsafe(EventArgs ev)
		{
			// refresh buffer
			if (ev is ConsoleBufferSizeEventArgs)
			{
				if (output.Width != activeOutput.Width
					|| output.Height != activeOutput.Height)
				{
					// copy buffer info
					var activeBufferInfo = new ConsoleScreenBufferInfoEx() { cbSize = ConsoleScreenBufferInfoEx.Size };
					var outputBufferInfo = new ConsoleScreenBufferInfoEx() { cbSize = ConsoleScreenBufferInfoEx.Size };
					UnsafeNativeMethods.GetConsoleScreenBufferInfoEx(activeOutput.Handle.DangerousGetHandle(), ref activeBufferInfo);
					UnsafeNativeMethods.GetConsoleScreenBufferInfoEx(output.Handle.DangerousGetHandle(), ref outputBufferInfo);

					outputBufferInfo.dwMaximumWindowSize = activeBufferInfo.dwMaximumWindowSize;
					outputBufferInfo.dwSize = activeBufferInfo.dwSize;
					outputBufferInfo.srWindow = activeBufferInfo.srWindow;

					UnsafeNativeMethods.SetConsoleScreenBufferInfoEx(output.Handle.DangerousGetHandle(), ref outputBufferInfo);

					// notify size change
					foreach (var o in overlays)
						o.OnResize();
					Invalidate();
				}
			}

			// process events
			bool isKeyEvent;
			if (ev is ConsoleKeyEventArgs keyEv)
			{
				foreach (var o in overlays)
				{
					if (o.OnPreHandleKeyEvent(keyEv))
						return;
				}
				isKeyEvent = true;
			}
			else
				isKeyEvent = false;

			if (ActiveOverlay != null && ActiveOverlay.OnHandleEvent(ev))
			{
				if (isKeyEvent)
					return;
			}

			if (!isKeyEvent)
			{
				foreach (var o in overlays)
				{
					if (o == ActiveOverlay)
						continue;

					if(o.OnHandleEvent(ev))
					{
						if (isKeyEvent)
							return;
					}
				}
			}
			
			// call events handler
			
			switch (ev)
			{
				case ConsoleKeyDownEventArgs keyDown:
					ConsoleKeyDown?.Invoke(this, keyDown);
					break;
				case ConsoleKeyUpEventArgs keyUp:
					ConsoleKeyUp?.Invoke(this, keyUp);
					break;
				case ConsoleMenuEventArgs menu:
					ConsoleMenu?.Invoke(this, menu);
					break;
				case ConsoleBufferSizeEventArgs windowSize:
					ConsoleBufferSize?.Invoke(this, windowSize);
					break;
				case ConsoleSetFocusEventArgs focus:
					ConsoleFocus?.Invoke(this, focus);
					break;
			}
		} // proc OnHandleEventUnsafe

		private void CheckConsoleWindowPosition()
		{
			var currentWindow = activeOutput.GetWindow();
			if (currentWindow.Left != lastWindow.Left
				|| currentWindow.Top != lastWindow.Top
				|| currentWindow.Right != lastWindow.Right
				|| currentWindow.Bottom != lastWindow.Bottom)
			{
#if DBG_RESIZE
				Debug.Print("New WindowSize Detected: {0}", currentWindow.ToString());
#endif
				lastWindow = currentWindow;

				foreach (var o in overlays)
					o.OnResize();

				Invalidate();
			}
		} // proc CheckConsoleWindowPosition

		#endregion

		#region -- OnIdle, DoEvents ---------------------------------------------------

		/// <summary></summary>
		/// <returns></returns>
		public void OnIdle()
		{
			CheckThreadSynchronization();
			OnIdleUnsafe();
		} // proc OnIdle

		private void OnIdleUnsafe()
		{
			foreach (var o in overlays)
				o.OnIdle();
		} // proc OnIdleUnsafe

		public void DoEvents()
		{
			CheckThreadSynchronization();
			DoEventsUnsafe(true);
		} // proc DoEvents

		private void DoEventsUnsafe(bool runIdle)
		{
			// check for window event
			CheckConsoleWindowPosition();

			// run events
			foreach (var ev in input.ReadInputEvents(-1))
				OnHandleEventUnsafe(ev);

			// run actions
			while (TryDequeue(out var action))
				action();

			// run idle event
			if (runIdle)
				OnIdleUnsafe();

			// render screen
			if (isInvalidate)
				OnRender();
		} // proc DoEventsUnsafe

		#endregion

		#region -- Run ----------------------------------------------------------------

		public int Run(Func<Task<int>> main)
		{
			CheckThreadSynchronization();

			//if (!UnsafeNativeMethods.SetConsoleMode(hInput, 0x0010 | 0x0008 | 0x0080))
			//	throw new Win32Exception();
			var continueLoop = true;

			// run code
			var t = main();
			t.GetAwaiter().OnCompleted(
				() =>
				{
					continueLoop = false;
					eventQueueFilled.Set();
				}
			);

			// run event loop
			var testEvent = 0;
			var waitHandles = new WaitHandle[] { input.WaitHandle, eventQueueFilled.WaitHandle };
			while (continueLoop)
			{
				DoEventsUnsafe(testEvent != WaitHandle.WaitTimeout);

				if (continueLoop || isInvalidate)
					testEvent = WaitHandle.WaitAny(waitHandles, 400);
			}

			return t.Result;
		} // proc Run

		#endregion

		public ConsoleFocusableOverlay ActiveOverlay
		{
			get
			{
				if (activeOverlays.Count == 0)
					return null;
				var topOverlay = activeOverlays[activeOverlays.Count - 1];
				return topOverlay.IsVisible ? topOverlay : null;
			}
		} // prop ActiveOverlay

		//public int ReservedTopRowCount { get; set; } = 0;
		public int ReservedBottomRowCount { get; set; } = 0;

		static ConsoleApplication()
		{
			Current = new ConsoleApplication();
			//System.Console.SetOut(new ConsoleOutputWriter(Current));
		} // sctor

		/// <summary>Currently aktive console</summary>
		public static ConsoleApplication Current { get; }
	} // class ConsoleApplication

	#endregion
}
