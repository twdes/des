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
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;
using static Neo.Console.UnsafeNativeMethods;

namespace Neo.Console
{
	#region -- class ConsoleOutputBuffer ----------------------------------------------

	public sealed class ConsoleOutputBuffer : ConsoleBuffer
	{
		private readonly static IntPtr InvalidHandle = new IntPtr(-1);

		#region -- Ctor/Dtor ----------------------------------------------------------

		private ConsoleOutputBuffer(SafeFileHandle hScreenBuffer)
			: base(hScreenBuffer)
		{
		} // ctor

		protected override ConsoleBuffer CloneInternal()
			=> new ConsoleOutputBuffer(CloneHandle());

		public ConsoleOutputBuffer Copy()
		{
			GetConsoleScreenBufferInfoEx(DangerousGetHandle, out var bufferInfo);

			// create new console with same attributes
			var newConsolePtr = CreateConsoleScreenBufferCore();
			SetConsoleScreenBufferInfoEx(newConsolePtr, true, ref bufferInfo);

			var newConsole = new ConsoleOutputBuffer(new SafeFileHandle(newConsolePtr, true));

			// copy content
			newConsole.WriteBuffer(0, 0, ReadBuffer(0, 0, bufferInfo.dwSize.X - 1, bufferInfo.dwSize.Y - 1));

			return newConsole;
		} // proc Copy
		
		#endregion

		#region -- Buffer -------------------------------------------------------------

		private static void GetConsoleScreenBufferInfoEx(IntPtr hConsole, out ConsoleScreenBufferInfoEx bufferInfo)
		{
			bufferInfo = new ConsoleScreenBufferInfoEx() { cbSize = ConsoleScreenBufferInfoEx.Size };
			if (!UnsafeNativeMethods.GetConsoleScreenBufferInfoEx(hConsole, ref bufferInfo))
				throw new Win32Exception();
		} // proc GetConsoleScreenBufferInfoEx

		private static void SetConsoleScreenBufferInfoEx(IntPtr hConsole, bool all, ref ConsoleScreenBufferInfoEx bufferInfo)
		{
			if (!UnsafeNativeMethods.SetConsoleScreenBufferInfoEx(hConsole, ref bufferInfo))
				throw new Win32Exception();
			if (all)
				SetConsoleCursorPosition(hConsole, bufferInfo.dwCursorPosition);
		} // proc SetConsoleScreenBufferInfoEx


		private void GetConsoleScreenBufferInfo(out ConsoleScreenBufferInfo bufferInfo)
		{
			if (!UnsafeNativeMethods.GetConsoleScreenBufferInfo(DangerousGetHandle, out bufferInfo))
				throw new Win32Exception();
		} // proc GetConsoleScreenBufferInfo

		public void SetBufferSize(int width, int height)
		{
			if (!SetConsoleScreenBufferSize(DangerousGetHandle, CreateCoord(width, height)))
				throw new Win32Exception();
		} // proc SetBufferSize

		public void Scroll(int scrollX, int scrollY)
		{
			GetConsoleScreenBufferInfo(out var bufferInfo);
			var fill = new CharInfo { Char = ' ', Attributes = bufferInfo.wAttributes };
			var scroll = CreateSmallRect(-scrollX, -scrollY, Width - 1, Height - 1);
			var dst = CreateCoord(0, 0);
			ScrollConsoleScreenBuffer(DangerousGetHandle, ref scroll, IntPtr.Zero, dst, ref fill);
			SetCursorPosition(bufferInfo.dwCursorPosition.X + scrollX, bufferInfo.dwCursorPosition.Y + scrollY);
		} // proc Scroll

		public int Width
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.dwSize.X;
			}
			set
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				if (bufferInfo.dwSize.X != value)
					SetBufferSize(value, bufferInfo.dwSize.Y);
			}
		} // prop Width

		public int Height
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.dwSize.Y;
			}
			set
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				if (bufferInfo.dwSize.Y != value)
					SetBufferSize(bufferInfo.dwSize.X, value);
			}
		} // prop Height

		#endregion

		#region -- Fill ---------------------------------------------------------------

		public int Fill(int left, int top, int length, ConsoleColor foreground, ConsoleColor background)
			=> FillConsoleOutputAttribute(DangerousGetHandle, GetCharAttributes(foreground, background), (uint)length, CreateCoord(left, top), out var r)
				? unchecked((int)r)
				: throw new Win32Exception();

		public int Fill(int left, int top, int length, char c)
			=> FillConsoleOutputCharacter(DangerousGetHandle, c, (uint)length, CreateCoord(left, top), out var r)
				? unchecked((int)r)
				: throw new Win32Exception();

		#endregion

		#region -- ReadBuffer ---------------------------------------------------------

		public CharBuffer ReadBuffer(int left, int top, int right, int bottom)
		{
			// check dimensions
			if (left < 0 || top < 0 || right < 0 || bottom < 0)
				throw new ArgumentOutOfRangeException();

			GetConsoleScreenBufferInfo(out var bufferInfo);
			var readRight = right < bufferInfo.dwSize.X ? right : bufferInfo.dwSize.X - 1;
			var readBottom = bottom < bufferInfo.dwSize.Y ? bottom : bufferInfo.dwSize.Y - 1;

			// create target buffer
			var b = new CharBuffer(right - left + 1, bottom - top + 1);
			ReadBufferIntern(left, top, readRight, readBottom, b, 0, 0, out _, out _);

			// missing in buffer
			if (readRight < right)
				b.Fill(readRight - left + 1, 0, b.Width - 1, b.Height - 1, ' ');
			if (readBottom < bottom)
				b.Fill(0, readBottom - top + 1, b.Width - 1, b.Height - 1, ' ');

			return b;
		} // func ReadBuffer

		public void ReadBuffer(int left, int top, int right, int bottom, CharBuffer destination, int destinationLeft, int destinationTop)
			=> ReadBuffer(left, top, right, bottom, destination, destinationLeft, destinationTop, out var destinationRight, out var destinationBottom);

		public void ReadBuffer(int left, int top, int right, int bottom, CharBuffer destination, int destinationLeft, int destinationTop, out int destinationRight, out int destinationBottom)
		{
			// check source rectangle

			ReadBufferIntern(left, top, right, bottom, destination, destinationLeft, destinationTop, out destinationRight, out destinationBottom);
		} // proc ReadBuffer

		private void ReadBufferIntern(int left, int top, int right, int bottom, CharBuffer destination, int destinationLeft, int destinationTop, out int destinationRight, out int destinationBottom)
		{
			// copy buffer
			var readRegion = CreateSmallRect(left, top, right, bottom);
			var bufSize = CreateCoord(destination.Width, destination.Height);
			var bufOffset = CreateCoord(destinationLeft, destinationTop);
			if (!ReadConsoleOutput(DangerousGetHandle, destination.Chars, bufSize, bufOffset, ref readRegion))
				throw new Win32Exception();

			// get copied area
			destinationRight = destinationLeft + (readRegion.Right - readRegion.Left) + 1;
			destinationBottom = destinationTop + (readRegion.Bottom - readRegion.Top) + 1;
		} // proc ReadBuffer

		#endregion

		#region -- WriteBuffer --------------------------------------------------------

		public void WriteBuffer(int destinationLeft, int destinationTop, CharBuffer source)
			=> WriteBuffer(destinationLeft, destinationTop, source, 0, 0, source.Width - 1, source.Height - 1);

		public void WriteBuffer(int destinationLeft, int destinationTop, CharBuffer source, int sourceLeft, int sourceTop, int sourceRight, int sourceBottom)
		{
			var destinationRight = destinationLeft + (sourceRight - sourceLeft);
			var destinationBottom = destinationTop + (sourceBottom - sourceTop);

			// write chars
			var writeRegion = CreateSmallRect(destinationLeft, destinationTop, destinationRight, destinationBottom);
			var bufSize = CreateCoord(source.Width, source.Height);
			var bufOffset = CreateCoord(sourceLeft, sourceTop);
			if (!WriteConsoleOutput(DangerousGetHandle, source.Chars, bufSize, bufOffset, ref writeRegion))
				throw new Win32Exception();
		} // proc WriteBuffer

		#endregion

		#region -- WriteConsole -------------------------------------------------------

		public void Write(string text)
		{
			var b = DefaultEncoding.GetBytes(text);
			WriteConsole(b, 0, b.Length);
		} // proc Write
		
		public unsafe int WriteConsole(byte[] buffer, int offset, int length)
		{
			if (length <= 0)
				return 0;
			if (buffer.Length < offset + length)
				throw new ArgumentOutOfRangeException(nameof(length));

			fixed (byte* p = buffer)
			{
				if (!UnsafeNativeMethods.WriteConsole(DangerousGetHandle, p + offset, unchecked((uint)length / 2), out var written, IntPtr.Zero))
					throw new Win32Exception();
				return unchecked((int)written);
			}
		} // func WriteConsole

		#endregion

		#region -- Cursor -------------------------------------------------------------

		private int lastCursorLeft = -1;
		private int lastCursorTop = -1;
		private int lastCursorSize = -1;

		private void GetConsoleCursorInfo(out ConsoleCursorInfo cursorInfo)
		{
			if (!UnsafeNativeMethods.GetConsoleCursorInfo(DangerousGetHandle, out cursorInfo))
				throw new Win32Exception();
		} // proc GetConsoleCursorInfo

		public void SetCursor(int? cursorSize, bool? cursorVisible)
		{
			var isChanged = false;
			GetConsoleCursorInfo(out var cursorInfo);

			if (cursorSize.HasValue)
			{
				if (cursorSize > 100)
					cursorSize = 100;
				if (cursorSize < 1)
					cursorSize = 1;

				var v = unchecked((uint)cursorSize);
				if (v != cursorInfo.dwSize)
				{
					cursorInfo.dwSize = v;
					isChanged = true;
				}
			}

			if (cursorVisible.HasValue)
			{
				if (cursorInfo.bVisible != cursorVisible.Value)
				{
					cursorInfo.bVisible = cursorVisible.Value;
					isChanged = true;
				}
			}
			
			if (isChanged && !SetConsoleCursorInfo(DangerousGetHandle, ref cursorInfo))
				throw new Win32Exception();
		} // proc SetCursor

		public bool SetCursorPosition(int left, int top)
			=> SetConsoleCursorPosition(DangerousGetHandle, CreateCoord(left, top));

		public void SetColor(ConsoleColor foreground, ConsoleColor background)
		{
			if (!SetConsoleTextAttribute(DangerousGetHandle, GetCharAttributes(foreground, background)))
				throw new Win32Exception();
		} // proc SetColor

		/// <summary>Is the cursor position changed.</summary>
		/// <returns></returns>
		public bool ResetInvalidateCursor()
		{
			GetConsoleCursorInfo(out var cursorInfo);
			GetConsoleScreenBufferInfo(out var bufferInfo);

			var isVisible = lastCursorSize > 0;
			if (cursorInfo.bVisible != isVisible
				|| cursorInfo.dwSize != lastCursorSize
				|| bufferInfo.dwCursorPosition.X != lastCursorLeft
				|| bufferInfo.dwCursorPosition.Y != lastCursorTop)
			{
				lastCursorSize = cursorInfo.bVisible ? unchecked((int)cursorInfo.dwSize) : 0;
				lastCursorLeft = bufferInfo.dwCursorPosition.X;
				lastCursorTop = bufferInfo.dwCursorPosition.Y;

				return true;
			}
			else
				return false;
		} // func ResetInvalidateCursor

		/// <summary>Size of Cursor between 1..100</summary>
		public int CursorSize
		{
			get
			{
				GetConsoleCursorInfo(out var cursorInfo);
				return unchecked((int)cursorInfo.dwSize);
			}
			set => SetCursor(value, null);
		} // prop CursorSize

		public bool CursorVisible
		{
			get
			{
				GetConsoleCursorInfo(out var cursorInfo);
				return cursorInfo.bVisible;
			}
			set => SetCursor(null, value);
		} // prop CursorVisible

		public ConsoleColor CurrentForeground
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return (ConsoleColor)((ushort)bufferInfo.wAttributes & 0x000F);
			}
			set => SetColor(value, CurrentBackground);
		} // prop CurrentForeground

		public ConsoleColor CurrentBackground
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return (ConsoleColor)(((ushort)bufferInfo.wAttributes & 0x00F0) >> 4);
			}
			set => SetColor(CurrentForeground, value);
		} // prop CurrentBackground

		public int CursorLeft
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.dwCursorPosition.X;
			}
			set => SetCursorPosition(value, CursorTop);
		} // prop CursorLeft

		public int CursorTop
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.dwCursorPosition.Y;
			}
			set => SetCursorPosition(CursorLeft, value);
		} // prop CursorTop

		#endregion

		#region -- Window -------------------------------------------------------------

		internal SmallRect GetWindow()
		{
			GetConsoleScreenBufferInfo(out var bufferInfo);
			return bufferInfo.srWindow;
		} // func GetWindow

		internal void ResizeWindow(int left, int top, int right, int bottom)
		{
			var rc = CreateSmallRect(left, top, right, bottom);
			if (!SetConsoleWindowInfo(DangerousGetHandle, false, ref rc))
				throw new Win32Exception();
		} // proc SetWindow

		public int WindowLeft
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.srWindow.Left;
			}
		} // func WindowLeft

		public int WindowTop
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.srWindow.Top;
			}
		} // func WindowTop

		public int WindowRight
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.srWindow.Right;
			}
		} // func WindowRight

		public int WindowBottom
		{
			get
			{
				GetConsoleScreenBufferInfo(out var bufferInfo);
				return bufferInfo.srWindow.Bottom;
			}
		} // func WindowBottom

		#endregion

		/// <summary>Try change console mode.</summary>
		/// <param name="consoleMode"></param>
		/// <returns></returns>
		public bool TrySetConsoleMode(uint consoleMode)
			=> SetConsoleMode(DangerousGetHandle, consoleMode);

		/// <summary></summary>
		public uint ConsoleMode
		{
			get
			{
				if (!GetConsoleMode(DangerousGetHandle, out var dwMode))
					throw new Win32Exception();
				return dwMode;
			}
			set
			{
				if (!TrySetConsoleMode(value))
					throw new Win32Exception();
			}
		}

		public static ConsoleOutputBuffer GetActiveBuffer()
		{
			var hActive = new SafeFileHandle(GetStdHandle(StdHandle.Output), false);
			return new ConsoleOutputBuffer(hActive);
		} // func GetActiveBuffer

		private static IntPtr CreateConsoleScreenBufferCore()
		{
			var hNewScreenBuffer = CreateConsoleScreenBuffer(DesiredAccessRights.GenericRead | DesiredAccessRights.GenericWrite, FileShare.ReadWrite, IntPtr.Zero, CONSOLE_TEXTMODE_BUFFER, IntPtr.Zero);
			if (hNewScreenBuffer == InvalidHandle)
				throw new Win32Exception();
		
			return hNewScreenBuffer;
		} // func CreateConsoleScreenBufferCore

		public static ConsoleOutputBuffer CreateNew(int width, int height)
		{
			var hNewScreenBuffer = CreateConsoleScreenBufferCore();

			if (!SetConsoleScreenBufferSize(hNewScreenBuffer, CreateCoord(width, height)))
				throw new Win32Exception();

			return new ConsoleOutputBuffer(new SafeFileHandle(hNewScreenBuffer, true));
		} // func CreateNew

		internal static Coord CreateCoord(int x, int y)
			=> new Coord() { X = unchecked((short)x), Y = unchecked((short)y) };

		internal static SmallRect CreateSmallRect(int left, int top, int right, int bottom)
			=> new SmallRect() { Left = unchecked((short)left), Top = unchecked((short)top), Right = unchecked((short)right), Bottom = unchecked((short)bottom) };

		internal static CharAttributes GetCharAttributes(ConsoleColor foreground, ConsoleColor background)
			=> (CharAttributes)unchecked((ushort)foreground | ((ushort)background << 4));
	} // class ConsoleBuffer

	#endregion
}
