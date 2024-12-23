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
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Server;
using TecWare.DE.Server.UI;

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
		public const char VerticalDoubleLine = (char)0x2551;
		public const char HorizontalDoubleLine = (char)0x2550;
		public const char HorizontalThinLine = (char)0x2500;

		public const char VerticalDoubleToHorizontalThinLineLeft = (char)0x255F;
		public const char VerticalDoubleToHorizontalThinLineRight = (char)0x2562;

		public const char TopLeftDoubleLine = (char)0x2554;
		public const char TopRightDoubleLine = (char)0x2557;
		public const char BottomLeftDoubleLine = (char)0x255A;
		public const char BottomRightDoubleLine = (char)0x255D;

		public const char Block = (char)0x2588;
		public const char Shadow = (char)0x2591;

		private ConsoleApplication application = null;
		private ConsoleOverlayPosition position = ConsoleOverlayPosition.Buffer;
		private int left = 0;
		private int top = 0;
		private CharBuffer content = null;
		private bool isBufferInvalidate = false;
		private bool isSizeInvalidate = true;
		private bool isVisible = true;
		private int zOrder = 0;

		public ConsoleOverlay()
		{
		} // ctor

		public bool Locate(int newLeft, int newTop)
		{
			var c = false;
			if (newLeft != left)
			{
				Left = newLeft;
				c = true;
			}
			if (newTop != top)
			{
				Top = newTop;
				c = true;
			}
			return c;
		} // func Locate

		public bool Resize(int newWidth, int newHeight, bool clear = true)
		{
			if (newWidth == Width && newHeight == Height)
				return false;

			if (newWidth <= 0 || newHeight <= 0)
				content = null; // clear buffer
			else
			{
				var newBuffer = new CharBuffer(newWidth, newHeight, ' ');
				if (content != null && !clear)
				{
					// todo: copy
				}
				else
					Invalidate();
				content = newBuffer;
			}
			return true;
		} // proc Resize

		public void Invalidate()
		{
			// mark to render
			isBufferInvalidate = true;
			application?.Invalidate();
		} // proc Invalidate

		public void InvalidateSize()
		{
			isSizeInvalidate = true;
			if (IsVisible)
				application?.Invalidate();
		} // proc InvalidateSize

		public void Render(int windowLeft, int windowTop, CharBuffer windowBuffer)
		{
			if (application == null)
				return;

			if (isSizeInvalidate)
			{
				OnParentResize();
				isSizeInvalidate = false;
			}

			if (isBufferInvalidate)
			{
				try
				{
					OnRender();
				}
				catch (Exception e)
				{
					// fill content with error message
					var (endLeft, endTop) = Content.Write(0, 0, e.ToString(), 0, ConsoleColor.White, ConsoleColor.Red);
					while (endTop < Content.Height)
					{
						while (endLeft < Content.Width)
						{
							Content.Set(endLeft, endTop, ' ', ConsoleColor.White, ConsoleColor.Red);
							endLeft++;
						}

						endTop++;
						endLeft = 0;
					}
				}
				isBufferInvalidate = false;
			}

			Content?.CopyTo(ActualLeft - windowLeft, ActualTop - windowTop, windowBuffer);
		} // proc Render

		protected virtual void OnRender() { }

		public virtual void OnIdle() { }

		protected virtual void OnParentResize() { }

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

		private void AddOverlay()
		{
			application.AddOverlay(this);
			OnAdded();
			InvalidateSize();
			if (IsVisible)
				Invalidate();
		} // proc AddOverlay

		private void RemoveOverlay()
		{
			if (IsVisible)
				Invalidate();
			application.RemoveOverlay(this);
			OnRemoved();
		} // proc RemoveOverlay

		protected virtual void OnAdded() { }

		protected virtual void OnRemoved() { }

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

		public ConsoleColor ForegroundColor { get; set; } = ConsoleColor.White;
		public ConsoleColor BackgroundColor { get; set; } = ConsoleColor.DarkCyan;

		internal CharBuffer Content => content;
	} // class ConsoleOverlay

	#endregion

	#region -- class ConsoleFocusableOverlay ------------------------------------------

	public class ConsoleFocusableOverlay : ConsoleOverlay
	{
		public event EventHandler CursorChanged;

		private bool cursorIsInvalidate = false;
		private int cursorLeft = 0;
		private int cursorTop = 0;
		private int cursorVisible = 25;

		protected void SetCursor(int newLeft, int newTop, int newVisible)
		{
			if (cursorLeft != newLeft)
			{
				cursorLeft = newLeft;
				InvalidateCursor();
			}
			if (cursorTop != newTop)
			{
				cursorTop = newTop;
				InvalidateCursor();
			}
			if (cursorVisible != newVisible)
			{
				cursorVisible = newVisible;
				InvalidateCursor();
			}

			Invalidate();
		} // pproc SetCursor

		protected override void OnParentResize()
		{
			base.OnParentResize();
		} // proc OnParentResize

		/// <summary>Reset the cursor, and make the cursor visible.</summary>
		public void InvalidateCursor()
		{
			cursorIsInvalidate = true;
			CursorChanged?.Invoke(this, EventArgs.Empty);
		} // proc InvalidateCursor

		internal bool ResetInvalidateCursor()
		{
			if (cursorIsInvalidate)
			{
				cursorIsInvalidate = false;
				return true;
			}
			else
				return false;
		} // func ResetInvalidateCursor

		public void Activate()
			=> Application.ActivateOverlay(this);

		#region -- Scrolling ----------------------------------------------------------

		private static bool CalculateScroll(int top, int scrollHeight, int visibleSize, int virtualOffset, int virtualSize, out int startAt, out int endAt)
		{
			if (scrollHeight <= 0)
			{
				startAt = 0;
				endAt = 0;
				return false;
			}

			startAt = virtualSize > 0 ? (virtualOffset * scrollHeight / virtualSize) : -1;
			endAt = startAt >= 0 ? (startAt + visibleSize * scrollHeight / virtualSize) : -1;

			// move parameter to render position
			startAt += top;
			endAt += top;

			return true;
		} // proc CalculateScroll

		protected void RenderVerticalScroll(int left, int top, int bottom, int visibleSize, int virtualOffset, int virtualSize)
		{
			if (!CalculateScroll(top, bottom - top + 1, visibleSize, virtualOffset, virtualSize, out var startAt, out var endAt))
				return;

			for (var i = top; i <= bottom; i++)
			{
				var selected = i >= startAt && i <= endAt;
				Content.Set(left, i, selected ? Block : Shadow, ForegroundColor, BackgroundColor);
			}
		} // proc RenderVerticalScroll

		protected void RenderHorizontalScroll(int left, int right, int top, int visibleSize, int virtualOffset, int virtualSize)
		{
			if (!CalculateScroll(left, right - left + 1, visibleSize, virtualOffset, virtualSize, out var startAt, out var endAt))
				return;

			for (var i = left; i <= right; i++)
			{
				var selected = i >= startAt && i <= endAt;
				Content.Set(i, top, selected ? Block : Shadow, ForegroundColor, BackgroundColor);
			}
		} // proc RenderHorizontalScroll

		protected bool ValidateScrollOffset(ref int offset, int newOffset, int screenSize, int virtualSize)
		{
			if (newOffset < 0)
				newOffset = 0;
			else if (newOffset > virtualSize - screenSize)
			{
				if (virtualSize <= screenSize)
					newOffset = 0;
				else
					newOffset = virtualSize - screenSize;
			}

			if (newOffset != offset)
			{
				offset = newOffset;
				return true;
			}
			else
				return false;
		} // proc ValidateScrollOffset

		#endregion

		public bool IsActive => Application.ActiveOverlay == this;

		public override bool IsRenderable => Position == ConsoleOverlayPosition.Console ? base.IsRenderable && IsActive : base.IsRenderable;

		public int CursorLeft => cursorLeft;
		public int CursorTop => cursorTop;
		public int CursorSize => cursorVisible;
	} // class ConsoleFocusableOverlay

	#endregion

	#region -- class ConsoleDialogOverlay ---------------------------------------------

	public abstract class ConsoleDialogOverlay : ConsoleFocusableOverlay
	{
		#region -- class KeyCommand ---------------------------------------------------

		private sealed class KeyCommand
		{
			private readonly ConsoleKey key;
			private readonly string name;
			private readonly Func<Task<bool?>> execute;
			private readonly Func<bool> canExecute;

			public KeyCommand(ConsoleKey key, string name, Func<Task<bool?>> execute, Func<bool> canExecute)
			{
				this.key = key;
				this.name = name;
				this.execute = execute;
				this.canExecute = canExecute;
			} // ctor

			public void Execute(ConsoleDialogOverlay dialog)
			{
				if (execute == null)
					return;

				var app = dialog.Application;
				dialog.Application = null; // remove current dialog
				execute().ContinueWith(
					t =>
					{
						try
						{
							if (t.Result.HasValue)
							{
								if (t.Result.Value)
									dialog.OnAccept();
								else
									dialog.OnCancel();
							}
							else
							{
								dialog.Application = app;
								dialog.Activate();
							}
						}
						catch (Exception e)
						{
							app.WriteError(e);
						}
					},
					TaskContinuationOptions.ExecuteSynchronously
				);
			} // proc Execute

			public ConsoleKey Key => key;
			public string Name => name;

			public bool IsVisible => Key >= ConsoleKey.F1 && Key <= ConsoleKey.F10 && !String.IsNullOrEmpty(name);
			public bool CanExecute => canExecute == null ? true : canExecute.Invoke();
		} // class KeyCommand

		#endregion

		private readonly TaskCompletionSource<bool> dialogResult;

		private readonly Dictionary<ConsoleKey, KeyCommand> commands = new Dictionary<ConsoleKey, KeyCommand>();
		private readonly KeyCommand[] visibleCommands = new KeyCommand[10];
		private readonly List<ConsoleOverlay> children = new List<ConsoleOverlay>();

		#region -- Ctor/Dtor ----------------------------------------------------------

		protected ConsoleDialogOverlay()
		{
			dialogResult = new TaskCompletionSource<bool>();
			SetCursor(0, 0, 0);
		} // ctor

		public override void OnIdle()
		{
			base.OnIdle();
			RefreshCommands();
		} // proc OnIdle

		protected virtual void GetResizeConstraints(out int maxWidth, out int maxHeight, out bool includeReservedRows)
		{
			maxWidth = Int32.MaxValue;
			maxHeight = Int32.MaxValue;
			includeReservedRows = false;
		} // proc GetResizeConstraints

		protected sealed override void OnParentResize()
		{
			base.OnParentResize();

			var app = Application;
			GetResizeConstraints(out var maxWidth, out var maxHeight, out var includeReservedRows);

			var windowWidth = app.WindowRight - app.WindowLeft + 1;
			var windowHeight = app.WindowBottom - app.WindowTop - (includeReservedRows ? app.ReservedBottomRowCount : 0) + 1;

			var width = Math.Min(maxWidth, windowWidth - 4);
			var height = Math.Min(maxHeight, windowHeight - 2);

			Position = ConsoleOverlayPosition.Window;
			if (Resize(width, height)
				| Locate((windowWidth - width) / 2, (windowHeight - height) / 2))
				OnResizeContent();
		} // proc OnParentResize

		protected virtual void OnResizeContent()
		{
			InvalidateCursor();
		} // proc OnResizeContent

		#endregion

		#region -- Render -------------------------------------------------------------

		private (int firstIdx, int lastIdx) RenderTitle(string title, int width)
		{
			if (String.IsNullOrEmpty(title))
				goto failed;

			var left = width / 2 - title.Length / 2;
			var maxWidth = width - 8;
			if (left < 4)
				left = 4;
			if (left < width || maxWidth < 0)
			{
				if (title.Length > maxWidth)
				{
					if (maxWidth > 4)
						title = "..." + title.Substring(title.Length - maxWidth + 3);
					else
						title = title.Substring(0, maxWidth);
				}

				var lastIdx = left + title.Length;
				var firstIdx = left - 1;
				Content.Set(firstIdx, 0, ' ', ForegroundColor, BackgroundColor);
				Content.Write(left, 0, title, null, ForegroundColor, BackgroundColor);
				Content.Set(lastIdx, 0, ' ', ForegroundColor, BackgroundColor);

				return (firstIdx, lastIdx);
			}
			failed:
			return (Int32.MaxValue, Int32.MinValue);
		} // func RenderTitle

		private (int firstIdx, int lastIdx) RenderCommands(int width, int height)
		{
			var top = height - 1;
			var left = 2;
			for (var i = 0; i < 10; i++)
			{
				if (visibleCommands[i] != null)
				{
					var name = visibleCommands[i].Name;
					if (name.Length + 2 > width - 24)
						break;

					Content.Set(left++, top, ' ', ForegroundColor, BackgroundColor);

					Content.Set(left++, top, i == 9 ? '1' : 'F', ConsoleColor.Cyan, BackgroundColor);
					Content.Set(left++, top, i == 9 ? '0' : (char)('1' + i), ConsoleColor.Cyan, BackgroundColor);
					var (endLeft, _) = Content.Write(left, top, name, foreground: ForegroundColor, background: BackgroundColor);
					Content.Set(endLeft, top, ' ', ForegroundColor, BackgroundColor);
					left = endLeft;
				}
			}

			if (left == 2)
				goto failed;
			return (2, left);
			failed:
			return (Int32.MaxValue, Int32.MinValue);
		} // proc RenderCommands

		public void RenderFrame(string title)
		{
			var width = Width;
			var height = Height;

			Content.Set(0, 0, TopLeftDoubleLine, ForegroundColor, BackgroundColor);
			Content.Set(width - 1, 0, TopRightDoubleLine, ForegroundColor, BackgroundColor);
			Content.Set(0, height - 1, BottomLeftDoubleLine, ForegroundColor, BackgroundColor);
			Content.Set(width - 1, height - 1, BottomRightDoubleLine, ForegroundColor, BackgroundColor);

			var (titleFirstIdx, titleLastIdx) = RenderTitle(title, width);
			var (commandFirstIdx, commandLastIdx) = RenderCommands(width, height);
			for (var i = 1; i < width - 1; i++)
			{
				if (i < titleFirstIdx || i > titleLastIdx)
					Content.Set(i, 0, HorizontalDoubleLine, ForegroundColor, BackgroundColor);
				if (i < commandFirstIdx || i > commandLastIdx)
					Content.Set(i, height - 1, HorizontalDoubleLine, ForegroundColor, BackgroundColor);
			}

			for (var i = 1; i < height - 1; i++)
			{
				Content.Set(0, i, VerticalDoubleLine, ForegroundColor, BackgroundColor);
				Content.Set(width - 1, i, VerticalDoubleLine, ForegroundColor, BackgroundColor);
			}
		} // proc RenderFrame

		#endregion

		#region -- OnHandleEvent, OnAccept, OnCancel ----------------------------------

		protected virtual void OnAccept()
		{
			dialogResult.SetResult(true);
			Application = null;
		} // proc OnAccept

		protected virtual void OnCancel()
		{
			dialogResult.SetResult(false);
			Application = null;
		} // proc OnCancel

		public override bool OnHandleEvent(EventArgs e)
		{
			if (e is ConsoleKeyUpEventArgs keyUp)
			{
				if (commands.TryGetValue(keyUp.Key, out var cmd))
				{
					if (cmd.CanExecute)
						cmd.Execute(this);
					return true;
				}
				else if (keyUp.Key == ConsoleKey.Enter)
				{
					OnAccept();
					return true;
				}
				else if (keyUp.Key == ConsoleKey.Escape)
				{
					OnCancel();
					return true;
				}
				else
					return true;
			}
			else if (e is ConsoleKeyDownEventArgs)
				return true;
			else
				return base.OnHandleEvent(e);
		} // proc OnHandleEvent

		#endregion

		#region -- Children -----------------------------------------------------------

		public void InsertControl(int index, ConsoleOverlay child)
		{
			children.Insert(index, child);
			if (Application != null)
			{
				child.ZOrder = ZOrder + index; // fixme: zorder collision
				child.Application = Application;
			}
		} // proc InsertControl

		public void RemoveControl(ConsoleOverlay child)
		{
			children.Remove(child);
			if (child.Application != null)
				child.Application = null;
		} // proc RemoveControl

		protected override void OnAdded()
		{
			base.OnAdded();

			var zOrder = ZOrder;
			foreach (var cur in children)
			{
				cur.ZOrder = ++zOrder;
				cur.Application = Application;
			}
		} // proc OnAdded

		protected override void OnRemoved()
		{
			foreach (var cur in children)
				cur.Application = null;
			base.OnRemoved();
		} // proc OnRemoved

		#endregion

		#region -- Commands -----------------------------------------------------------

		public void AddKeyCommand(ConsoleKey key, string name = null, Func<Task<bool?>> executeTask = null, Func<bool> canExecute = null)
		{
			commands[key] = new KeyCommand(key, name, executeTask, canExecute);
			RefreshCommands();
		} // proc AddKeyCommand

		private void RefreshCommands()
		{
			var updatedCommands = false;

			foreach (var c in commands.Values)
			{
				if (c.IsVisible)
				{
					var idx = c.Key - ConsoleKey.F1;
					if (c.CanExecute)
					{
						if (visibleCommands[idx] == null)
						{
							visibleCommands[idx] = c;
							updatedCommands = true;
						}
					}
					else
					{
						if (visibleCommands[idx] != null)
						{
							visibleCommands[idx] = null;
							updatedCommands = true;
						}
					}
				}
			}

			if (updatedCommands)
				Invalidate();
		} // proc RefreshCommands

		public static async Task<bool?> ContinueDialog(Task t, bool? accept = null)
		{
			await t;
			return accept;
		} // proc ContinueDialog

		#endregion

		public Task<bool> ShowDialogAsync()
		{
			Activate();
			return dialogResult.Task;
		} // proc ShowDialogAsync

		public IReadOnlyList<ConsoleOverlay> Children => children;
	} // class ConsoleDialogOverlay

	#endregion

	#region -- class ConsoleApplication -----------------------------------------------

	public sealed class ConsoleApplication
	{
		#region -- class ConsoleSynchronizationContext --------------------------------

		private sealed class ConsoleSynchronizationContext : SynchronizationContext
		{
			private readonly ConsoleApplication application;

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
		private int reservedBottomRowCount = 0;
		private int reservedRightColumnCount = 0;

		private readonly Stack<Action> eventQueue = new Stack<Action>(); // other events that join in the main thread
		private readonly ManualResetEventSlim eventQueueFilled = new ManualResetEventSlim(false); // marks that events in queue
		private readonly List<ConsoleOverlay> overlays = new List<ConsoleOverlay>(); // first overlay is the active overlay
		private readonly List<ConsoleFocusableOverlay> activeOverlays = new List<ConsoleFocusableOverlay>(); // list of active controls, top most gets keyboard input

		private SmallRect lastWindow;

		private readonly int currentThreadId;

		#region -- Ctor/Dtor ----------------------------------------------------------

		private ConsoleApplication(ConsoleOutputBuffer output = null, ConsoleInputBuffer input = null)
		{
			activeOutput = output ?? ConsoleOutputBuffer.GetActiveBuffer();
			var mode = activeOutput.ConsoleMode
				| 0x0008 //  ENABLE_WINDOW_INPUT
				| 0x0080 // ENABLE_EXTENDED_FLAGS 
				;

			//mode = mode & ~(uint)(
			//	0x0040 // ENABLE_QUICK_EDIT_MODE
			//);

			this.activeOutput.TrySetConsoleMode(mode);
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
			while (insertAt < overlays.Count && insertZ >= overlays[insertAt].ZOrder)
				insertAt++;

			overlays.Insert(insertAt, overlay);
			if (overlay is ConsoleFocusableOverlay fo)
				AddActiveOverlay(fo);

			if (overlay.IsRenderable)
				Invalidate();
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

			Invalidate(true);
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
				output.CurrentForeground = currentForegroundColor;
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
				t =>
				{
					using (r)
					{
						r.WriteToConsole();
						r.Application = null;
						return t.Result;
					}
				},
				TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously
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

		#region -- enum InvalidateFlag ------------------------------------------------

		[Flags]
		private enum InvalidateFlag
		{
			None = 0,
			InvalidateRender = 1,
			InvalidateCursor = 2
		} // enum InvalidateFlag

		#endregion

		private InvalidateFlag invalidateFlags = InvalidateFlag.None;

		internal void Invalidate(bool forceCursorRender = false)
		{
			invalidateFlags = InvalidateFlag.InvalidateRender;
			if (forceCursorRender)
				invalidateFlags |= InvalidateFlag.InvalidateCursor;
			eventQueueFilled.Set();
		} // proc Invalidate

		private void OnRender()
		{
			var isCursorInvalidate = (invalidateFlags & InvalidateFlag.InvalidateCursor) != 0;
			try
			{
				SmallRect window;
				do
				{
					var isDirty = false;
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
						if (isCursorInvalidate | activeOverlay.ResetInvalidateCursor() | output.ResetInvalidateCursor())
						{
							isDirty = OnRenderCursor(
								activeOverlay.ActualLeft + activeOverlay.CursorLeft,
								activeOverlay.ActualTop + activeOverlay.CursorTop,
								activeOverlay.CursorSize,
								activeOverlay.CursorSize > 0,
								ref window
						  );
						}
					}
					else if (isCursorInvalidate | output.ResetInvalidateCursor())
					{
						isDirty = OnRenderCursor(
							output.CursorLeft,
							output.CursorTop,
							output.CursorSize,
							output.CursorVisible,
							ref window
						);
					}

					isCursorInvalidate = false;
					if (!isDirty)
						break;
				}
				while (true);
			}
			finally
			{
				invalidateFlags = InvalidateFlag.None;
			}
		} // proc OnRender

		private bool OnRenderCursor(int left, int top, int cursorSize, bool cursorVisible, ref SmallRect window)
		{
			activeOutput.SetCursorPosition(left, top);
			activeOutput.SetCursor(cursorSize, cursorVisible);

			// get new window
			var afterWindow = activeOutput.GetWindow();
			var bottomRow = top + ReservedBottomRowCount;
			if (bottomRow > afterWindow.Bottom)
			{
				if (bottomRow >= activeOutput.Height) // we a hidden the buffer end
				{
					var scrollY = activeOutput.Height - bottomRow - 1;
					output.Scroll(0, scrollY);
				}
				else// move window down, to make bottom rows visible
				{
					var scrollY = bottomRow - afterWindow.Bottom;
					activeOutput.ResizeWindow(0, scrollY, 0, scrollY);
				}
				return true;
			}
			else
				return afterWindow.Left != window.Left || afterWindow.Top != window.Top;
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
			//Debug.WriteLine(ev.ToString());

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

					//// check if buffer width is greater
					//CharBuffer newContent = null;
					//if (activeBufferInfo.dwSize.X > outputBufferInfo.dwSize.X)
					//{
					//	newContent = new CharBuffer(activeBufferInfo.dwSize.X, activeBufferInfo.dwSize.Y);
					//	var diff = activeBufferInfo.dwSize.Y - output.Height;
					//	output.ReadBuffer(0, Math.Max(diff, 0), activeBufferInfo.dwSize.X, output.Height - 1, newContent, 0,0);
					//}

					outputBufferInfo.dwMaximumWindowSize = activeBufferInfo.dwMaximumWindowSize;
					outputBufferInfo.dwSize = activeBufferInfo.dwSize;
					outputBufferInfo.srWindow = activeBufferInfo.srWindow;

					UnsafeNativeMethods.SetConsoleScreenBufferInfoEx(output.Handle.DangerousGetHandle(), ref outputBufferInfo);

					//// recopy output buffer
					//if (newContent != null)
					//	output.WriteBuffer(0, 0, newContent);

					// notify size change
					foreach (var o in overlays)
						o.InvalidateSize();
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

					if (o.OnHandleEvent(ev))
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
					o.InvalidateSize();

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

		private void DoActionsUnsafe()
		{
			while (TryDequeue(out var action))
				action();
		} // proc DoActionsUnsafe

		private void DoEventsUnsafe(bool runIdle)
		{
			// check for window event
			CheckConsoleWindowPosition();

			// run events
			foreach (var ev in input.ReadInputEvents(-1))
				OnHandleEventUnsafe(ev);

			// run actions
			DoActionsUnsafe();

			// run idle event
			if (runIdle)
				OnIdleUnsafe();

			// render screen
			if (invalidateFlags != InvalidateFlag.None)
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

				if (continueLoop || invalidateFlags != InvalidateFlag.None)
					testEvent = WaitHandle.WaitAny(waitHandles, 400);
			}

			// empty async queue
			DoActionsUnsafe();

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

		public int ReservedBottomRowCount
		{
			get => reservedBottomRowCount;
			set
			{
				if (reservedBottomRowCount != value)
				{
					reservedBottomRowCount = value;
					Invalidate(true);
				}
			}
		} // proc ReservedBottomRowCount

		public int ReservedRightColumnCount
		{
			get => reservedRightColumnCount;
			set
			{
				if (reservedRightColumnCount != value)
				{
					reservedRightColumnCount = value;

					foreach (var cur in overlays.OfType<ConsoleReadLineOverlay>())
						cur.InvalidateSize();

					Invalidate(true);
				}
			}
		} // prop ReservedRightColumnCount

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
