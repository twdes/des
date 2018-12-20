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
using System.IO;
using System.Runtime.InteropServices;

namespace Neo.Console
{
	internal enum StdHandle : uint
	{
		Input = unchecked((uint)-10),
		Output = unchecked((uint)-11),
		Error = unchecked((uint)-12)
	} // enum StdHandle

	[Flags]
	internal enum CharAttributes : ushort
	{
		/// <summary>Text color contains blue.</summary>
		ForegroundBlue = 0x0001,
		/// <summary>Text color contains green.</summary>
		ForegroundGreen = 0x0002,
		/// <summary> Text color contains red.</summary>
		ForegroundRed = 0x0004,
		/// <summary>Text color is intensified.</summary>
		ForegroundItensity = 0x0008,
		/// <summary>Background color contains blue.</summary>
		BackgroundBlue = 0x0010,
		/// <summary>Background color contains green.</summary>
		BackgroundGreen = 0x0020,
		/// <summary>Background color contains red.</summary>
		BackgroundRed = 0x0040,
		/// <summary>Background color is intensified.</summary>
		BackgroundItensity = 0x0080,
		/// <summary>Leading byte.</summary>
		CommonLvbLeadingByte = 0x0100,
		/// <summary>Trailing byte.</summary>
		CommonLvbTrailingByte = 0x0200,
		/// <summary>Top horizontal</summary>
		CommonLvbGridHorizontal = 0x0400,
		/// <summary>Left vertical.</summary>
		CommonLvbGridLvertical = 0x0800,
		/// <summary>Right vertical.</summary>
		CommonLvbGridRvertical = 0x1000,
		/// <summary>Reverse foreground and background attribute.</summary>
		CommonLvbReverseVideo = 0x4000,
		/// <summary>Underscore.</summary>
		CommonLvbUnderscore = 0x8000
	} // enum CharAttributes

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	internal struct Coord
	{
		public short X;
		public short Y;

		public override string ToString()
			=> $"({X},{Y})";
	} // struct Coord

	internal struct SmallRect
	{
		public short Left;
		public short Top;
		public short Right;
		public short Bottom;

		public override string ToString()
			=> $"({Left},{Top})-({Right},{Bottom})";
	} // struct Coord

	[DebuggerDisplay("{Char}:{Attributes}")]
	internal struct CharInfo
	{
		public char Char;
		public CharAttributes Attributes;
	} // struct CharInfo

	[Flags]
	internal enum DesiredAccessRights : uint
	{
		GenericRead = 0x80000000,
		GenericWrite = 0x40000000
	} // enum DesiredAccessRights

	[Flags]
	internal enum DuplicateOptions : uint
	{
		CloseSource = 0x00000001,
		SameAccess = 0x00000002
	} // enum DuplicateOptions

	internal struct ConsoleCursorInfo
	{
		public uint dwSize;
		public bool bVisible;
	} // struct ConsoleCursorInfo

	internal struct ConsoleScreenBufferInfo
	{
		public Coord dwSize;
		public Coord dwCursorPosition;
		public CharAttributes wAttributes;
		public SmallRect srWindow;
		public Coord dwMaximumWindowSize;
	} // struct ConsoleScreenBufferInfo

	internal struct ConsoleScreenBufferInfoEx
	{
		public uint cbSize;
		public Coord dwSize;
		public Coord dwCursorPosition;
		public CharAttributes wAttributes;
		public SmallRect srWindow;
		public Coord dwMaximumWindowSize;

		public CharAttributes wPopupAttributes;
		public bool bFullscreenSupported;
		[MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
		public uint[] ColorTable;

		public static uint Size => (uint)Marshal.SizeOf<ConsoleScreenBufferInfoEx>();
	} // struct ConsoleScreenBufferInfoEx

	//internal struct ConsoleSelectionInfo
	//{
	//	public uint dwFlags;
	//	public Coord dwSelectionAnchor;
	//	public SmallRect srSelection;
	//} // struct ConsoleSelectionInfo

	internal enum EventType : ushort
	{
		KeyEvent = 0x0001,
		MouseEvent = 0x0002,
		WindowBufferSizeEvent = 0x0004,
		MenuEvent = 0x0008,
		FocusEvent = 0x0010
	} // enum EventType

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto)]
	internal struct ConsoleKeyEventRecord
	{
		[FieldOffset(0)]
		internal bool bKeyDown;
		[FieldOffset(4)]
		internal ushort wRepeatCount;
		[FieldOffset(6)]
		internal ushort wVirtualKeyCode;
		[FieldOffset(8)]
		internal ushort wVirtualScanCode;
		[FieldOffset(10)]
		internal char uChar;
		[FieldOffset(12)]
		internal uint dwControlKeyState;
	} // event ConsoleKeyEventRecord

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	internal struct ConsoleMouseEventRecord
	{
		internal Coord dwMousePosition;
		internal uint dwButtonState;
		internal uint dwControlKeyState;
		internal uint dwEventFlags;
	} // struct ConsoleMouseEventRecord

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto)]
	internal struct ConsoleWindowBufferSizeRecord
	{
		[FieldOffset(0)]
		internal Coord dwSize;
	} // struct ConsoleWindowBufferSizeRecord

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	internal struct ConsoleMenuEventRecord
	{
		internal uint dwCommandId;
	} // struct ConsoleMenuEventRecord

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto)]
	internal struct ConsoleFocusEventRecord
	{
		[FieldOffset(0)]
		internal bool bSetFocus;
	} // struct ConsoleFocusEventRecord

	[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto)]
	internal struct ConsoleInputRecord
	{
		[FieldOffset(0)]
		internal EventType EventType;
		[FieldOffset(4)]
		internal ConsoleKeyEventRecord KeyEvent;
		[FieldOffset(4)]
		internal ConsoleMouseEventRecord MouseEvent;
		[FieldOffset(4)]
		internal ConsoleWindowBufferSizeRecord WindowBufferSizeEvent;
		[FieldOffset(4)]
		internal ConsoleMenuEventRecord MenuEvent;
		[FieldOffset(4)]
		internal ConsoleFocusEventRecord FocusEvent;
	} // struct ConsoleInputRecord

	internal static class UnsafeNativeMethods
	{
		public const uint CONSOLE_TEXTMODE_BUFFER = 1;

		private const string kernel32 = "kernel32.dll";

		[DllImport(kernel32, SetLastError = true)]
		public static extern IntPtr GetStdHandle(StdHandle nStdHandle);

		[DllImport(kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern int GetConsoleCP();
		[DllImport(kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern int GetConsoleOutputCP();
		
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern IntPtr CreateConsoleScreenBuffer([In] DesiredAccessRights dwDesiredAccess, [In, MarshalAs(UnmanagedType.U4)] FileShare dwShareMode, [In] IntPtr lpSecurityAttributes, [In] uint dwFlags, [In] IntPtr lpScreenBufferData);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool FillConsoleOutputAttribute([In] IntPtr hConsoleOutput, [In] CharAttributes wAttribute, [In] uint nLength, [In] Coord dwWriteCoord, [Out] out uint lpNumberOfAttrsWritten);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool FillConsoleOutputCharacter([In] IntPtr hConsoleOutput, [In] char cCharacter, [In] uint nLength, [In] Coord dwWriteCoord, [Out] out uint lpNumberOfCharsWritten);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool GetConsoleCursorInfo([In] IntPtr hConsoleOutput, [Out] out ConsoleCursorInfo lpConsoleCursorInfo);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool SetConsoleCursorInfo([In] IntPtr hConsoleOutput, [In] ref ConsoleCursorInfo lpConsoleCursorInfo);

		[DllImport(kernel32, SetLastError = true)]
		public static extern bool SetConsoleWindowInfo([In] IntPtr hConsoleOutput, [In] bool bAbsolute, [In] ref SmallRect lpConsoleWindow);
		//[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		//public static extern bool GetConsoleSelectionInfo([Out] out ConsoleSelectionInfo lpConsoleSelectionInfo);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool GetConsoleScreenBufferInfo([In] IntPtr hConsoleOutput, [Out]out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool GetConsoleScreenBufferInfoEx([In] IntPtr hConsoleOutput, [In, Out]ref ConsoleScreenBufferInfoEx lpConsoleScreenBufferInfo);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool SetConsoleScreenBufferInfoEx([In] IntPtr hConsoleOutput, [In]ref ConsoleScreenBufferInfoEx lpConsoleScreenBufferInfo);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool ScrollConsoleScreenBuffer([In] IntPtr hConsoleOutput, [In] ref SmallRect lpScrollRectangle, [In] ref SmallRect lpClipRectangle, [In] Coord dwDestinationOrigin, [In] ref CharInfo lpFill);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool ScrollConsoleScreenBuffer([In] IntPtr hConsoleOutput, [In] ref SmallRect lpScrollRectangle, [In] IntPtr lpClipRectangle, [In] Coord dwDestinationOrigin, [In] ref CharInfo lpFill);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool SetConsoleCursorPosition([In] IntPtr hConsoleOutput, [In] Coord dwCursorPosition);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool SetConsoleScreenBufferSize([In] IntPtr hConsoleOutput, [In] Coord dwSize);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool SetConsoleTextAttribute([In] IntPtr hConsoleOutput, [In] CharAttributes wAttributes);

		//[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		//public static extern bool SetConsoleWindowInfo([In] IntPtr hConsoleOutput, [In] bool bAbsolute, [In] ref SmallRect lpConsoleWindow);

		[DllImport(kernel32, SetLastError = true)]
		public static extern bool GetConsoleMode([In] IntPtr hConsoleInput, [Out] out uint dwMode);
		[DllImport(kernel32, SetLastError = true)]
		public static extern bool SetConsoleMode([In] IntPtr hConsoleInput, [In] uint dwMode);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool ReadConsoleOutput([In]IntPtr hConsoleInput, [In,Out]CharInfo[,] lpBuffer, [In]Coord dwBufferSize, [In]Coord dwBufferCoord, [In, Out] ref SmallRect lpReadRegion);

		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static unsafe extern bool WriteConsole([In] IntPtr hConsoleOutput, [In] byte* lpBuffer, [In] uint nNumberOfCharsToWrite, [Out] out uint lpNumberOfCharsWritten, IntPtr lpReserved);
		
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool WriteConsoleOutput([In] IntPtr hConsoleOutput, [In] CharInfo[,] lpBuffer, [In] Coord dwBufferSize, [In] Coord dwBufferCoord, [In, Out] ref SmallRect lpWriteRegion);

		[DllImport(kernel32, SetLastError = true)]
		public static extern bool GetNumberOfConsoleInputEvents([In] IntPtr hConsoleOutput, [Out] out uint lpcNumberOfEvents);
		[DllImport(kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool ReadConsoleInput([In] IntPtr hConsoleOutput, [In, Out] ConsoleInputRecord[] lpBuffer, [In] uint nLength, [Out] out uint lpNumberOfEventsRead);
		[DllImport(kernel32, SetLastError = true)]
		public static extern bool FlushConsoleInputBuffer([In] IntPtr hConsoleOutput);

		[DllImport(kernel32, SetLastError = true)]
		public static extern bool SetConsoleActiveScreenBuffer([In] IntPtr hConsoleOutput);

		[DllImport(kernel32, SetLastError = true)]
		public static extern IntPtr GetCurrentProcess();
		[DllImport(kernel32, SetLastError = true)]
		public static extern bool DuplicateHandle([In] IntPtr hSourceProcessHandle, [In] IntPtr hSourceHandle, [In] IntPtr hTargetProcessHandle, [Out] out IntPtr lpTargetHandle, [In] DesiredAccessRights dwDesiredAccess, [In] bool bInheritHandle, [In] DuplicateOptions dwOptions);
	} // class NativeMethods
}
