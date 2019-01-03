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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Neo.Console.UnsafeNativeMethods;

namespace Neo.Console
{
	#region -- class CharBuffer -------------------------------------------------------

	public sealed class CharBuffer : ICloneable
	{
		private readonly CharInfo[,] chars;

		public CharBuffer(int width, int height)
		{
			chars = new CharInfo[height, width];
		} // ctor

		public CharBuffer(int width, int height, char fill, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
			: this(width, height)
		{
			chars = new CharInfo[height, width];

			Fill(0, 0, width - 1, height - 1, fill, foreground, background);
		} // ctor

		object ICloneable.Clone()
			=> Clone();

		public CharBuffer Clone()
		{
			var n = new CharBuffer(Width, Height);
			Array.Copy(chars, n.chars, chars.Length);
			return n;
		} // func Clone

		public void CopyTo(int left, int top, CharBuffer target)
		{
			var srcLeft = left < 0 ? -left : 0;
			var trgLeft = left >= 0 ? left : 0;
			var srcTop = top < 0 ? -top : 0;
			var trgTop = top >= 0 ? top : 0;
			var w = left + Width > target.Width ? target.Width - left : Width;
			var h = top + Height > target.Height ? target.Height - top : Height;

			var sy = srcTop;
			var ty = trgTop;
			while (sy < h)
			{
				var sx = srcLeft;
				var tx = trgLeft;
				while (sx < w)
				{
					target.chars[ty, tx] = chars[sy, sx];

					sx++;
					tx++;
				}

				sy++;
				ty++;
			}
		} // proc CopyTo

		public void Set(int left, int top, char c)
		{
			chars[top, left].Char = c;
		} // proc Set

		public void Set(int left, int top, char c, ConsoleColor? foreground = null, ConsoleColor? background = null)
		{
			chars[top, left].Char = c;
			var a = (ushort)chars[top, left].Attributes;
			if (foreground.HasValue)
				a = unchecked((ushort)((a & 0xFFF0) | (int)foreground.Value));
			if (background.HasValue)
				a = unchecked((ushort)((a & 0xFF0F) | ((int)background.Value << 4)));
			chars[top, left].Attributes = (CharAttributes)a;
		} // proc Set

		public void Fill(int left, int top, int right, int bottom, char fill, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
		{
			var attr = ConsoleOutputBuffer.GetCharAttributes(foreground, background);

			for (var y = top; y <= bottom; y++)
			{
				for (var x = left; x <= right; x++)
				{
					chars[y, x].Char = fill;
					chars[y, x].Attributes = attr;
				}
			}
		} // proc Fill

		public (int endLeft, int endTop) Write(int left, int top, string value, int? lineBreakTo = null, ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
		{
			if (String.IsNullOrEmpty(value))
				return (left, top);

			var attr = ConsoleOutputBuffer.GetCharAttributes(foreground, background);

			var y = top;
			var x = left;
			var l = value.Length;
			for (var i = 0; i < l; i++)
			{
				if (x >= Width)
				{
					if (lineBreakTo.HasValue)
					{
						y++;
						x = lineBreakTo.Value;
					}
					else
						return (x, y);
				}
				if (y >= Height)
					return (x, y);

				chars[y, x].Char = value[i];
				chars[y, x].Attributes = attr;
				x++;
			}
			return (x, y);
		} // proc Write

		public int Width => chars.GetLength(1);
		public int Height => chars.GetLength(0);

		internal CharInfo[,] Chars => chars;
	} // class CharBuffer

	#endregion

	#region -- class ConsoleBuffer ----------------------------------------------------

	public abstract class ConsoleBuffer : ICloneable, IDisposable
	{
		private readonly SafeFileHandle hBuffer;

		#region -- Ctor/Dtor ----------------------------------------------------------

		protected ConsoleBuffer(SafeFileHandle hBuffer)
		{
			this.hBuffer = hBuffer;
		} // ctor

		public void Dispose()
			=> Dispose(true);

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				hBuffer.Dispose();
		} // proc Dispose

		object ICloneable.Clone()
			=> CloneInternal();

		protected abstract ConsoleBuffer CloneInternal();
		
		protected SafeFileHandle CloneHandle()
		{
			// duplicate handle
			if (!DuplicateHandle(GetCurrentProcess(), hBuffer.DangerousGetHandle(), GetCurrentProcess(), out var hNewBuffer, 0, false, DuplicateOptions.SameAccess))
				throw new Win32Exception();
			return new SafeFileHandle(hNewBuffer, true);
		} // func CloneHandle

		#endregion

		protected IntPtr DangerousGetHandle => hBuffer.DangerousGetHandle();

		public SafeHandle Handle => hBuffer;

		public static Encoding DefaultEncoding { get; } = Encoding.Unicode;
	} // proc ConsoleBuffer

	#endregion
}
