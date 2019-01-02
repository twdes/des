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
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static Neo.Console.UnsafeNativeMethods;

namespace Neo.Console
{
	#region -- class ConsoleSetFocusEventArgs -----------------------------------------

	public sealed class ConsoleSetFocusEventArgs : EventArgs
	{
		private ConsoleSetFocusEventArgs()
		{
		} // ctor

		public override string ToString()
			=> nameof(ConsoleSetFocusEventArgs);

		public static ConsoleSetFocusEventArgs Default { get; } = new ConsoleSetFocusEventArgs();
	} // class ConsoleSetFocusEventArgs

	#endregion

	#region -- class ConsoleLostFocusEventArgs ----------------------------------------

	public sealed class ConsoleLostFocusEventArgs : EventArgs
	{
		public ConsoleLostFocusEventArgs()
		{
		} // ctor

		public override string ToString()
			=> nameof(ConsoleLostFocusEventArgs);

		public static ConsoleLostFocusEventArgs Default { get; } = new ConsoleLostFocusEventArgs();
	} // class ConsoleLostFocusEventArgs

	#endregion

	#region -- enum ConsoleKeyModifiers -----------------------------------------------

	[Flags]
	public enum ConsoleKeyModifiers
	{
		/// <summary></summary>
		None = 0,
		/// <summary>The right ALT key is pressed.</summary>
		RightAltPressed = 0x0001,
		/// <summary>The left ALT key is pressed.</summary>
		LeftAltPressed = 0x0002,
		/// <summary>The right CTRL key is pressed.</summary>
		RightCtrlPressed = 0x0004,
		/// <summary>The left CTRL key is pressed.</summary>
		LeftCtrlPressed = 0x0008,
		/// <summary>The SHIFT key is pressed.</summary>
		ShiftPressed = 0x0010,
		/// <summary>The NUM LOCK light is on.</summary>
		NumLockOn = 0x0020,
		/// <summary>The SCROLL LOCK light is on.</summary>
		ScrollLockOn = 0x0040,
		/// <summary>The CAPS LOCK light is on.</summary>
		CapsLockOn = 0x0080,
		/// <summary>The key is enhanced.</summary>
		EnhancedKey = 0x0100,

		AltPressed = LeftAltPressed | RightAltPressed,
		CtrlPressed = LeftCtrlPressed | RightCtrlPressed
	} // enum ConsoleKeyModifiers

	#endregion

	#region -- enum ConsoleMouseButtonState -------------------------------------------

	[Flags]
	public enum ConsoleMouseButtonState
	{
		ButtonLeft = 0x0001,
		ButtonRight = 0x0002,
		Button2 = 0x0004,
		Button3 = 0x0008,
		Button4 = 0x0010
	} // enum ConsoleMouseButtonState

	#endregion

	#region -- enum ConsoleWheelOrientation -------------------------------------------

	public enum ConsoleWheelOrientation
	{
		Horizontal,
		Vertical
	} // enum ConsoleWheelOrientation

	#endregion

	#region -- class ConsoleKeyEventArgs ----------------------------------------------

	public abstract class ConsoleKeyEventArgs : EventArgs
	{
		private readonly ConsoleKey keyCode;
		private readonly ConsoleKeyModifiers keyModifiers;
		private readonly char keyChar;

		protected ConsoleKeyEventArgs(ConsoleKey keyCode, ConsoleKeyModifiers keyModifiers, char keyChar)
		{
			this.keyCode = keyCode;
			this.keyModifiers = keyModifiers;
			this.keyChar = keyChar;
		} // ctor

		public override string ToString()
			=> $"{GetType().Name}: code={keyCode}, char={keyChar}, mod={keyModifiers}";

		public ConsoleKey Key => keyCode;
		public char KeyChar => keyChar;
		public ConsoleKeyModifiers KeyModifiers => keyModifiers;

		public ConsoleModifiers Modifiers
		{
			get
			{
				ConsoleModifiers m = 0;
				if ((keyModifiers & ConsoleKeyModifiers.ShiftPressed) != 0)
					m |= ConsoleModifiers.Shift;
				if ((keyModifiers & ConsoleKeyModifiers.AltPressed) != 0)
					m |= ConsoleModifiers.Alt;
				if ((keyModifiers & ConsoleKeyModifiers.CtrlPressed) != 0)
					m |= ConsoleModifiers.Control;
				return m;
			}
		} // prop Modifiers

		public static implicit operator ConsoleKeyInfo(ConsoleKeyEventArgs arg)
		{
			var modifiers = arg.Modifiers;
			return new ConsoleKeyInfo(arg.keyChar, arg.keyCode, (modifiers & ConsoleModifiers.Shift) != 0, (modifiers & ConsoleModifiers.Alt) != 0, (modifiers & ConsoleModifiers.Control) != 0);
		} // operator
	} // class ConsoleKeyEventArgs

	#endregion

	#region -- class ConsoleKeyDownEventArgs ------------------------------------------

	public sealed class ConsoleKeyDownEventArgs : ConsoleKeyEventArgs
	{
		public ConsoleKeyDownEventArgs(ConsoleKey keyCode, ConsoleKeyModifiers keyModifiers, char keyChar)
			: base(keyCode, keyModifiers, keyChar)
		{
		} // ctor
	} // class ConsoleKeyDownEventArgs

	#endregion

	#region -- class ConsoleKeyUpEventArgs --------------------------------------------

	public sealed class ConsoleKeyUpEventArgs : ConsoleKeyEventArgs
	{
		public ConsoleKeyUpEventArgs(ConsoleKey keyCode, ConsoleKeyModifiers keyModifiers, char keyChar)
			: base(keyCode, keyModifiers, keyChar)
		{
		} // ctor
	} // class ConsoleKeyUpEventArgs

	#endregion

	#region -- class ConsoleMenuEventArgs ---------------------------------------------

	public sealed class ConsoleMenuEventArgs : EventArgs
	{
		private readonly uint commandId;

		public ConsoleMenuEventArgs(uint commandId)
		{
			this.commandId = commandId;
		} // ctor

		public override string ToString()
			=> $"{nameof(ConsoleMenuEventArgs)}: id={commandId}";

		public uint CommandId => commandId;
	} // class ConsoleMenuEventArgs

	#endregion

	#region -- class ConsoleMouseEventArgs --------------------------------------------

	public class ConsoleMouseEventArgs : EventArgs
	{
		private readonly ConsoleMouseButtonState buttonState;
		private readonly ConsoleKeyModifiers keyModifiers;
		private readonly int mouseX;
		private readonly int mouseY;

		public ConsoleMouseEventArgs(ConsoleMouseButtonState buttonState, ConsoleKeyModifiers keyModifiers, int mouseX, int mouseY)
		{
			this.buttonState = buttonState;
			this.keyModifiers = keyModifiers;
			this.mouseX = mouseX;
			this.mouseY = mouseY;
		} // ctor

		public override string ToString()
			=> $"{GetType().Name}: buttons={buttonState}, mod={keyModifiers}, x={mouseX}, y={mouseY}";

		public ConsoleMouseButtonState ButtonState => buttonState;
		public ConsoleKeyModifiers KeyModifiers => keyModifiers;

		public int Left => mouseX;
		public int Top => mouseY;
	} // class ConsoleMouseEventArgs

	#endregion

	#region -- class ConsoleMouseMovedEventArgs ---------------------------------------

	public sealed class ConsoleMouseMovedEventArgs : ConsoleMouseEventArgs
	{
		public ConsoleMouseMovedEventArgs(ConsoleMouseButtonState buttonState, ConsoleKeyModifiers keyModifiers, int mouseX, int mouseY)
			: base(buttonState, keyModifiers, mouseX, mouseY)
		{
		}
	} // class ConsoleMouseMovedEventArgs

	#endregion

	#region -- class ConsoleMouseDoubleEventArgs --------------------------------------

	public sealed class ConsoleMouseDoubleEventArgs : ConsoleMouseEventArgs
	{
		public ConsoleMouseDoubleEventArgs(ConsoleMouseButtonState buttonState, ConsoleKeyModifiers keyModifiers, int mouseX, int mouseY)
			: base(buttonState, keyModifiers, mouseX, mouseY)
		{
		}
	} // class ConsoleMouseDoubleEventArgs

	#endregion

	#region -- class ConsoleMouseWheelEventArgs ---------------------------------------

	public sealed class ConsoleMouseWheelEventArgs : ConsoleMouseEventArgs
	{
		private readonly int value;
		private readonly ConsoleWheelOrientation orientation;

		public ConsoleMouseWheelEventArgs(ConsoleMouseButtonState buttonState, ConsoleKeyModifiers keyModifiers, int mouseX, int mouseY, ConsoleWheelOrientation orientation, int value)
			: base(buttonState, keyModifiers, mouseX, mouseY)
		{
			this.orientation = orientation;
			this.value = value;
		} // ctor

		public override string ToString()
			=> $"{base.ToString()}, o={orientation}, v={value}";

		public int Value => value;
		public ConsoleWheelOrientation Orientation => orientation;
	} // class ConsoleMouseWheelEventArgs

	#endregion

	#region -- class ConsoleBufferSizeEventArgs ---------------------------------------

	public sealed class ConsoleBufferSizeEventArgs : EventArgs
	{
		private readonly int width;
		private readonly int height;

		public ConsoleBufferSizeEventArgs(int width, int height)
		{
			this.width = width;
			this.height = height;
		} // ctor

		public override string ToString()
			=> $"{nameof(ConsoleBufferSizeEventArgs)}: width={width}, height={height}";

		public int Width => width;
		public int Height => height;
	} // class ConsoleBufferSizeEventArgs

	#endregion

	#region -- class ConsoleInputBuffer -----------------------------------------------

	public sealed class ConsoleInputBuffer : ConsoleBuffer
	{
		#region -- class ConsoleWaitHandle -------------------------------------------

		private sealed class ConsoleWaitHandle : WaitHandle
		{
			public ConsoleWaitHandle(SafeHandle handle)
			{
				this.SetSafeWaitHandle(new SafeWaitHandle(handle.DangerousGetHandle(), false));
			} // ctor
		} // class ConsoleWaitHandle

		#endregion

		private readonly WaitHandle waitHandle;

		#region -- Ctor/Dtor ----------------------------------------------------------

		private ConsoleInputBuffer(SafeFileHandle hInputBuffer)
			: base(hInputBuffer)
		{
			this.waitHandle = new ConsoleWaitHandle(hInputBuffer);
		} // ctor

		protected override ConsoleBuffer CloneInternal()
			=> new ConsoleInputBuffer(CloneHandle());

		#endregion

		#region -- ReadConsoleInput ---------------------------------------------------

		private static bool TryConvertEvent(ref ConsoleInputRecord r, out EventArgs ev, out int repeatCount)
		{
			switch (r.EventType)
			{
				case EventType.KeyEvent:
					{
						var keyCode = (ConsoleKey)r.KeyEvent.wVirtualKeyCode;
						var keyModifiers = (ConsoleKeyModifiers)r.KeyEvent.dwControlKeyState;
						if (r.KeyEvent.bKeyDown)
							ev = new ConsoleKeyDownEventArgs(keyCode, keyModifiers, r.KeyEvent.uChar);
						else
							ev = new ConsoleKeyUpEventArgs(keyCode, keyModifiers, r.KeyEvent.uChar);
						repeatCount = r.KeyEvent.wRepeatCount;
					}
					return true;
				case EventType.MouseEvent:
					{
						var buttonState = (ConsoleMouseButtonState)(r.MouseEvent.dwButtonState & 0xFFFF);
						var keyModifiers = (ConsoleKeyModifiers)r.MouseEvent.dwControlKeyState;
						switch (r.MouseEvent.dwEventFlags)
						{
							case 0x0000: // Click/Released
								ev = new ConsoleMouseEventArgs(buttonState, keyModifiers, r.MouseEvent.dwMousePosition.X, r.MouseEvent.dwMousePosition.Y);
								repeatCount = 1;
								return true;
							case 0x0001: // Moved
								ev = new ConsoleMouseMovedEventArgs(buttonState, keyModifiers, r.MouseEvent.dwMousePosition.X, r.MouseEvent.dwMousePosition.Y);
								repeatCount = 1;
								return true;
							case 0x0004: // Wheel
								ev = new ConsoleMouseWheelEventArgs(buttonState, keyModifiers, r.MouseEvent.dwMousePosition.X, r.MouseEvent.dwMousePosition.Y, ConsoleWheelOrientation.Horizontal, unchecked((int)(r.MouseEvent.dwButtonState >> 16)));
								repeatCount = 1;
								return true;
							case 0x0008: // HWheel
								ev = new ConsoleMouseWheelEventArgs(buttonState, keyModifiers, r.MouseEvent.dwMousePosition.X, r.MouseEvent.dwMousePosition.Y, ConsoleWheelOrientation.Horizontal, unchecked((int)(r.MouseEvent.dwButtonState >> 16)));
								repeatCount = 1;
								return true;
							default: // Moved
								ev = EventArgs.Empty;
								repeatCount = 0;
								return false;
						}
					}
				case EventType.MenuEvent:
					ev = new ConsoleMenuEventArgs(r.MenuEvent.dwCommandId);
					repeatCount = 1;
					return true;
				case EventType.WindowBufferSizeEvent:
					ev = new ConsoleBufferSizeEventArgs(r.WindowBufferSizeEvent.dwSize.X, r.WindowBufferSizeEvent.dwSize.Y);
					repeatCount = 1;
					return true;
				case EventType.FocusEvent:
					ev = r.FocusEvent.bSetFocus
						? (EventArgs)ConsoleSetFocusEventArgs.Default
						: (EventArgs)ConsoleLostFocusEventArgs.Default;
					repeatCount = 1;
					return true;
				default:
					ev = EventArgs.Empty;
					repeatCount = 0;
					return false;
			}
		} // func ConvertEvent

		public IEnumerable<EventArgs> ReadInputEvents(int maxCount = -1)
		{
			if (maxCount < 0)
				maxCount = NumberOfEvents;

			var buffer = new ConsoleInputRecord[1024];
			while (maxCount > 0)
			{
				var readLength = Math.Max(maxCount, buffer.Length);

				if (!ReadConsoleInput(DangerousGetHandle, buffer, unchecked((uint)readLength), out var readed))
					throw new Win32Exception();

				// return events
				for (var i = 0; i < readed; i++)
				{
					if (TryConvertEvent(ref buffer[i], out var ev, out var repeatCount))
					{
						for (var j = 0; j < repeatCount; j++)
							yield return ev;
					}
				}

				maxCount -= unchecked((int)readed);
			}
		} // proc ReadInputEvents

		public void FlushInputEvents()
		{
			if (!FlushConsoleInputBuffer(DangerousGetHandle))
				throw new Win32Exception();
		} // proc FlushInputEvents

		public bool EventsAvailable => NumberOfEvents > 0;

		public int NumberOfEvents
		{
			get
			{
				if (!GetNumberOfConsoleInputEvents(DangerousGetHandle, out var events))
					throw new Win32Exception();
				return unchecked((int)events);
			}
		} // prop NumberOfEvents

		#endregion

		public WaitHandle WaitHandle => waitHandle;

		public static ConsoleInputBuffer GetStandardInput()
			=> new ConsoleInputBuffer(new SafeFileHandle(GetStdHandle(StdHandle.Input), false));
	} // class ConsoleInputBuffer

	#endregion
}
