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

		public void WriteLine()
		{
			WriteLine(String.Empty);
		} // proc WriteLine

		public void WriteLine(string text)
		{
			lock (screenLock)
				Console.Out.WriteLine(text);
		} // proc WriteLine

		public void WriteObject(object o)
		{
			if (o == null)
				WriteLine("<null>");
			else
				WriteLine(o.ToString());
		} // proc WriteObject

		public void WriteError(Exception exception)
		{
			if (exception == null)
				return;

			var aggEx = exception as AggregateException;
			if (aggEx == null)
			{
				// write exception
				lock(screenLock)
				{
					using (SetColor(ConsoleColor.DarkRed))
					{
						Console.WriteLine($"[{exception.GetType().Name}]");
						Console.WriteLine($"  {exception.Message}");
          }
				}

				// chain exceptions
				WriteError(exception.InnerException);
			}
			else
			{
				foreach (var ex in aggEx.InnerExceptions)
					WriteError(ex);
			}
		} // proc WriteError

		public void WriteError(string text)
		{
			lock (screenLock)
				Console.Error.WriteLine(text);
		} // proc WriteError

		public void Write(string text)
			=> Write(text, ConsoleColor.Gray);

		public void Write(string text, ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
		{
			lock (screenLock)
			{
				using (SetColor(foreground, background))
					Console.Write(text);
			}
		} // proc WriteLine

		#region -- SetColor ---------------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ResetColors : IDisposable
		{
			public ResetColors(ConsoleColor foregroundColor, ConsoleColor backgroundColor)
			{
				this.OldBackgroundColor = Console.BackgroundColor;
				this.OldForegroundColor = Console.ForegroundColor;
				Console.BackgroundColor = backgroundColor;
				Console.ForegroundColor = foregroundColor;
			} // ctor

			public void Dispose()
			{
				Console.ForegroundColor = this.OldForegroundColor;
				Console.BackgroundColor = this.OldBackgroundColor;
			} // proc Dispose

			public ConsoleColor OldForegroundColor { get; }
			public ConsoleColor OldBackgroundColor { get; }
		} // class ResetColors

		public IDisposable SetColor(ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
			=> new ResetColors(foreground, background);

		#endregion

		#region -- SetCursor --------------------------------------------------------------

		private sealed class ResetCursor : IDisposable
		{			
			public ResetCursor(int left, int  top, bool visible)
			{
				this.OldCursorLeft = Console.CursorLeft;
				this.OldCursorTop = Console.CursorTop;
				this.OldVisible = Console.CursorVisible;

				Console.CursorLeft = left;
				Console.CursorTop = top;
				Console.CursorVisible = visible;
			} // ctor

			public void Dispose()
			{
				Console.CursorLeft = OldCursorLeft;
				Console.CursorTop = OldCursorTop;
				Console.CursorVisible = OldVisible;
			} // proc Dispose

			public int OldCursorLeft { get; }
			public int OldCursorTop { get; }
			public bool OldVisible { get; }
		} // class ResetCursor

		public IDisposable SetCursor(int? left = null, int? top = null, bool? visible = null)
			=> new ResetCursor(left ?? Console.CursorLeft, top ?? Console.CursorTop, visible ?? Console.CursorVisible);

		#endregion

		public object SyncRoot => screenLock;
	} // class DebugView
}
