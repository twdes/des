using System;

namespace TecWare.DE.Stuff
{
	#region -- class LogExtra -----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public static class LogExtra
	{
		public static void Start(this ILogger log, string name) => log?.LogMsg(LogMsgType.Information, ("-- " + name.Trim() + " wird gestartet ").PadRight(80, '-'));
		public static void Stop(this ILogger log, string name) => log?.LogMsg(LogMsgType.Information, ("-- " + name.Trim() + " wird gestoppt ").PadRight(80, '-'));
		public static void Abort(this ILogger log, string name) => log?.LogMsg(LogMsgType.Warning, ("-- " + name.Trim() + " ist abgebrochen wurden ").PadRight(80, '-'));
	} // class LogExtra

	#endregion

	#region -- class ProcsDE ------------------------------------------------------------

	public static partial class ProcsDE
	{
		public static bool IsFilterEqual(string value, string filterExpression)
		{
			var p1 = filterExpression.IndexOf('*');
			var p2 = filterExpression.LastIndexOf('*');
			if (p1 == p2) // only one start
			{
				if (p1 == 0) // => endswith
					return value.EndsWith(filterExpression.Substring(1), StringComparison.OrdinalIgnoreCase);
				else if (p1 == filterExpression.Length - 1)// => startwith
					return value.StartsWith(filterExpression.Substring(0, p1), StringComparison.OrdinalIgnoreCase);
				else
					return IsFilterEqualEx(value, filterExpression);
			}
			else
			{
				var p3 = filterExpression.IndexOf('*', p1 + 1);
				if (p3 == p2) // two stars
				{
					if (p1 == 0 && p2 == filterExpression.Length - 1) // => contains
						return value.IndexOf(filterExpression.Substring(1, p2 - 1), StringComparison.OrdinalIgnoreCase) >= 0;
					else
						return IsFilterEqualEx(value, filterExpression);
				}
				else
					return IsFilterEqualEx(value, filterExpression);
			}
		} // func IsFilterEqual

		private static bool IsFilterEqualEx(string value, string filterExpression)
		{
			throw new NotImplementedException();
		} // func IsFilterEqualEx

		public static bool PasswordCompare(string testPassword, string passwordHash)
			=> Passwords.PasswordCompare(testPassword, passwordHash);
	} // class ProcsDE

	#endregion
}
