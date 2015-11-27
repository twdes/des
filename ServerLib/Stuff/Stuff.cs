using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;

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
		public static IEnumerable<X509Certificate2> FindCertificate(string search)
		{
			if (search.StartsWith("store://")) // search in the store
			{
				// store://location/name/subject
				// default for location is "currentuser"
				// allowed is "cu", "lm" as shortcut
				// name default is "My"

				search = search.Substring(8);
				var parts = search.Split('/');

				StoreLocation storeLocation = StoreLocation.CurrentUser;
				StoreName storeName = StoreName.My;
				var subject = String.Empty;

				var ofs = 0;
				if (parts.Length >= 3)
				{
					// first should be user
					if (String.Compare(parts[0], "m", StringComparison.OrdinalIgnoreCase) == 0 ||
						String.Compare(parts[0], "LocalMachine", StringComparison.OrdinalIgnoreCase) == 0)
						storeLocation = StoreLocation.LocalMachine;
					ofs++;
				}
				if (parts.Length >= 2)
				{
					if (!Enum.TryParse<StoreName>(parts[ofs], out storeName))
						storeName = StoreName.My;
					ofs++;
				}
				if (parts.Length >= 1)
				{
					subject = parts[ofs];
				}

				using (var store = new X509Store(storeName, storeLocation))
				{
					store.Open(OpenFlags.ReadOnly);
					try
					{
						foreach (var c in store.Certificates)
						{
							if (String.IsNullOrEmpty(subject) || CertifacteMatchSubject(c.Subject, subject))
								yield return c;
						}
					}
					finally
					{
						store.Close();
					}
				}
			}
			else if (Path.IsPathRooted(search))
				yield return new X509Certificate2(search);
		} // func FindCertificate

		private static bool CertifacteMatchSubject(string subject, string expr)
		{
			return subject == expr; // todo: a select algorithm
		} // func CertifacteMatchSubject

		#region -- Filter -----------------------------------------------------------------

		/// <summary>Simple "Star"-Filter rule, for single-strings</summary>
		/// <param name="value"></param>
		/// <param name="filterExpression"></param>
		/// <returns></returns>
		public static bool IsFilterEqual(string value, string filterExpression)
		{
			var p1 = filterExpression.IndexOf('*');
			var p2 = filterExpression.LastIndexOf('*');
			if (p1 == p2) // only one start
			{
				if (p1 == 0) // => endswith
					if (value.Length == 1)
						return true;
					else
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

		#endregion
	} // class ProcsDE

	#endregion
}
