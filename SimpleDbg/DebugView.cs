using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class DebugView
	{
		private object screenLock = new object();

		public void WriteLine(string text)
		{
			lock (screenLock)
				Console.Out.WriteLine(text);
		} // proc WriteLine

		public void WriteError(string text)
		{
			lock (screenLock)
				Console.Error.WriteLine(text);
		} // proc WriteError

		public void Write(string text, ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
		{
			lock (screenLock)
			{
				var oldFg = Console.ForegroundColor;
				var oldBg = Console.BackgroundColor;
				try
				{
					Console.ForegroundColor = foreground;
					Console.BackgroundColor = background;
					Console.Write(text);
				}
				finally
				{
					Console.ForegroundColor = oldFg;
					Console.BackgroundColor = oldBg;
				}
			}
		} // proc WriteLine
	} // class DebugView
}
