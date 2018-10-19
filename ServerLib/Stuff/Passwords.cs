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
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace TecWare.DE.Stuff
{
	#region -- class Passwords --------------------------------------------------------

	internal static class Passwords
	{
		public static SecureString CreateSecureString(this string password)
			=> CreateSecureString(password, 0, password.Length);

		public unsafe static SecureString CreateSecureString(this string password, int offset, int length)
		{
			if (String.IsNullOrEmpty(password))
				throw new ArgumentNullException(nameof(password));

			fixed (char* c = password)
				return new SecureString(c + offset, length);
		} // func CereateSecureString

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

		#region -- Encode/Decode Password ---------------------------------------------

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct DATA_BLOB
		{
			public int DataSize;
			public IntPtr DataPtr;
		} // struct DATA_BLOB

		[DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

		[DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

		[DllImport("Kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
		private static extern void ZeroMemory(IntPtr dest, IntPtr size);
		[DllImport("Kernel32.dll", SetLastError = true)]
		private static extern IntPtr LocalFree(IntPtr handle);

		private unsafe static SecureString DecodeWindowsPassword(byte[] passwordBytes, bool forLocalMachine)
		{
			var dataOut = default(DATA_BLOB);
			var dataIV = default(DATA_BLOB);
			var hData = default(GCHandle);
			try
			{
				hData = GCHandle.Alloc(passwordBytes, GCHandleType.Pinned);
				var dataIn = new DATA_BLOB()
				{
					DataSize = passwordBytes.Length,
					DataPtr = hData.AddrOfPinnedObject()
				};

				// crypt data
				var flags = 1;
				if (forLocalMachine)
					flags |= 4;
				if (!CryptUnprotectData(ref dataIn, String.Empty, ref dataIV, IntPtr.Zero, IntPtr.Zero, flags, ref dataOut))
					throw new Win32Exception();

				// unpack data
				if (dataOut.DataPtr == IntPtr.Zero)
					throw new OutOfMemoryException();

				return new SecureString((char*)dataOut.DataPtr, dataOut.DataSize / 2);
			}
			finally
			{
				if (hData.IsAllocated)
					hData.Free();

				if (dataOut.DataPtr != IntPtr.Zero)
				{
					ZeroMemory(dataOut.DataPtr, new IntPtr(dataOut.DataSize));
					LocalFree(dataOut.DataPtr);
				}
			}
		} // func DecodeWindowsPassword

		private static byte[] EncodeWindowsPassword(IntPtr passwordPtr, int passwordSize, bool forLocalMachine)
		{
			var dataOut = default(DATA_BLOB);
			var dataIV = default(DATA_BLOB);
			try
			{
				var dataIn = new DATA_BLOB()
				{
					DataSize = passwordSize,
					DataPtr = passwordPtr
				};

				// crypt data
				var flags = 1;
				if (forLocalMachine)
					flags |= 4;
				if (!CryptProtectData(ref dataIn, String.Empty, ref dataIV, IntPtr.Zero, IntPtr.Zero, flags, ref dataOut))
					throw new Win32Exception();

				// unpack data
				if (dataOut.DataPtr == IntPtr.Zero)
					throw new OutOfMemoryException();

				var data = new byte[dataOut.DataSize];
				Marshal.Copy(dataOut.DataPtr, data, 0, dataOut.DataSize);

				return data;
			}
			finally
			{
				if (dataOut.DataPtr != IntPtr.Zero)
				{
					ZeroMemory(dataOut.DataPtr, new IntPtr(dataOut.DataSize));
					LocalFree(dataOut.DataPtr);
				}
			}
		} // func EncodeWindowsPassword

		public static SecureString DecodePassword(string passwordValue)
		{
			if (String.IsNullOrEmpty(passwordValue))
				return null;
			if (passwordValue.Length > 5 && passwordValue[5] == ':')
			{
				var pwdType = passwordValue.Substring(0, 5);
				switch (pwdType)
				{
					case "win0x":
						return DecodeWindowsPassword(Procs.ConvertToBytes(passwordValue, 6, passwordValue.Length - 6), true);
					case "win64":
						return DecodeWindowsPassword(Convert.FromBase64String(passwordValue.Substring(6, passwordValue.Length - 6)), true);
					case "usr0x":
						return DecodeWindowsPassword(Procs.ConvertToBytes(passwordValue, 6, passwordValue.Length - 6), false);
					case "usr64":
						return DecodeWindowsPassword(Convert.FromBase64String(passwordValue.Substring(6, passwordValue.Length - 6)), false);
					case "plain":
						return passwordValue.CreateSecureString(6, passwordValue.Length - 6);
					default:
						throw new ArgumentOutOfRangeException("passwordType", pwdType, "Invalid password type.");
				}
			}
			else
				return passwordValue.CreateSecureString();
		} // func DecodePassword

		public static string EncodePassword(SecureString password, string passwordType)
		{
			if (passwordType == null)
				passwordType = "win64";

			var passwordPtr = Marshal.SecureStringToGlobalAllocUnicode(password);
			var passwordSize = password.Length * 2;
			try
			{
				switch (passwordType)
				{
					case "win0x":
						return "win0x:" + Procs.ConvertToString(EncodeWindowsPassword(passwordPtr, passwordSize, true));
					case "win64":
						return "win64:" + Convert.ToBase64String(EncodeWindowsPassword(passwordPtr, passwordSize, true));
					case "usr0x":
						return "usr0x:" + Procs.ConvertToString(EncodeWindowsPassword(passwordPtr, passwordSize, false));
					case "usr64":
						return "usr64:" + Convert.ToBase64String(EncodeWindowsPassword(passwordPtr, passwordSize, false));
					case "plain":
						return "plain:" + Marshal.PtrToStringUni(passwordPtr, password.Length);
					default:
						throw new ArgumentOutOfRangeException(nameof(passwordType), passwordType, "Invalid password type.");
				}
			}
			finally
			{
				Marshal.ZeroFreeGlobalAllocUnicode(passwordPtr);
			}
		} // func EncodePassword

		#endregion

		#region -- Password Hash ------------------------------------------------------

		public static byte[] ParsePasswordHash(string passwordHash)
		{
			if (String.IsNullOrEmpty(passwordHash))
				return null;

			if (passwordHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return Procs.ConvertToBytes(passwordHash, 2, passwordHash.Length - 2);
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
			var sha = SHA512.Create();

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
