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
		public static bool PasswordCompare(string testPassword, string passwordHash)
			=> Passwords.PasswordCompare(testPassword, passwordHash);
	} // class ProcsDE

	#endregion
}
