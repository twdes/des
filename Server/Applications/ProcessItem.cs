using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEProcessItem : DEConfigLogItem
	{
		public const string ProcessCategory = "Process";

		private static readonly XName xnArguments = DEConfigurationConstants.MainNamespace + "arguments";
		private static readonly XName xnEnv =  DEConfigurationConstants.MainNamespace + "env";
		private static readonly XName xnKill = DEConfigurationConstants.MainNamespace + "kill";

		#region -- class ProcessWaitHandle ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class ProcessWaitHandle : WaitHandle
		{
			public ProcessWaitHandle(IntPtr hProcess)
			{
				this.SafeWaitHandle = new SafeWaitHandle(hProcess, false);
			} // ctor
		} // class ProcessWaitHandle

		#endregion

		private Encoding outputEncoding = null; // Encoding für die Ausgaben der Anwendung
		private Encoding inputEncoding = null;  // Encoding für Input-Texte

		private Process process = null;         // Aktuell laufender Prozess
		private IntPtr hUser = IntPtr.Zero;
		private IntPtr hProfile = IntPtr.Zero;
		private ProcessWaitHandle exitWaitHandle = null;
		private RegisteredWaitHandle waitHandle = null;
		private StreamWriter inputStream = null;
		private StreamReader outputStream = null;
		private StreamReader errorStream = null;
		private IAsyncResult arOutputStream = null;
		private IAsyncResult arErrorStream = null;
		private Action<LogMsgType, StreamReader> procProcessLogLine;

		#region -- Ctor/Dtor/Config -------------------------------------------------------

		public DEProcessItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
			procProcessLogLine = ProcessReceiveLine;

			PublishItem(new DEConfigItemPublicAction("processStart") { DisplayName = "Start" });
			PublishItem(new DEConfigItemPublicAction("processStop") { DisplayName = "Stop" });

			//processId = new SimpleConfigItemProperty<int?>(this, "tw_process_id", "Id", ProcessCategory, "Gets the unique identifier for the associated process.", "N0", null);
    } // ctor

		//[
		//DEConfigHttpAction("process"),
		//Description("Gibt die Prozesseigenschaften zurück.")
		//]
		//private XElement HttpRefreshAction(int typ = 0)
		//{
		//	if (typ == 1)
		//		HttpStartAction();
		//	else if (typ == 2)
		//		HttpStopAction();

		//	XElement r = new XElement("process");
		//	if (IsProcessRunning)
		//	{
		//		Process p = process;
		//		r.Add(
		//			new XElement("id", p.Id.ToString("N0")),
		//			new XElement("mem_paged", Procs.FormatFileSize(p.PagedMemorySize64)),
		//			new XElement("mem_sys_paged", Procs.FormatFileSize(p.PagedSystemMemorySize64)),
		//			new XElement("mem_sys_nonpaged", Procs.FormatFileSize(p.NonpagedSystemMemorySize64)),
		//			new XElement("mem_peak_paged", Procs.FormatFileSize(p.PeakPagedMemorySize64)),
		//			new XElement("mem_private", Procs.FormatFileSize(p.PrivateMemorySize64)),
		//			new XElement("mem_virtual", Procs.FormatFileSize(p.VirtualMemorySize64)),
		//			new XElement("mem_peak_virtual", Procs.FormatFileSize(p.PeakVirtualMemorySize64)),
		//			new XElement("mem_workingset", Procs.FormatFileSize(p.WorkingSet64)),
		//			new XElement("mem_peak_workingset", Procs.FormatFileSize(p.PeakWorkingSet64)),
		//			new XElement("mem_handles", p.HandleCount.ToString("N0")),
		//			new XElement("start", p.StartTime.ToString("G")),
		//			new XElement("time_total", p.TotalProcessorTime.ToString()),
		//			new XElement("time_user", p.UserProcessorTime.ToString()),
		//			new XElement("time_priv", p.PrivilegedProcessorTime.ToString())
		//		);
		//	}
		//	else
		//	{
		//		r.Add(
		//			new XElement("id", "Läuft nicht"),
		//			new XElement("session", String.Empty),
		//			new XElement("mem_paged", String.Empty),
		//			new XElement("mem_sys_paged", String.Empty),
		//			new XElement("mem_sys_nonpaged", String.Empty),
		//			new XElement("mem_peak_paged", String.Empty),
		//			new XElement("mem_private", String.Empty),
		//			new XElement("mem_virtual", String.Empty),
		//			new XElement("mem_peak_virtual", String.Empty),
		//			new XElement("mem_workingset", String.Empty),
		//			new XElement("mem_peak_workingset", String.Empty),
		//			new XElement("mem_handles", String.Empty),
		//			new XElement("start", String.Empty),
		//			new XElement("time_total", String.Empty),
		//			new XElement("time_user", String.Empty),
		//			new XElement("time_priv", String.Empty)
		//		);
		//	}
		//	return r;
		//} // proc HttpStartAction

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (IsProcessRunning)
					StopProcess();

				//Procs.FreeAndNil(ref processId);
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			if (Config.GetAttribute("autostart", false))
			{
				if (IsProcessRunning)
				{
					if (Config.GetAttribute("configrestart", false))
					{
						StopProcess();
						StartProcess();
					}
				}
				else
					StartProcess();
			}
		} // proc OnEnReadConfiguration

		#endregion

		#region -- Http -------------------------------------------------------------------

		[
		DEConfigHttpAction("processStart", IsSafeCall = true),
		Description("Startet den Prozess.")
		]
		private void HttpStartAction()
		{
			if (!IsProcessRunning && !StartProcess())
				throw new Exception("Prozess nicht gestartet.");
		} // proc HttpStartAction

		[
		DEConfigHttpAction("processStop", IsSafeCall = true),
		Description("Stoppt den Prozess.")
		]
		private void HttpStopAction()
		{
			if (IsProcessRunning)
				StopProcess();
		} // proc HttpStartAction

		[
		DEConfigHttpAction("processSend", IsSafeCall = true),
		Description("Sendet eine Zeichenfolge an die Anwendung")
		]
		private void HttpSendAction(string cmd)
		{
			inputStream.WriteLine(cmd);
		} // proc HttpSendAction

		#endregion

		#region -- Start/Stop Process -------------------------------------------------------

		private unsafe char[] CreateEnvironment(IntPtr hToken, string userName, bool loadProfile)
		{
			char[] r;
			char* pEnv;

			if (hToken == IntPtr.Zero)
			{
				pEnv = NativeMethods.GetEnvironmentStrings();
				if (pEnv == null)
					throw new Win32Exception();
			}
			else
			{
				if (loadProfile)
				{
					var profileInfo = new NativeMethods.PROFILEINFO();
					profileInfo.dwSize = Marshal.SizeOf(typeof(NativeMethods.PROFILEINFO));
					profileInfo.lpUserName = userName;
					if (!NativeMethods.LoadUserProfile(hToken, ref profileInfo))
						throw new Win32Exception();
					hProfile = profileInfo.hProfile;
				}
				else
					hProfile = IntPtr.Zero;

				if (!NativeMethods.CreateEnvironmentBlock(out pEnv, loadProfile ? hToken : IntPtr.Zero, false))
					throw new Win32Exception();
			}

			try
			{
				// Suche das Ende im Environment
				var envLength = 0;
				char* c = pEnv;
				while (*c != '\0' || *(c + 1) != '\0')
				{
					envLength++;
					c++;
				}
				envLength++;

				// Erzeuge die Zusätze 
				var sbEnvAdd = new StringBuilder();
				foreach (var env in Config.Elements(xnEnv))
				{
					var key = env.GetAttribute("key", String.Empty);
					if (!String.IsNullOrEmpty(key))
						sbEnvAdd.Append(key).Append('=').Append(env.Value).Append('\0');
				}

				// Kopiere das Env
				r = new char[envLength + sbEnvAdd.Length + 1];
				Marshal.Copy(new IntPtr(pEnv), r, 0, envLength);
				sbEnvAdd.CopyTo(0, r, envLength, sbEnvAdd.Length);
				r[r.Length - 1] = '\0';

				return r;
			}
			finally
			{
				// Zerstöre den Block wieder
				if (hToken == IntPtr.Zero)
					NativeMethods.FreeEnvironmentStrings(pEnv);
				else
					NativeMethods.DestroyEnvironmentBlock(pEnv);
			}
		} // func CreateEnvironment

		private void CreateProcessPipe(bool isInput, out SafeFileHandle parent, out SafeFileHandle child)
		{
			var securityAttributes = new NativeMethods.SECURITY_ATTRIBUTES();
			securityAttributes.bInheritHandle = true;

			SafeFileHandle hFile = null;
			try
			{
				bool lRet;
				if (isInput)
					lRet = NativeMethods.CreatePipe(out child, out hFile, securityAttributes, 0);
				else
					lRet = NativeMethods.CreatePipe(out hFile, out child, securityAttributes, 0);

				if (!lRet || child.IsInvalid || hFile.IsInvalid)
					throw new Win32Exception();

				var p = new HandleRef(this, NativeMethods.GetCurrentProcess());
				if (!NativeMethods.DuplicateHandle(p, hFile, p, out parent, 0, false, 2))
					throw new Win32Exception();
			}
			finally
			{
				if (hFile != null && !hFile.IsInvalid)
					hFile.Close();
			}
		} // proc CreateProcessPipe

		private bool StartProcess()
		{
			try
			{
				if (IsProcessRunning)
					StopProcess();

				Log.LogMsg(LogMsgType.Information, "Starte anwendung...");
				if (inputEncoding != null)
					Console.InputEncoding = inputEncoding;

				SafeFileHandle hInput = null;
				SafeFileHandle hOutput = null;
				SafeFileHandle hError = null;
				var hEnvironment = default(GCHandle);
				using (var startupInfo = new NativeMethods.STARTUPINFO())
					try
					{
						// Erzeuge die Befehlszeile
						var sbCommand = new StringBuilder();

						var fileName = Config.GetAttribute("filename", String.Empty);
						if (!String.IsNullOrEmpty(fileName))
						{
							if (fileName.Length > 0 && fileName[0] == '"' && fileName[fileName.Length] == '"')
								sbCommand.Append(fileName);
							else
								sbCommand.Append('"').Append(fileName).Append('"');
						}

						string sArguments = Config.GetAttribute("arguments", String.Empty);
						if (!String.IsNullOrEmpty(sArguments))
							sbCommand.Append(' ').Append(sArguments);
						XElement arguments = Config.Element(xnArguments);
						if (arguments != null && !String.IsNullOrEmpty(arguments.Value))
							sbCommand.Append(' ').Append(arguments.Value);

						if (sbCommand.Length == 0)
							throw new ArgumentException("filename", "Keine Datei angegeben.");

						// Working-Directory
						var workingDirectory = Config.GetAttribute("workingDirectory", null);

						// Soll der Prozess unter einem anderen Nutzer ausgeführt werden
						var domain = Config.GetAttribute("domain", null);
						var userName = Config.GetAttribute("username", null);
						var password = Config.GetAttribute("password", null);
						if (!String.IsNullOrEmpty(userName))
						{
							if (!NativeMethods.LogonUser(userName, domain, password, Environment.UserInteractive ? NativeMethods.LOGON_TYPE.LOGON32_LOGON_INTERACTIVE : NativeMethods.LOGON_TYPE.LOGON32_LOGON_SERVICE, NativeMethods.LOGON_PROVIDER.LOGON32_PROVIDER_DEFAULT, out hUser))
								throw new Win32Exception();
						}

						// Erzeuge eine Environment für den Prozess
						hEnvironment = GCHandle.Alloc(CreateEnvironment(hUser, userName, Config.GetAttribute("loadUserProfile", false)), GCHandleType.Pinned);

						// Flags für den neuen Prozess
						var flags = NativeMethods.CREATE_PROCESS_FLAGS.CREATE_NEW_PROCESS_GROUP | NativeMethods.CREATE_PROCESS_FLAGS.CREATE_NO_WINDOW | NativeMethods.CREATE_PROCESS_FLAGS.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_PROCESS_FLAGS.CREATE_SUSPENDED;

						// Erzeuge die Startparameter
						startupInfo.dwFlags = 0x00000100;
						CreateProcessPipe(true, out hInput, out startupInfo.hStdInput);
						CreateProcessPipe(false, out hOutput, out startupInfo.hStdOutput);
						CreateProcessPipe(false, out hError, out startupInfo.hStdError);

						NativeMethods.PROCESS_INFORMATION processinformation = new NativeMethods.PROCESS_INFORMATION();
						try
						{
							if (hUser == IntPtr.Zero)
							{
								if (!NativeMethods.CreateProcess(null, sbCommand, null, null, true, flags, hEnvironment.AddrOfPinnedObject(), workingDirectory, startupInfo, processinformation))
									throw new Win32Exception();
							}
							else
							{
								if (!NativeMethods.CreateProcessAsUser(hUser, null, sbCommand, null, null, true, flags, hEnvironment.AddrOfPinnedObject(), workingDirectory, startupInfo, processinformation))
									throw new Win32Exception();
							}

							// Erzeuge das .net Prozess-Objekt
							process = Process.GetProcessById(processinformation.dwProcessId);

							// Erzeuge die Pipes
							inputStream = new StreamWriter(new FileStream(hInput, FileAccess.Write, 4096, false), inputEncoding ?? Console.InputEncoding);
							inputStream.AutoFlush = true;
							outputStream = new StreamReader(new FileStream(hOutput, FileAccess.Read, 4096, false), outputEncoding ?? Console.OutputEncoding);
							errorStream = new StreamReader(new FileStream(hError, FileAccess.Read, 4096, false), outputEncoding ?? Console.OutputEncoding);

							exitWaitHandle = new ProcessWaitHandle(processinformation.hProcess);
							waitHandle = ThreadPool.RegisterWaitForSingleObject(exitWaitHandle, ProcessExited, process, -1, true);

							arOutputStream = procProcessLogLine.BeginInvoke(LogMsgType.Information, outputStream, null, outputStream);
							arErrorStream = procProcessLogLine.BeginInvoke(LogMsgType.Warning, errorStream, null, errorStream);

							// Starte die Anwendung
							NativeMethods.ResumeThread(processinformation.hThread);
						}
						finally
						{
							NativeMethods.CloseHandle(processinformation.hThread);
						}
					}
					catch
					{
						if (hUser != IntPtr.Zero)
							NativeMethods.CloseHandle(hUser);
						if (hInput != null && !hInput.IsInvalid)
							hInput.Close();
						if (hOutput != null && !hOutput.IsInvalid)
							hOutput.Close();
						if (hError != null && !hError.IsInvalid)
							hError.Close();

						throw;
					}
					finally
					{
						if (hEnvironment.IsAllocated)
							hEnvironment.Free();
					}

				CallMemberDirect("ProcessStarted", new object[] { process }, lThrowExceptions: false);

				return true;
			}
			catch (Exception e)
			{
				Log.LogMsg(LogMsgType.Error, e.GetMessageString());
				return false;
			}
		} // proc StartProcess

		private bool SendKillCommand()
		{
			var kill = Config.Element(xnKill);
			if (kill == null)
				return false;

			try
			{
				inputStream.WriteLine(kill.Value);
				return true;
			}
			catch (Exception e)
			{
				Debug.Print(e.GetMessageString());
				return false;
			}
		} // func SendKillCommand

		private void StopProcess()
		{
			const string csSendKillCommand = "SendKillCommand";
			try
			{
				bool lCommandSended = SendKillCommand();

				// Prüfe ob das Script ne Idee hat
				if (!lCommandSended)
				{
					object sendKillCommand = this[csSendKillCommand];
					if (sendKillCommand != null)
						lCommandSended = (bool)Lua.RtConvertValue(CallMemberDirect(csSendKillCommand, LuaResult.Empty.Values, lThrowExceptions: false), typeof(bool));
				}

				//// Ctrl+C - muss via CreateRemoteThread ausgeführt werden!
				//if (!lCommandSended)
				//{
				//	NativeMethods.GenerateConsoleCtrlEvent(0, process.Id);
				//	lCommandSended = true;
				//}

				// Warte auf das Ende
				if (!process.WaitForExit(lCommandSended ? Config.GetAttribute("exitTimeout", 3000) : 100))
				{
					Log.LogMsg(LogMsgType.Warning, "Prozess wird abgeschossen.");
					process.Kill();
				}
			}
			catch (Exception e)
			{
				Log.Except(e);
			}
		} // proc StopProcess

		#endregion

		private void ProcessLogLine(LogMsgType type, string line)
		{
			if (this["ProcessLine"] != null)
			{
				var r = CallMemberDirect("ProcessLine", new object[] { type, line }, lThrowExceptions: false);
				var tmp = r[0];
				if (tmp != null && tmp is string)
					line = (string)tmp;

				tmp = r[1];
				if (tmp is LogMsgType)
					type = (LogMsgType)tmp;
				else if (tmp is int)
					type = (LogMsgType)(int)tmp;
			}
			if (!String.IsNullOrEmpty(line))
				Log.LogMsg(type, line);
		} // proc ProcessLogLine

		private void ProcessReceiveLine(LogMsgType type, StreamReader sr)
		{
			string line;
			while ((line = sr.ReadLine()) != null)
				try
				{
					ProcessLogLine(LogMsgType.Information, line);
				}
				catch (Exception e)
				{
					Log.Except(e);
				}
		} // proc ProcessReceiveLine

		private void ProcessExited(object state, bool timeout)
		{
			uint exitCode;
			if (!NativeMethods.GetExitCodeProcess(exitWaitHandle.SafeWaitHandle, out exitCode))
				exitCode = uint.MaxValue;

			try
			{
				Log.LogMsg(LogMsgType.Information, "Prozess beendet (ExitCode={0})", unchecked((int)exitCode));

				if (this["ProcessStopped"] != null)
					CallMemberDirect("ProcessStopped", new object[] { (Process)state }, lThrowExceptions: false);
			}
			finally
			{
				Procs.FreeAndNil(ref exitWaitHandle);
				waitHandle = null;

				if (hUser != IntPtr.Zero)
				{
					Debug.Print("UnloadProfile={0}", NativeMethods.UnloadUserProfile(hUser, hProfile));
					Debug.Print("CloseUserHandle={0}", NativeMethods.CloseHandle(hUser));
				}

				if (arOutputStream != null)
					procProcessLogLine.EndInvoke(arOutputStream);
				if (arErrorStream != null)
					procProcessLogLine.EndInvoke(arErrorStream);

				arOutputStream = null;
				arErrorStream = null;

				Procs.FreeAndNil(ref inputStream);
				Procs.FreeAndNil(ref outputStream);
				Procs.FreeAndNil(ref errorStream);

				process = null;
			}
		} // proc ProcessExited

		/// <summary>Zugriff auf den Prozess</summary>
		public Process Process => process;
		/// <summary>Läuft der Prozess noch.</summary>
		public bool IsProcessRunning => process != null && !process.WaitForExit(0);
	} // class DEProcessItem
}
