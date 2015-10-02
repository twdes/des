using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace TecWare.DE.Stuff
{
	#region -- class Passwords ----------------------------------------------------------

	internal static class Passwords
	{
		private const string hexAlpha = "0123456789ABCDEF";

		#region -- Password ---------------------------------------------------------------

		public static bool PasswordCompare(string testPassword, string passwordHash)
		{
			if (passwordHash == null || testPassword == null)
				return true;

			if (passwordHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				if ((passwordHash.Length & 1) != 0) // Gerade Zahl erwartet
					throw new ArgumentException("invalid hash");

				var hash = new byte[(passwordHash.Length / 2) - 1];
				var i = 2;
				var j = 0;
				while (i < passwordHash.Length)
				{
					var t = hexAlpha.IndexOf(Char.ToUpper(passwordHash[i]));
					if (t == -1)
						throw new ArgumentException("invalid hash");
					hash[j] = (byte)(t << 4);
					i++;
					t = hexAlpha.IndexOf(passwordHash[i]);
					if (t == -1)
						throw new ArgumentException("invalid hash");
					hash[j] = (byte)(hash[j] | t);
					i++;
					j++;
				}

				return PasswordCompare(testPassword, hash);
			}
			else
				return PasswordCompare(testPassword, Convert.FromBase64String(passwordHash));
		} // func PasswordCompare

		public static bool PasswordCompare(string testPassword, byte[] passwordHash)
		{
			if (passwordHash == null)
				return String.IsNullOrEmpty(testPassword);
			if (passwordHash.Length < 6)
				throw new ArgumentException("invalid hash-length");

			if (BitConverter.ToInt16(passwordHash, 0) != 2)
				throw new ArgumentException("invalid hash-version");

			var testPasswordBytes = Encoding.Unicode.GetBytes(testPassword);

			// Errechne den SHA256 hash (Password + Salt)
			var sha = SHA512Managed.Create();
			sha.TransformBlock(testPasswordBytes, 0, testPasswordBytes.Length, testPasswordBytes, 0);
			sha.TransformFinalBlock(passwordHash, 2, 4);

			return Procs.CompareBytes(sha.Hash, 0, passwordHash, 6, sha.HashSize / 8);
		} // func PasswordCompare

		public unsafe static byte[] HashPassword(string password)
		{
			var pPassword = Marshal.StringToHGlobalUni(password);
			try
			{
				return HashPassword(pPassword, password.Length, new Random().Next());
			}
			finally
			{
				Marshal.ZeroFreeGlobalAllocUnicode(pPassword);
			}
		} // func HashPassword

		public unsafe static byte[] HashPassword(SecureString password)
		{
			var pPassword = Marshal.SecureStringToGlobalAllocUnicode(password);
			try
			{
				return HashPassword(pPassword, password.Length, new Random().Next());
			}
			finally
			{
				Marshal.ZeroFreeGlobalAllocUnicode(pPassword);
			}
		} // func HashPassword

		public unsafe static byte[] HashPassword(IntPtr pPassword, int iLength, int iSalt)
		{
			char* c = (char*)pPassword.ToPointer();

			// Errechne den Hash-Wert
			var sha = SHA512Managed.Create();

			var b = new byte[2];
			var i = 0;

			while (i < iLength)
			{
				unchecked
				{
					b[0] = (byte)(short)*c;
					b[1] = (byte)((short)*c >> 8);
				}
				sha.TransformBlock(b, 0, 2, b, 0);
				c++;
				i++;
			}

			b = BitConverter.GetBytes(iSalt);
			sha.TransformFinalBlock(b, 0, 4);

			// Erzeuge Salt+Hash
			var r = new byte[sha.HashSize / 8 + 6];
			r[0] = 2;
			r[1] = 0;
			Array.Copy(b, 0, r, 2, 4);
			Array.Copy(sha.Hash, 0, r, 6, sha.HashSize / 8);

			return r;
		} // func HashPassword

		#endregion
	} // class Passwords

	#endregion
}
