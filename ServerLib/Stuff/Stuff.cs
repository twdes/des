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
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Xml;
using Neo.IronLua;
using TecWare.DE.Server;

namespace TecWare.DE.Stuff
{
	#region -- class LogExtra ---------------------------------------------------------

	/// <summary>Log extensions</summary>
	public static class LogExtra
	{
		/// <summary>Write a start line in the log.</summary>
		/// <param name="log">Log implementation.</param>
		/// <param name="name">What was started.</param>
		public static void Start(this ILogger log, string name) => log?.LogMsg(LogMsgType.Information, ("-- " + name.Trim() + " wird gestartet ").PadRight(80, '-'));
		/// <summary>Write a stop line in the log.</summary>
		/// <param name="log">Log implementation.</param>
		/// <param name="name">What was stopped.</param>
		public static void Stop(this ILogger log, string name) => log?.LogMsg(LogMsgType.Information, ("-- " + name.Trim() + " wird gestoppt ").PadRight(80, '-'));
		/// <summary>Write a abort line in the log.</summary>
		/// <param name="log">Log implementation.</param>
		/// <param name="name">What was aborted.</param>
		public static void Abort(this ILogger log, string name) => log?.LogMsg(LogMsgType.Warning, ("-- " + name.Trim() + " ist abgebrochen wurden ").PadRight(80, '-'));
	} // class LogExtra

	#endregion

	#region -- class DisposableScopeThreadSecure --------------------------------------

	/// <summary>Disposable scope, that checks thead assignment.</summary>
	public sealed class DisposableScopeThreadSecure : IDisposable
	{
		private readonly Thread enterThread;
		private readonly Action dispose;

		/// <summary>Disposable scope, that checks thead assignment.</summary>
		/// <param name="dispose">Action that gets called on dispose.</param>
		public DisposableScopeThreadSecure(Action dispose)
		{
			this.enterThread = Thread.CurrentThread;
			this.dispose = dispose;
		} // ctor

		/// <summary>Dispose implementation.</summary>
		public void Dispose()
		{
			if (enterThread != Thread.CurrentThread)
				throw new InvalidOperationException("Enter and Exit must be in the same thread.");
			dispose();
		} // proc Dispose
	} // class DisposableScopeThreadSecure

	#endregion

	#region -- class ProcsDE ----------------------------------------------------------

	/// <summary>Helper for DE Server</summary>
	public static partial class ProcsDE
	{
		#region -- FindCertificate ----------------------------------------------------

		private static void CreateCertificateMatchThumbprintPredicate(string thumbPrint, ref Predicate<X509Certificate2> predicate)
		{
			if (thumbPrint.Length > 0)
				predicate = c => String.Compare(c.Thumbprint, thumbPrint, StringComparison.OrdinalIgnoreCase) == 0;
		} // func CreateCertificateMatchThumbprintPredicate

		private static string[] SplitSubject(string subject)
		{
			var filter = subject.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			for (var i = 0; i < filter.Length; i++)
				filter[i] = filter[i].Trim();
			return filter;
		} // func SplitSubject

		private static void CreateCertificateMatchSubjectPredicate(string filterSubject, ref Predicate<X509Certificate2> predicate)
		{
			var filterExpr = SplitSubject(filterSubject);
			if (filterExpr.Length > 0)
				predicate = c => CertifacteMatchSubject(c, filterExpr);
		} // proc CreateCertificateMatchSubjectPredicate

		private static bool CertifacteMatchSubject(X509Certificate2 cert, string[] filter)
		{
			var splittedSubject = SplitSubject(cert.Subject);

			foreach (var f in filter)
			{
				var p = f.IndexOf('=');
				if (p != -1)
				{
					var hit = false;
					foreach (var s in splittedSubject)
					{
						if (String.Compare(f, 0, s, 0, p, StringComparison.OrdinalIgnoreCase) == 0)
						{
							hit = true;
							if (String.Compare(f, s, StringComparison.OrdinalIgnoreCase) != 0)
								return false;
						}
					}
					if (!hit)
						return false;
				}
			}
			return true;
		} // func CertifacteMatchSubject

		/// <summary>Find certificate in the local certificate store.</summary>
		/// <param name="search">Search path to the certificate.</param>
		/// <returns>Matches.</returns>
		public static IEnumerable<X509Certificate2> FindCertificate(string search)
		{
			if (search.StartsWith("store://")) // search in the store
			{
				// store://location/name/subject
				// default for location is "currentuser"
				// allowed is "cu", "lm" as shortcut
				// name default is "My"

				// subject is a comma seperated key=value pair
				//         select via "subject:" <-- is default
				//                    "thumbprint:"
				// for name refere StoreName enumeration

				search = search.Substring(8);
				var parts = search.Split('/');

				var storeLocation = StoreLocation.CurrentUser;
				var storeName = StoreName.My;
				var filter = new Predicate<X509Certificate2>(c => true);

				var ofs = 0;
				if (parts.Length >= 3) // local machine or current user
				{
					// first should be user
					if (String.Compare(parts[0], "lm", StringComparison.OrdinalIgnoreCase) == 0 ||
						String.Compare(parts[0], "LocalMachine", StringComparison.OrdinalIgnoreCase) == 0)
						storeLocation = StoreLocation.LocalMachine;
					ofs++;
				}
				if (parts.Length >= 2) // check for store name
				{
					if (!Enum.TryParse(parts[ofs], true, out storeName))
						storeName = StoreName.My;
					ofs++;
				}
				if (parts.Length >= 1) // build filter string
				{
					var selectPart = parts[ofs] ?? String.Empty;
					if (selectPart.StartsWith("thumbprint:", StringComparison.OrdinalIgnoreCase))
						CreateCertificateMatchThumbprintPredicate(selectPart.Substring(11).Trim(), ref filter);
					else if (selectPart.StartsWith("subject:", StringComparison.OrdinalIgnoreCase))
						CreateCertificateMatchSubjectPredicate(selectPart.Substring(8), ref filter);
					else
						CreateCertificateMatchSubjectPredicate(selectPart, ref filter);
				}

				using (var store = new X509Store(storeName, storeLocation))
				{
					store.Open(OpenFlags.ReadOnly);
					try
					{
						foreach (var c in store.Certificates)
						{
							if (filter(c))
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

		#endregion

		#region -- PasswordCompare ----------------------------------------------------

		/// <summary>Compare the password with the password hash.</summary>
		/// <param name="testPassword">Password to test.</param>
		/// <param name="passwordHash">Hash string.</param>
		/// <returns><c>true</c>, if the password matches.</returns>
		public static bool PasswordCompare(string testPassword, string passwordHash)
			=> Passwords.PasswordCompare(testPassword, passwordHash);

		/// <summary>Compare the password with the password hash.</summary>
		/// <param name="testPassword">Password to test.</param>
		/// <param name="passwordHash">Password hash.</param>
		/// <returns><c>true</c>, if the password matches.</returns>
		public static bool PasswordCompare(string testPassword, byte[] passwordHash)
			=> Passwords.PasswordCompare(testPassword, passwordHash);

		/// <summary>Parse a hash string to an hash array.</summary>
		/// <param name="passwordHash">Hash string.</param>
		/// <returns>Password hash.</returns>
		public static byte[] ParsePasswordHash(string passwordHash)
			=> Passwords.ParsePasswordHash(passwordHash);

		/// <summary>Decode a password from a string.</summary>
		/// <param name="passwordValue">Password value.</param>
		/// <returns></returns>
		/// <remarks>
		/// If the password starts with 
		/// - win0x:[windows protected hex-bytes] 
		/// - win64:[base64 windows protected byte array]
		/// - plain:[plaintext]
		/// Nothing, it is a plain text password.
		/// 
		/// Windows Protected password can be encode with powershell ConvertFrom-SecureString without the key argument.
		/// </remarks>
		public static SecureString DecodePassword(string passwordValue)
			=> Passwords.DecodePassword(passwordValue);

		/// <summary>Encode a password for decode password.</summary>
		/// <param name="password"></param>
		/// <param name="passwordType"></param>
		/// <returns></returns>
		public static string EncodePassword(SecureString password, string passwordType)
			=> Passwords.EncodePassword(password, passwordType);

		/// <summary>Create a secure string from a string.</summary>
		/// <param name="password"></param>
		/// <returns></returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static SecureString CreateSecureString(this string password)
			=> CreateSecureString(password, 0, password.Length);

		/// <summary>Create a secure string from a string.</summary>
		/// <param name="password"></param>
		/// <param name="offset"></param>
		/// <param name="length"></param>
		/// <returns></returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public unsafe static SecureString CreateSecureString(this string password, int offset, int length)
		{
			if (String.IsNullOrEmpty(password))
				throw new ArgumentNullException(nameof(password));

			fixed (char* c = password)
				return new SecureString(c + offset, length);
		} // func CereateSecureString

		/// <summary></summary>
		/// <param name="secureString"></param>
		/// <param name="other"></param>
		/// <returns></returns>
		public static unsafe bool Compare(this SecureString secureString, string other)
		{
			// simple tests
			if (secureString == null && other == null)
				return true;
			else if (secureString == null)
				return false;
			else if (other == null)
				return false;
			else  if (secureString.Length != other.Length)
				return false;

			using (var p1 = secureString.GetPasswordHandle(true))
			{
				var c1 = (char*)p1.DangerousGetHandle();
				for (var i = 0; i < other.Length; i++)
				{
					if (*c1 != other[i])
						return false;
					c1++;
				}
			}

			return true;
		} // func Compare

		/// <summary></summary>
		/// <param name="secureString"></param>
		/// <param name="other"></param>
		/// <returns></returns>
		public static unsafe bool Compare(this SecureString secureString, SecureString other)
		{
			if (secureString == null && other == null)
				return true;
			else if (secureString == null)
				return false;
			else if (other == null)
				return false;
			else if (secureString.Length != other.Length)
				return false;

			using (var p1 = secureString.GetPasswordHandle(true))
			using (var p2 = other.GetPasswordHandle(true))
			{
				var c1 = (char*)p1.DangerousGetHandle();
				var c2 = (char*)p2.DangerousGetHandle();
				var l = secureString.Length;
				for (var i = 0; i < l; i++)
				{
					if (*c1 != *c2)
						return false;
					c1++;
					c2++;
				}
			}

			return true;
		} // func Compare

		/// <summary>Extract password a string.</summary>
		/// <param name="ss"></param>
		/// <returns></returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static string AsPlainText(this SecureString ss)
		{
			if (ss == null)
				return null;
			else if (ss.Length == 0)
				return String.Empty;
			else
			{
				var pwdPtr = Marshal.SecureStringToGlobalAllocUnicode(ss);
				try
				{
					return Marshal.PtrToStringUni(pwdPtr);
				}
				finally
				{
					Marshal.ZeroFreeGlobalAllocUnicode(pwdPtr);
				}
			}
		} // func AsPlainText

		#endregion
		
		#region -- UseNode ------------------------------------------------------------

		/// <summary>Change the current config item, to a new one, specified by the path.</summary>
		/// <param name="current">Start node.</param>
		/// <param name="path">Path to change (should not start with /).</param>
		/// <param name="offset">Offset within the path.</param>
		/// <returns></returns>
		public static DEConfigItem UseNode(DEConfigItem current, string path, int offset = 0)
		{
			if (offset >= path.Length)
				return current;
			else
			{
				var pos = path.IndexOf('/', offset);
				if (pos == offset)
					throw new FormatException($"Invalid path format (at {offset}).");
				if (pos == -1)
					pos = path.Length;

				if (pos - offset == 0) // end
					return current;
				else // find node
				{
					var currentName = path.Substring(offset, pos - offset);
					var newCurrent = current.UnsafeFind(currentName);
					if (newCurrent == null)
						throw new ArgumentOutOfRangeException(nameof(path), path, $"Path not found (at {offset}).");

					return UseNode(newCurrent, path, pos + 1);
				}
			}
		} // proc UseNode

		#endregion

		/// <summary>Exchange <c>null</c> to <c>DBNull</c>.</summary>
		/// <param name="value"></param>
		/// <returns></returns>
		public static object NullIfDBNull(this object value)
			=> value == DBNull.Value ? null : value;

		/// <summary>Compare to paths.</summary>
		/// <param name="path1"></param>
		/// <param name="path2"></param>
		/// <returns></returns>
		public static bool IsPathEqual(string path1, string path2)
			=> String.Compare(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase) == 0;

		/// <summary>Convert the object to a type-object.</summary>
		/// <param name="serviceType">Type description string, type or luatype</param>
		/// <param name="throwException">Exception if the object is not convertible.</param>
		/// <returns>Returns the type</returns>
		public static Type GetServiceType(object serviceType, bool throwException)
		{
			switch(serviceType)
			{
				case null:
					return null;
				case Type t:
					return t;
				case LuaType lt:
					return lt.Type;
				case string typeString:
					return LuaType.GetType(typeString, lateAllowed: throwException).Type;
				default:
					if (throwException)
						throw new ArgumentException(nameof(serviceType));
					return null;
			}
		} // func GetServiceType
	} // class ProcsDE

	#endregion

	#region -- class LuaArgument ------------------------------------------------------

	/// <summary>Attribute to mark arguments for the help generation.</summary>
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
	public sealed class LuaArgument : Attribute
	{
		private readonly string name;

		/// <summary>Attribute to mark arguments for the help generation.</summary>
		/// <param name="name">Name of the argument.</param>
		public LuaArgument(string name)
		{
			this.name = name;
		} // ctor

		/// <summary>Name of the argument.</summary>
		public string Name => name;
		/// <summary>Description of the argument.</summary>
		public string Description { get; set; }
	} // class LuaArgument

	#endregion
}
