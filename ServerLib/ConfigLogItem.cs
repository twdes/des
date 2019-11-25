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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DELogLine --------------------------------------------------------

	/// <summary>Holds a log line.</summary>
	public sealed class DELogLine
	{
		/// <summary>Creates</summary>
		/// <param name="dataLine"></param>
		public DELogLine(string dataLine)
		{
			LogLineParser.Parse(dataLine, out var typ, out var stamp, out var text);

			this.Stamp = stamp;
			this.Typ = typ;
			this.Text = text;
		} // ctor

		/// <summary>Creates a log line from the given values.</summary>
		/// <param name="stamp"></param>
		/// <param name="typ"></param>
		/// <param name="text"></param>
		public DELogLine(DateTime stamp, LogMsgType typ, string text)
		{
			this.Stamp = stamp;
			this.Typ = typ;
			this.Text = text ?? String.Empty;
		} // ctor

		/// <summary></summary>
		/// <returns></returns>
		public override string ToString()
			=> ToLineData();

		/// <summary></summary>
		/// <returns></returns>
		public string ToLineData()
		{
			var sb = new StringBuilder(Text?.Length ?? +64); // reserve space for the string

			sb.Append(LogLineParser.ConvertDateTime(Stamp))
				.Append('\t')
				.Append(((int)Typ).ToString())
				.Append('\t');

			foreach (var c in Text)
			{
				switch (c)
				{
					case '\n':
						sb.Append("\\n");
						break;
					case '\r':
						break;
					case '\t':
						sb.Append("\\t");
						break;
					case '\\':
						sb.Append(@"\\");
						break;
					case '\0':
						sb.Append("\\0");
						break;
					default:
						sb.Append(c);
						break;
				}
			}

			return sb.ToString();
		} // func GetLineData

		/// <summary>Time of the event.</summary>
		public DateTime Stamp { get; }
		/// <summary>Classification of the event.</summary>
		public LogMsgType Typ { get; }
		/// <summary>Content</summary>
		public string Text { get; }
	} // class DELogLine

	#endregion

	#region -- class DELogFile --------------------------------------------------------

	/// <summary>Access to a LogFile.</summary>
	public sealed class DELogFile : IDERangeEnumerable2<DELogLine>, IDisposable
	{
		private const string windowsLineEnding = "\r\n";

		/// <summary></summary>
		public event EventHandler LinesAdded;

		private readonly byte[] logFileBuffer = new byte[0x10000];

		private readonly object logFileLock = new object(); // Lock for multithread access
		private readonly FileStream logData;                // File, that contains the lines
		private bool isDisposed = false;

		private int logLineCount = 0;                           // Is the current count of log lines
		private readonly List<long> linesOffsetCache = new List<long>(); // Offset of line >> 5 (32)

		#region -- Ctor/Dtor ----------------------------------------------------------

		/// <summary></summary>
		/// <param name="fileName"></param>
		public DELogFile(string fileName)
		{
			var fileInfo = new FileInfo(fileName);
			if (!fileInfo.Exists) // if not exists --> create the log file
			{
				// creat the directory
				if (!fileInfo.Directory.Exists)
					fileInfo.Directory.Create();

				// Create a empty file, that we can set read access for all processes.
				File.WriteAllBytes(fileInfo.FullName, new byte[0]);
			}

			// Open the file
			logData = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			using (var started = new ManualResetEventSlim(false))
			{
				// Use different schedule, to fork from the thread queue
				Task.Factory.StartNew(() => CreateLogLineCache(started), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
				started.Wait();
			}
		} // ctor

		/// <summary></summary>
		public void Dispose()
		{
			lock (logFileLock)
			{
				CheckDisposed();

				logData?.Dispose();
				isDisposed = true;
			}
		} // proc Dispose

		private void CheckDisposed()
		{
			if (isDisposed)
				throw new ObjectDisposedException(nameof(DELogFile));
		} // proc CheckDisposed

		#endregion

		#region -- Add, SetSize -------------------------------------------------------

		/// <summary></summary>
		/// <param name="minSize"></param>
		/// <param name="maxSize"></param>
		public void SetSize(uint minSize, uint maxSize)
		{
			lock (logFileLock)
			{
				MinimumSize = Math.Min(minSize, maxSize);
				MaximumSize = Math.Max(maxSize, minSize);
			}
		} // func SetSize

		/// <summary></summary>
		/// <param name="line"></param>
		public void Add(DELogLine line)
		{
			var lineData = Encoding.Default.GetBytes(line.ToLineData() + windowsLineEnding);
			lock (logFileLock)
			{
				if (isDisposed) // check if the log is disposed
					return;

				try
				{
					if (logData.Length + lineData.Length > MaximumSize)
						TruncateLog(MinimumSize); // truncate the log file, calculate the new size

					// set the position to the end and mark the offset
					logData.Seek(0, SeekOrigin.End);
					AddToLogLineCache(logData.Position);
					logLineCount++;

					// add the line data
					logData.Write(lineData, 0, lineData.Length);
					logData.Flush();
				}
				catch (Exception e)
				{
					Debug.Print(e.GetMessageString());
					ResetLog();
				}
			}

			LinesAdded?.Invoke(this, EventArgs.Empty);
		} // proc Add

		private void CreateLogLineCache(ManualResetEventSlim started)
		{
			lock (logFileLock)
			{
				started.Set();
				try
				{
					int readed;
					long lastLinePosition = 0;

					// reset data
					ResetLog();

					do
					{
						readed = logData.Read(logFileBuffer, 0, logFileBuffer.Length);

						for (var i = 0; i < readed; i++)
						{
							if (logFileBuffer[i] == '\n')
							{
								AddToLogLineCache(lastLinePosition);
								logLineCount++;

								lastLinePosition = logData.Position - readed + i + 1;
							}
						}
					} while (readed > 0);
				}
				catch (Exception e)
				{
					Debug.Print(e.GetMessageString());
					ResetLog(true);
				}
			}

			if (logLineCount != 0)
				LinesAdded?.Invoke(this, EventArgs.Empty);
		} // proc CreateLogLineCache

		private void TruncateLog(long newSize)
		{
			// search for the line position (es wird 32 Zeilenweise entfernt)
			var indexRemove = linesOffsetCache.BinarySearch(newSize);
			if (indexRemove == -1) // before first
				indexRemove = 0;
			else if (indexRemove < -1) // within a block
				indexRemove = ~indexRemove - 1; // not lower the minimum

			// correct the remove byte to the line ending
			var removeBytes = indexRemove < linesOffsetCache.Count ? linesOffsetCache[indexRemove] : logData.Length;

			// copy the log data to the start of the file
			var readPos = removeBytes;
			var writePos = 0;
			while (logData.Length > readPos)
			{
				logData.Seek(readPos, SeekOrigin.Begin);
				var readed = logData.Read(logFileBuffer, 0, logFileBuffer.Length);
				logData.Seek(writePos, SeekOrigin.Begin);
				logData.Write(logFileBuffer, 0, readed);

				readPos += readed;
				writePos += readed;
			}

			// correct the line cache
			linesOffsetCache.RemoveRange(0, indexRemove);
			logLineCount -= indexRemove << 5;
			for (var i = 0; i < linesOffsetCache.Count; i++)
				linesOffsetCache[i] = linesOffsetCache[i] - removeBytes;

			// truncate the bytes
			logData.SetLength(logData.Position);
		} // proc TruncateLog

		private void ResetLog(bool clearData = false)
		{
			// try to reset data, should not happen on real life
			logData.Position = 0;
			logLineCount = 0;
			linesOffsetCache.Clear();

			// clear log, to make the data valid
			if (clearData)
				logData.SetLength(0);
		} // proc ResetLog

		private void AddToLogLineCache(long position)
		{
			if ((logLineCount & 0x1F) == 0)
			{
				var index = logLineCount >> 5;
				if (index != linesOffsetCache.Count)
					throw new InvalidDataException();

				linesOffsetCache.Add(position);
			}
		} // proc AddToLogLineCache

		#endregion

		#region -- GetEnumerator ------------------------------------------------------

		/// <summary></summary>
		/// <returns></returns>
		public IEnumerator GetEnumerator()
			=> GetEnumerator(0, Int32.MaxValue, null);

		/// <summary></summary>
		/// <param name="start"></param>
		/// <param name="count"></param>
		/// <returns></returns>
		public IEnumerator<DELogLine> GetEnumerator(int start, int count)
			=> GetEnumerator(start, count, null);

		/// <summary></summary>
		/// <param name="start"></param>
		/// <param name="count"></param>
		/// <param name="selector"></param>
		/// <returns></returns>
		public IEnumerator<DELogLine> GetEnumerator(int start, int count, IPropertyReadOnlyDictionary selector)
		{
			// create selector
			if (selector != null)
			{
				//throw new NotImplementedException();
			}

			// find the range to search in
			var idxFrom = start >> 5;
			var idxFromOffset = start & 0x1F;

			string lineData;
			var i = 0;
			logData.Seek(linesOffsetCache[idxFrom], SeekOrigin.Begin);
			using (var sr = new StreamReader(logData, Encoding.Default, false, 4096, true))
			{
				while (i < count && (lineData = sr.ReadLine()) != null)
				{
					if (idxFromOffset > 0)
						idxFromOffset--;
					else
					{


						yield return new DELogLine(lineData);
						i++;
					}
				}
			}
		} // func GetEnumerator

		#endregion

		/// <summary>Returns the name of the log-file.</summary>
		public string FileName => logData.Name;
		/// <summary>Returns the synchronization object for the file.</summary>
		public object SyncRoot => logFileLock;

		/// <summary>Size of the file to truncate.</summary>
		public uint MinimumSize { get; private set; } = 3 << 20;
		/// <summary>Size of the file, when the truncate will start.</summary>
		public uint MaximumSize { get; private set; } = 4 << 20;
		/// <summary>Total size of the file</summary>
		public uint CurrentSize => unchecked((uint)logData.Length);

		/// <summary>Number of log lines.</summary>
		public int Count { get { lock (logFileLock) return logLineCount; } }
	} // class DELogFile

	#endregion

	#region -- class DEConfigLogItem --------------------------------------------------

	/// <summary>Configuration node with log line.</summary>
	public class DEConfigLogItem : DEConfigItem, ILogger, ILogger2
	{
		/// <summary></summary>
		public const string LogCategory = "Log";

		#region -- class LogLineDescriptor --------------------------------------------

		/// <summary>Beschreibt die Logzeilen</summary>
		private sealed class LogLineDescriptor : IDEListDescriptor
		{
			private LogLineDescriptor()
			{
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType("line");
				xml.WriteProperty("@stamp", typeof(DateTime));
				xml.WriteProperty("@typ", typeof(string));
				xml.WriteProperty(".", typeof(string));
				xml.WriteEndType();
			} // proc WriteType

			private string GetLogLineType(LogMsgType typ)
			{
				switch (typ)
				{
					case LogMsgType.Error:
						return "E";
					case LogMsgType.Information:
						return "I";
					case LogMsgType.Warning:
						return "W";
					default:
						return "";
				}
			} // func GetLogLineType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				var logLine = (DELogLine)item;
				xml.WriteStartProperty("line");
				xml.WriteAttributeProperty("stamp", logLine.Stamp.ToString("O"));
				xml.WriteAttributeProperty("typ", GetLogLineType(logLine.Typ));
				xml.WriteValue(logLine.Text);
				xml.WriteEndProperty();
			} // proc WriteItem

			public static LogLineDescriptor Instance { get; } = new LogLineDescriptor();
		} // class LogLineDescriptor

		#endregion

		#region -- class LogLineController --------------------------------------------

		private sealed class LogLineController : IDEListController
		{
			private readonly DEConfigLogItem configItem;

			public LogLineController(DEConfigLogItem configItem)
			{
				this.configItem = configItem ?? throw new ArgumentNullException(nameof(configItem));
			} // ctor

			public void Dispose()
			{
			} // proc Dispose

			public IDisposable EnterReadLock()
			{
				Monitor.Enter(configItem.logFile.SyncRoot);
				return new DisposableScope(() => Monitor.Exit(configItem.logFile.SyncRoot));
			} // func EnterReadLock

			public IDisposable EnterWriteLock()
				=> throw new NotSupportedException();

			public void OnBeforeList() { }

			public string Id => LogLineListId;
			public string DisplayName => LogCategory;
			public string SecurityToken => SecuritySys;
			public IEnumerable List => configItem.logFile;
			public IDEListDescriptor Descriptor => LogLineDescriptor.Instance;
		} // class LogLineController

		#endregion

		#region -- class LogMessageScopeHolder ----------------------------------------

		private sealed class LogMessageScopeHolder : ILogMessageScope
		{
			private readonly DEConfigLogItem owner;
			private readonly LogMessageScopeFrame frame;

			public LogMessageScopeHolder(DEConfigLogItem owner, LogMessageScopeFrame frame)
			{
				this.owner = owner;
				this.frame = frame;

				lock (SyncRoot)
					frame.LogMessageScopeCounter++;
			} // ctor

			public void Dispose()
			{
				lock (SyncRoot)
				{
					if (frame.LogMessageScopeCounter == 0)
						return;

					if (--frame.LogMessageScopeCounter == 0)
					{
						frame.Scope.Dispose();
						owner.scopes.Remove(frame);
					}
				}
			} // proc Dispose

			private object SyncRoot => owner.scopes;

			public LogMsgType Typ => frame.Scope.Typ;

			public ILogMessageScope AutoFlush(bool autoFlush) => frame.Scope.AutoFlush(autoFlush);
			public ILogMessageScope SetType(LogMsgType value, bool force = false) => frame.Scope.SetType(value, force);
			public IDisposable Indent(string indentation = "  ") => frame.Scope.Indent(indentation);
			public ILogMessageScope Write(string text) => frame.Scope.Write(text);
			public ILogMessageScope WriteLine(bool force = true) => frame.Scope.WriteLine(force);
		} // class LogMessageScopeHolder

		#endregion

		#region -- class LogMessageScopeFrame -----------------------------------------

		/// <summary></summary>
		private sealed class LogMessageScopeFrame
		{
			public LogMessageScopeFrame(LogMessageScope scope)
			{
				this.Scope = scope;
			} // ctor

			public int LogMessageScopeCounter { get; set; } = 0;
			public LogMessageScope Scope { get; }
		} // class LogMessageScopeFrame

		#endregion

		private DELogFile logFile = null;
		private readonly IDEListController logListController;
		private readonly Lazy<string> logFileName;
		private bool isDebug = false;

		private readonly List<LogMessageScopeFrame> scopes = new List<LogMessageScopeFrame>();

		#region -- Ctor/Dtor ----------------------------------------------------------

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		public DEConfigLogItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
			logFileName = new Lazy<string>(() => Path.Combine(Server.LogPath, GetFullLogName()));

			RegisterList(LogLineListId, logListController = new LogLineController(this), true);
		} // ctor

		/// <summary></summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				logListController?.Dispose();
				logFile?.Dispose();
				logFile = null;
				ConfigLogItemCount--;
			}
			base.Dispose(disposing);
		} // proc Disposing

		private void GetFullName(DEConfigItem cur, StringBuilder sb)
		{
			if (cur == null)
				return;

			// Vorgänger
			GetFullName(cur.Owner as DEConfigItem, sb);

			// AKtuelle
			if (sb.Length > 0)
				sb.Append('\\');
			sb.Append(cur.Name);
		} // func GetFullName

		private string GetFullLogName()
		{
			var sb = new StringBuilder();
			GetFullName(this, sb);
			sb.Append(".log");
			return sb.ToString();
		} // func GetFullName

		/// <summary></summary>
		/// <param name="config"></param>
		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			if (config.ConfigOld == null) // LogDatei darf nur einmal initialisiert werden
			{
				if (String.IsNullOrEmpty(Server.LogPath))
					throw new ArgumentNullException("logPath", "LogPath muss gesetzt sein.");

				// Lege die Logdatei an
				logFile = new DELogFile(LogFileName);
				logFile.LinesAdded += (sender, e) => OnLinesAdded();

				ConfigLogItemCount++;
			}

			// Lese die Parameter für die Logdatei
			var log = XConfigNode.Create(Server.Configuration, config.ConfigNew).Element(DEConfigurationConstants.xnLog);
			SetLogSize((uint)log.GetAttribute<FileSize>("min").Value, (uint)log.GetAttribute<FileSize>("max").Value);
		} // proc OnBeginReadConfiguration

		private void SetLogSize(uint minLogSize, uint maxLogSize)
		{
			if (minLogSize != logFile.MinimumSize 
				|| maxLogSize != logFile.MaximumSize)
			{
				logFile.SetSize(minLogSize, maxLogSize);
				OnPropertyChanged(nameof(LogMinSize));
				OnPropertyChanged(nameof(LogMaxSize));
			}
		} // prop SetLogSize

		#endregion

		#region -- IDELogConfig Members -----------------------------------------------

		void ILogger.LogMsg(LogMsgType type, string text)
		{
			Debug.Print("[{0}] {1}", Name, text);

			// create log line
			if (IsDebug || type != LogMsgType.Debug)
			{
				var logLine = new DELogLine(DateTime.Now, type == LogMsgType.Debug ? LogMsgType.Information : type, text);
				if (Server.Queue?.IsQueueRunning ?? false)
					Server.Queue.RegisterCommand(() => logFile?.Add(logLine));
				else // Background thread is not in service, synchron add
					logFile?.Add(logLine);
			}

			DEScope.GetScopeService<IDEDebugContext>(false)?.OnMessage(type, text);
		} // proc ILogger.LogMsg

		ILogMessageScope ILogger2.CreateScope(LogMsgType typ, bool autoFlush)
		{
			lock (scopes)
			{
				var frame = new LogMessageScopeFrame(new LogMessageScope(this, typ, autoFlush));
				scopes.Add(frame);
				return new LogMessageScopeHolder(this, frame);
			}
		} // func ILogger2.CreateScope

		ILogMessageScope ILogger2.GetScope(LogMsgType typ, bool autoFlush)
		{
			lock (scopes)
			{
				if (scopes.Count == 0)
					return ((ILogger2)this).CreateScope(typ, autoFlush);
				else
				{
					var frame = scopes.Last();
					frame.Scope.SetType(typ);
					if (autoFlush)
						frame.Scope.AutoFlush();
					return new LogMessageScopeHolder(this, frame);
				}
			}
		} // func ILogger2.GetScope

		/// <summary></summary>
		public int ConfigLogItemCount
		{
			get
			{
				return this.GetService<IDEBaseLog>(typeof(DEServerBaseLog), true)?.TotalLogCount ?? 0;
			}
			set
			{
				var baseLog = this.GetService<IDEBaseLog>(typeof(DEServerBaseLog), true);
				if (baseLog != null)
					baseLog.TotalLogCount = value;
			}
		} // prop ConfigLogItemCount

		#endregion

		#region -- Http Schnittstelle -------------------------------------------------

		/// <summary>Publish commands to turn the debug flag on/off.</summary>
		protected void PublishDebugInterface()
		{
			PublishItem(new DEConfigItemPublicAction("debugOn") { DisplayName = "DebugOn" });
			PublishItem(new DEConfigItemPublicAction("debugOff") { DisplayName = "DebugOff" });
		} // proc PublishDebugInterface

		/// <summary>Is called if a log line is added.</summary>
		protected virtual void OnLinesAdded()
		{
			FireSysEvent(LogLineListId, null,
				new XElement("lines", 
					new XAttribute("lineCount", LogLineCount)
				)
			);
			OnPropertyChanged(nameof(LogLineCount));
			OnPropertyChanged(nameof(LogFileSize));
		} // proc OnLinesAdded

		/// <summary>Aktivate debug flag.</summary>
		[DEConfigHttpAction("debugOn", IsSafeCall = true, SecurityToken = SecuritySys)]
		internal void HttpDebugOn()
			=> IsDebug = true;

		/// <summary>Deactivate debug flag.</summary>
		[DEConfigHttpAction("debugOff", IsSafeCall = true, SecurityToken = SecuritySys)]
		internal void HttpDebugOff()
			=> IsDebug = false;

#if DEBUG
		/// <summary>Spam log with message for test proposes.</summary>
		/// <param name="spam">Spam message.</param>
		/// <param name="lines">Number of lines to generate</param>
		/// <param name="payloadLength">Add payload to make the message bigger.</param>
		/// <returns></returns>
		[
		DEConfigHttpAction("spam", IsSafeCall = true),
		Description("Spam log with message for test proposes."),
		LuaMember
		]
		public LuaTable SpamLog(string spam = "SPAM", int lines = 1024, int payloadLength = 0)
		{
			var msg = payloadLength > 0 ? "{0} {1} " + new string('-', payloadLength) : "{0} {1}";
			for (var i = 1; i <= lines; i++)
				Log.Info(msg, spam, i);
			return new LuaTable { ["lines"] = lines };
		} // func HttpSpamLog
#endif

		#endregion

		#region -- Properties ---------------------------------------------------------

		/// <summary>Size of the log file, to truncate.</summary>
		[
		PropertyName("tw_log_minsize"),
		DisplayName("Size (minimum)"),
		Description("Size of the log file, to truncate."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogMinSize => new FileSize(logFile.MinimumSize);
		/// <summary>If this size exceeds, the truncate will start.</summary>
		[
		PropertyName("tw_log_maxsize"),
		DisplayName("Size (maximum)"),
		Description("If this size exceeds, the truncate will start."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogMaxSize => new FileSize(logFile.MaximumSize);
		/// <summary>Current size of the log file.</summary>
		[
		PropertyName("tw_log_size"),
		DisplayName("Size (current)"),
		Description("Current size of the log file."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogFileSize => new FileSize(logFile.CurrentSize);
		/// <summary>Fullpath of the log file.</summary>
		[
		PropertyName("tw_log_filename"),
		DisplayName("FileName"),
		Description("Fullpath of the log file."),
		Category(LogCategory)
		]
		public string LogFileName => logFileName.Value;
		/// <summary>Number of lines in the log file.</summary>
		[
		PropertyName("tw_log_lines"),
		DisplayName("Lines"),
		Description("Number of lines in the log file."),
		Category(LogCategory),
		Format("{0:N0}")
		]
		public int LogLineCount => logFile.Count;

		/// <summary>if <c>true</c>, all debug-messages are written to the log file.</summary>
		[
		LuaMember,
		PropertyName("tw_log_debug"),
		DisplayName("IsDebug"),
		Description("Write the debug messages to the log file as information."),
		Category(LogCategory)
		]
		public bool IsDebug
		{
			get => isDebug;
			set
			{
				if (isDebug != value)
				{
					isDebug = value;
					OnPropertyChanged(nameof(IsDebug));
				}
			}
		} // prop IsDebugEvents

		/// <summary>Has this node a log file.</summary>
		public bool HasLog => logFile != null;

		#endregion
	} // class ConfigLogItem

	#endregion
}
