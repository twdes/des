using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal static class NativeMethods
	{
		private const string csKernel32 = "kernel32.dll";
		private const string csAdvApi32 = "advapi32.dll";
		private const string csUserEnv = "userenv.dll" ;

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		public struct SERVICE_DESCRIPTION
		{
			public IntPtr description;
		}

		[StructLayout(LayoutKind.Sequential)]
		public class SECURITY_ATTRIBUTES
		{
			public int nLength = 12;
			public IntPtr lpSecurityDescriptor = IntPtr.Zero;
			public bool bInheritHandle = false;
		}

		[StructLayout(LayoutKind.Sequential)]
		public class PROCESS_INFORMATION
		{
			public IntPtr hProcess;
			public IntPtr hThread;
			public int dwProcessId;
			public int dwThreadId;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		public class STARTUPINFO : IDisposable
		{
			public Int32 cb;
			public string lpReserved;
			public string lpDesktop;
			public string lpTitle;
			public Int32 dwX;
			public Int32 dwY;
			public Int32 dwXSize;
			public Int32 dwYSize;
			public Int32 dwXCountChars;
			public Int32 dwYCountChars;
			public Int32 dwFillAttribute;
			public Int32 dwFlags;
			public Int16 wShowWindow;
			public Int16 cbReserved2;
			public IntPtr lpReserved2;
			public SafeFileHandle hStdInput = new SafeFileHandle(IntPtr.Zero, false);
			public SafeFileHandle hStdOutput =new SafeFileHandle(IntPtr.Zero, false);
			public SafeFileHandle hStdError =new SafeFileHandle(IntPtr.Zero, false);

			public STARTUPINFO()
			{
				this.cb = Marshal.SizeOf(typeof(STARTUPINFO));
			}

			public void Dispose()
			{
				Procs.FreeAndNil(ref hStdInput);
				Procs.FreeAndNil(ref hStdOutput);
				Procs.FreeAndNil(ref hStdError);
			}
		}

		public enum LOGON_TYPE
		{
			LOGON32_LOGON_INTERACTIVE = 2,
			LOGON32_LOGON_NETWORK,
			LOGON32_LOGON_BATCH,
			LOGON32_LOGON_SERVICE,
			LOGON32_LOGON_UNLOCK = 7,
			LOGON32_LOGON_NETWORK_CLEARTEXT,
			LOGON32_LOGON_NEW_CREDENTIALS
		}

		public enum LOGON_PROVIDER
		{
			LOGON32_PROVIDER_DEFAULT,
			LOGON32_PROVIDER_WINNT35,
			LOGON32_PROVIDER_WINNT40,
			LOGON32_PROVIDER_WINNT50
		}

		[Flags]
		public enum CREATE_PROCESS_FLAGS
		{
			CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
			CREATE_DEFAULT_ERROR_MODE = 0x04000000,
			CREATE_NEW_CONSOLE = 0x00000010,
			CREATE_NEW_PROCESS_GROUP = 0x00000200,
			CREATE_NO_WINDOW = 0x08000000,
			CREATE_PROTECTED_PROCESS = 0x00040000,
			CREATE_PRESERVE_CODE_AUTHZ_LEVEL = 0x02000000,
			CREATE_SEPARATE_WOW_VDM = 0x00000800,
			CREATE_SHARED_WOW_VDM = 0x00001000,
			CREATE_SUSPENDED = 0x00000004,
			CREATE_UNICODE_ENVIRONMENT = 0x00000400,
			DEBUG_ONLY_THIS_PROCESS = 0x00000002,
			DEBUG_PROCESS = 0x00000001,
			DETACHED_PROCESS = 0x00000008,
			EXTENDED_STARTUPINFO_PRESENT = 0x00080000,
			INHERIT_PARENT_AFFINITY = 0x00010000
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct PROFILEINFO
		{
			public int dwSize;
			public int dwFlags;
			[MarshalAs(UnmanagedType.LPTStr)]
			public String lpUserName;
			[MarshalAs(UnmanagedType.LPTStr)]
			public String lpProfilePath;
			[MarshalAs(UnmanagedType.LPTStr)]
			public String lpDefaultPath;
			[MarshalAs(UnmanagedType.LPTStr)]
			public String lpServerName;
			[MarshalAs(UnmanagedType.LPTStr)]
			public String lpPolicyPath;
			public IntPtr hProfile;
		}

		[DllImport(csAdvApi32, SetLastError = true, CharSet = CharSet.Auto)]
		public static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, string lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword);
		[DllImport(csAdvApi32, EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern IntPtr OpenSCManager(string machineName, string databaseName, uint dwAccess);
		[DllImport(csAdvApi32, CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern Boolean ChangeServiceConfig(IntPtr hService, int nServiceType, int nStartType, int nErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, [In] char[] lpDependencies, string lpServiceStartName, string lpPassword, string lpDisplayName);
		[DllImport(csAdvApi32, SetLastError = true, CharSet = CharSet.Auto)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool ChangeServiceConfig2(IntPtr hService, int dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);
		[DllImport(csAdvApi32, SetLastError = true, CharSet = CharSet.Auto)]
		public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);
		[DllImport(csAdvApi32, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DeleteService(IntPtr hService);
		[DllImport(csAdvApi32, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool CloseServiceHandle(IntPtr hSCObject);


		[DllImport(csAdvApi32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool LogonUser(string lpszUsername, string lpszDomain, IntPtr lpszPassword, LOGON_TYPE dwLogonType, LOGON_PROVIDER dwLogonProvider, out IntPtr phToken);
		[DllImport(csAdvApi32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, StringBuilder lpCommandLine, SECURITY_ATTRIBUTES lpProcessAttributes, SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles, CREATE_PROCESS_FLAGS dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, STARTUPINFO lpStartupInfo, PROCESS_INFORMATION lpProcessInformation);
		
		[DllImport(csKernel32, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine, SECURITY_ATTRIBUTES lpProcessAttributes, SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles, CREATE_PROCESS_FLAGS dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, STARTUPINFO lpStartupInfo, PROCESS_INFORMATION lpProcessInformation);
		[DllImport(csKernel32)]
		public static extern bool GetExitCodeProcess(SafeHandle hProcess, out uint dwExitCode);

		[DllImport(csKernel32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, int dwProcessGroupId);

		[DllImport(csKernel32)]
		public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);
		[DllImport(csKernel32, SetLastError = true)]
		public static extern IntPtr GetCurrentProcess();
		[DllImport(csKernel32, BestFitMapping = false, CharSet = CharSet.Ansi, SetLastError = true)]
		public static extern bool DuplicateHandle(HandleRef hSourceProcessHandle, SafeHandle hSourceHandle, HandleRef hTargetProcess, out SafeFileHandle targetHandle, int dwDesiredAccess, bool bInheritHandle, int dwOptions);
		[DllImport(csKernel32, CharSet = CharSet.Auto)]
		public static unsafe extern char* GetEnvironmentStrings();
		[DllImport(csKernel32, CharSet = CharSet.Auto)]
		public static unsafe extern bool FreeEnvironmentStrings(char* lpEnvironment);
		[DllImport(csKernel32)]
		public static extern bool CloseHandle(IntPtr phToken);
		[DllImport(csKernel32)]
		public static extern bool ResumeThread(IntPtr hThread);

		[DllImport(csUserEnv, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);
		[DllImport(csUserEnv, SetLastError = true, CharSet = CharSet.Unicode)]
		public static extern bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);
		[DllImport(csUserEnv, SetLastError = true, CharSet = CharSet.Unicode)]
		public static unsafe extern bool CreateEnvironmentBlock(out char* lpEnvironment, IntPtr hToken, bool lInherit);
		[DllImport(csUserEnv, SetLastError = true, CharSet = CharSet.Auto)]
		public static unsafe extern bool DestroyEnvironmentBlock(char* lpEnvironment);

	} // class NativeMethods
}
