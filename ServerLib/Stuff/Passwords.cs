using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace TecWare.DE.Stuff
{
	#region -- class Passwords ----------------------------------------------------------

	internal static class Passwords
	{
		#region -- Password ---------------------------------------------------------------

		public static byte[] ParsePasswordHash(string passwordHash)
		{
			if (String.IsNullOrEmpty(passwordHash))
				return null;

			if (passwordHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				if ((passwordHash.Length & 1) != 0) // Gerade Zahl erwartet
					throw new ArgumentException("invalid hash", nameof(passwordHash));

				var hash = new byte[(passwordHash.Length >> 1) - 1];
				var i = 2;
				var j = 0;
				while (i < passwordHash.Length)
				{
					hash[j] = Byte.Parse(passwordHash.Substring(i, 2), NumberStyles.AllowHexSpecifier);
					i += 2;
					j++;
				}

				return hash;
			}
			else
				return Convert.FromBase64String(passwordHash);
		} // func ParsePasswordHash

		public static bool PasswordCompare(string testPassword, string passwordHash)
		{
			if (passwordHash == null || testPassword == null)
				return true;

			return PasswordCompare(testPassword, ParsePasswordHash(passwordHash));
		} // func PasswordCompare

		public static bool PasswordCompare(string testPassword, byte[] passwordHash)
		{
			if (passwordHash == null)
				return String.IsNullOrEmpty(testPassword);
			if (passwordHash.Length < 6)
				throw new ArgumentException("invalid hash-length", nameof(passwordHash));

			if (BitConverter.ToInt16(passwordHash, 0) != 2)
				throw new ArgumentException("invalid hash-version", nameof(passwordHash));

			var testPasswordBytes = Encoding.Unicode.GetBytes(testPassword);

			// create the SHA256 hash (Password + Salt)
			var sha = SHA512Managed.Create();
			sha.TransformBlock(testPasswordBytes, 0, testPasswordBytes.Length, testPasswordBytes, 0);
			sha.TransformFinalBlock(passwordHash, 2, 4);

			return Procs.CompareBytes(sha.Hash, 0, passwordHash, 6, sha.HashSize / 8);
		} // func PasswordCompare

		public unsafe static byte[] HashPassword(string password)
		{
			var passwordPtr = Marshal.StringToHGlobalUni(password);
			try
			{
				return HashPassword(passwordPtr, password.Length, new Random().Next());
			}
			finally
			{
				Marshal.ZeroFreeGlobalAllocUnicode(passwordPtr);
			}
		} // func HashPassword

		public unsafe static byte[] HashPassword(SecureString password)
		{
			var passwordPtr = Marshal.SecureStringToGlobalAllocUnicode(password);
			try
			{
				return HashPassword(passwordPtr, password.Length, new Random().Next());
			}
			finally
			{
				Marshal.ZeroFreeGlobalAllocUnicode(passwordPtr);
			}
		} // func HashPassword

		public unsafe static byte[] HashPassword(IntPtr passwordPtr, int length, int salt)
		{
			var c = (char*)passwordPtr.ToPointer();

			// create hash function
			var sha = SHA512Managed.Create();

			var b = new byte[2];
			var i = 0;

			while (i < length)
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

			b = BitConverter.GetBytes(salt);
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
