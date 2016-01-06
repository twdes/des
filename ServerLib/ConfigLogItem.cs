using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DELogLine ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Holds a log line.</summary>
	public sealed class DELogLine
	{
		/// <summary>Creates</summary>
		/// <param name="dataLine"></param>
		public DELogLine(string dataLine)
		{
			LogMsgType typ;
			DateTime stamp;
			string text;
			LogLineParser.Parse(dataLine, out typ, out stamp, out text);

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
			this.Text = text;
		} // ctor

		public override string ToString()
			=> ToLineData();

		public string ToLineData()
		{
			var sb = new StringBuilder(Text?.Length ?? +64); // reserve space for the string

			sb.Append(LogLineParser.ConvertDateTime(Stamp))
				.Append('\t')
				.Append(((int)Typ).ToString())
				.Append('\t');

			foreach (char c in Text)
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

	#region -- class DELogFile ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Access to a LogFile.</summary>
	public sealed class DELogFile : IDERangeEnumerable2<DELogLine>, IDisposable
	{
		private const string WindowsLineEnding = "\r\n";

		public event EventHandler LinesAdded;

		private readonly byte[] logFileBuffer = new byte[0x10000];

		private readonly object logFileLock = new object(); // Lock for multithread access
		private readonly FileStream logData;                // File, that contains the lines
		private bool isDisposed = false;

		private int logLineCount = 0;                           // Is the current count of log lines
		private List<long> linesOffsetCache = new List<long>(); // Offset of line >> 5 (32)

		#region -- Ctor/Dtor --------------------------------------------------------------

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
			using (ManualResetEventSlim started = new ManualResetEventSlim(false))
			{
				Task.Factory.StartNew(() => CreateLogLineCache(started));
				started.Wait();
			}
		} // ctor

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

		#region -- Add, SetSize -----------------------------------------------------------

		public void SetSize(uint minSize, uint maxSize)
		{
			lock (logFileLock)
			{
				MinimumSize = Math.Min(minSize, maxSize);
				MaximumSize = Math.Max(maxSize, minSize);
			}
		} // func SetSize

		public void Add(DELogLine line)
		{
			var lineData = Encoding.Default.GetBytes(line.ToLineData() + WindowsLineEnding);
			lock (logFileLock)
			{
				if (isDisposed) // check if the log is disposed
					return;

				try
				{
					if (logData.Length + lineData.Length > MaximumSize)
						TruncateLog(logData.Length - MinimumSize); // truncate the log file, calculate the new size

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

		private void TruncateLog(long removeBytes)
		{
			// search for the line position (es wird 32 Zeilenweise entfernt)
			var indexRemove = linesOffsetCache.BinarySearch(removeBytes);
			if (indexRemove == -1) // before first
				indexRemove = 0;
			else if (indexRemove < -1) // within a block
				indexRemove = ~indexRemove - 1; // not lower the minimum

			// correct the remove byte to the line ending
			removeBytes = indexRemove < linesOffsetCache.Count ? linesOffsetCache[indexRemove] : logData.Length;

			// copy the log data to the start of the file
			var readPos = removeBytes;
			var writePos = 0;
			var readed = 0;
			while (logData.Length > readPos)
			{
				logData.Seek(readPos, SeekOrigin.Begin);
				readed = logData.Read(logFileBuffer, 0, logFileBuffer.Length);
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

		#region -- GetEnumerator ----------------------------------------------------------

		public IEnumerator GetEnumerator()
			=> GetEnumerator(0, Int32.MaxValue, null);

		public IEnumerator<DELogLine> GetEnumerator(int start, int count)
			=> GetEnumerator(start, count, null);

		public IEnumerator<DELogLine> GetEnumerator(int start, int count, IPropertyReadOnlyDictionary selector)
		{
			// create selector
			if (selector != null)
			{
				throw new NotImplementedException();
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

	#region -- class DEConfigLogItem ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class DEConfigLogItem : DEConfigItem, ILogger, ILogger2
	{
		public const string LogCategory = "Log";

		#region -- class LogLineDescriptor ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
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

			private static readonly LogLineDescriptor logLineDescriptor = new LogLineDescriptor();

			public static LogLineDescriptor Instance { get { return logLineDescriptor; } }
		} // class LogLineDescriptor

		#endregion

		#region -- class LogLineController ------------------------------------------------

		private sealed class LogLineController : IDEListController
		{
			private DEConfigLogItem configItem;

			public LogLineController(DEConfigLogItem configItem)
			{
				this.configItem = configItem;
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
			{
				throw new NotSupportedException();
			} // proc EnterWriteLock

			public void OnBeforeList() { }

			public string Id => LogLineListId;
			public string DisplayName => LogCategory;
			public System.Collections.IEnumerable List => configItem.logFile;
			public IDEListDescriptor Descriptor => LogLineDescriptor.Instance;
		} // class LogLineController

		#endregion

		#region -- class LogMessageScopeHolder --------------------------------------------

		private sealed class LogMessageScopeHolder : ILogMessageScope
		{
			private DEConfigLogItem owner;

			public LogMessageScopeHolder(DEConfigLogItem owner)
			{
				this.owner = owner;
				lock (owner.logMessageScopeLock)
					this.owner.logMessageScopeCounter++;
			} // ctor

			public void Dispose()
			{
				lock (owner.logMessageScopeLock)
				{
					if (this.owner.logMessageScopeCounter == 0)
						return;

					if (--this.owner.logMessageScopeCounter == 0)
					{
						owner.currentMessageScope.Dispose();
						owner.currentMessageScope = null;
					}
				}
			} // proc Dispose

			private LogMessageScope Scope => owner.currentMessageScope;
			public LogMsgType Typ { get { return Scope.Typ; } }

			public ILogMessageScope AutoFlush() => Scope.AutoFlush();
			public ILogMessageScope SetType(LogMsgType value, bool force = false) => Scope.SetType(value, force);
			public IDisposable Indent(string indentation = "  ") => Scope.Indent(indentation);
			public ILogMessageScope Write(string text) => Scope.Write(text);
			public ILogMessageScope WriteLine(bool force = true) => Scope.WriteLine(force);
		} // class LogMessageScopeHolder

		#endregion

		private DELogFile logFile = null;
		private readonly Lazy<string> logFileName;

		private object logMessageScopeLock = new object();
		private LogMessageScope currentMessageScope = null;
		private int logMessageScopeCounter = 0;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEConfigLogItem(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			this.logFileName = new Lazy<string>(() => Path.Combine(Server.LogPath, GetFullLogName()));

			RegisterList(LogLineListId, new LogLineController(this), true);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Procs.FreeAndNil(ref logFile);
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
			var xLog = config.ConfigNew.Element(DEConfigurationConstants.xnLog);
			if (xLog != null)
				SetLogSize(xLog.GetAttribute("min", logFile.MinimumSize), xLog.GetAttribute("max", logFile.MaximumSize));
		} // proc OnBeginReadConfiguration

		private void SetLogSize(uint minLogSize, uint maxLogSize)
		{
			if (minLogSize != logFile.MinimumSize ||
				maxLogSize != logFile.MaximumSize)
			{
				logFile.SetSize(minLogSize, maxLogSize);
				OnPropertyChanged(nameof(LogMinSize));
				OnPropertyChanged(nameof(LogMaxSize));
			}
		} // prop SetLogSize

		#endregion

		#region -- IDELogConfig Members ---------------------------------------------------

		void ILogger.LogMsg(LogMsgType typ, string text)
		{
			Debug.Print("[{0}] {1}", Name, text);

			var logLine = new DELogLine(DateTime.Now, typ, text);
			if (Server.Queue?.IsQueueRunning ?? false)
				Server.Queue.Factory.StartNew(() => logFile?.Add(logLine));
			else // Background thread is not in service, synchron add
				logFile?.Add(logLine);
		} // proc ILogger.LogMsg

		public ILogMessageScope GetScope(LogMsgType typ = LogMsgType.Information, bool autoFlush = true)
		{
			lock (logMessageScopeLock)
			{
				if (currentMessageScope == null)
					currentMessageScope = new LogMessageScope(this, typ, autoFlush);
				else
				{
					currentMessageScope.SetType(typ);
					if (autoFlush)
						currentMessageScope.AutoFlush();
				}
			}
			return new LogMessageScopeHolder(this);
		} // func GetScope

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

		#region -- Http Schnittstelle -----------------------------------------------------

		protected virtual void OnLinesAdded()
		{
			FireEvent(LogLineListId, null, new XElement("lines", new XAttribute("lineCount", LogLineCount)));
			OnPropertyChanged(nameof(LogLineCount));
			OnPropertyChanged(nameof(LogFileSize));
		} // proc OnLinesAdded

#if DEBUG
		[
		DEConfigHttpAction("spam", IsSafeCall = true),
		Description("Erzeugt im aktuellen Log die angegebene Anzahl von Meldungen.")
		]
		private XElement HttpSpamLog(string spam = "SPAM", int lines = 1024)
		{
			while (lines-- > 0)
				Log.Info("{0} {1}" + new string('-', 100), spam, lines);
			return new XElement("spam");
		} // func HttpSpamLog
#endif

		#endregion

		#region -- Properties -------------------------------------------------------------

		[
		PropertyName("tw_log_minsize"),
		DisplayName("Size (minimum)"),
		Description("Size of the log file, to truncate."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogMinSize => new FileSize(logFile.MinimumSize);
		[
		PropertyName("tw_log_maxsize"),
		DisplayName("Size (maximum)"),
		Description("If this size exceeds, the truncate will start."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogMaxSize => new FileSize(logFile.MaximumSize);
		[
		PropertyName("tw_log_size"),
		DisplayName("Size (current)"),
		Description("Current size of the log file."),
		Category(LogCategory),
		Format("{0:XiB}")
		]
		public FileSize LogFileSize => new FileSize(logFile.CurrentSize);
		[
		PropertyName("tw_log_filename"),
		DisplayName("FileName"),
		Description("Fullpath of the log file."),
		Category(LogCategory)
		]
		public string LogFileName => logFileName.Value;
		[
		PropertyName("tw_log_lines"),
		DisplayName("Lines"),
		Description("Number of lines in the log file."),
		Category(LogCategory),
		Format("{0:N0}")
		]
		public int LogLineCount => logFile.Count;

		public bool HasLog => logFile != null;

		#endregion
	} // class ConfigLogItem

	#endregion
}
