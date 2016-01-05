using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DEConfigLogItem ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class DEConfigLogItem : DEConfigItem, ILogger, ILogger2
	{
		public const string LogCategory = "Log";

		#region -- class NoCloseStream ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class NoCloseStream : Stream
		{
			private Stream stream;

			public NoCloseStream(Stream stream)
			{
				this.stream = stream;
			} // ctor

			public override bool CanRead { get { return stream.CanRead; } }
			public override bool CanSeek { get { return stream.CanSeek; } }
			public override bool CanWrite { get { return stream.CanWrite; } }
			public override void Flush() { stream.Flush(); }
			public override long Length { get { return stream.Length; } }
			public override long Position { get { return stream.Position; } set { stream.Position = value; } }
			public override int Read(byte[] buffer, int offset, int count) { return stream.Read(buffer, offset, count); }
			public override long Seek(long offset, SeekOrigin origin) { return stream.Seek(offset, origin); }
			public override void SetLength(long value) { stream.SetLength(value); }
			public override void Write(byte[] buffer, int offset, int count) { stream.Write(buffer, offset, count); }
		} // class NoCloseStream

		#endregion

		#region -- class LogLine ----------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class LogLine
		{
			private DateTime stamp;
			private LogMsgType typ;
			private string text;

			public LogLine(string dataLine)
			{
				LogLineParser.Parse(dataLine, out typ, out stamp, out text);
			} // ctor

			public LogLine(DateTime stamp, LogMsgType iTyp, string sText)
			{
				this.stamp = stamp;
				this.typ = iTyp;
				this.text = sText;
			} // ctor

			public override string ToString()
				=> GetLineData();

			public string GetLineData()
			{
				var sb = new StringBuilder();
				sb.Append(LogLineParser.ConvertDateTime(stamp))
					.Append('\t')
					.Append(((int)typ).ToString())
					.Append('\t');
				foreach (char c in text)
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
							sb.Append(@"\\0");
							break;
						default:
							sb.Append(c);
							break;
					}
				return sb.ToString();
			} // func GetLineData

			public DateTime Stamp { get { return stamp; } }
			public LogMsgType Typ { get { return typ; } }
			public string Text { get { return text; } }
		} // class LogLine

		#endregion

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
				var logLine = (LogLine)item;
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

		private sealed class LogLineController : IDEListController, IDERangeEnumerable<LogLine>
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
				Monitor.Enter(configItem.logFileLock);
				return new DisposableScope(() => Monitor.Exit(configItem.logFileLock));
			} // func EnterReadLock

			public IDisposable EnterWriteLock()
			{
				throw new NotSupportedException();
			} // proc EnterWriteLock

			public void OnBeforeList() { }

			#region -- IDERangeEnumerable members -------------------------------------------

			private IEnumerator<LogLine> GetEnumerator(int iStart, int iCount)
			{
				// Indexbereich
				int iIdxFrom = iStart >> 5;
				int iIdxFromOffset = iStart & 0x1F;

				string sLine;
				int i = 0;
				configItem.logFile.Position = configItem.linesOffsetCache[iIdxFrom];
				using (StreamReader sr = new StreamReader(new NoCloseStream(configItem.logFile), Encoding.Default, false))
				{
					while (i < iCount && (sLine = sr.ReadLine()) != null)
					{
						if (iIdxFromOffset > 0)
							iIdxFromOffset--;
						else
						{
							yield return new LogLine(sLine);
							i++;
						}
					}
				}
			} // func GetEnumerator

			IEnumerator<LogLine> IDERangeEnumerable<LogLine>.GetEnumerator(int iStart, int iCount) { return GetEnumerator(iStart, iCount); }
			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(0, configItem.LogLineCount); }

			int IDERangeEnumerable<LogLine>.Count => configItem.LogLineCount;

			#endregion

			public string Id => LogLineListId;
			public string DisplayName => LogCategory;
			public System.Collections.IEnumerable List => this;
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

		private uint MinLogSize = 3 << 20;
		private uint MaxLogSize = 4 << 20;
				
		private object logFileLock = new object();
		private FileStream logFile = null;
		private int logLines = 0; // Zählt immer eine Mehr (die Leerzeile am Ende)
		private List<long> linesOffsetCache = new List<long>(); // Gibt den Offset jeder 32igsten Zeile zurück

		private object logMessageScopeLock = new object();
		private LogMessageScope currentMessageScope = null;
		private int logMessageScopeCounter = 0;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEConfigLogItem(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			RegisterList(LogLineListId, new LogLineController(this), true);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (logFile != null)
				{
					lock (logFileLock)
						Procs.FreeAndNil(ref logFile);
				}
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

			lock (logFileLock)
			{
				if (config.ConfigOld == null) // LogDatei darf nur einmal initialisiert werden
				{
					if (String.IsNullOrEmpty(Server.LogPath))
						throw new ArgumentNullException("logPath", "LogPath muss gesetzt sein.");

					// Lege die Logdatei an
					var logFileName = LogFileName;
					var fi = new FileInfo(logFileName);
					if (!fi.Exists)
					{
						// Verzeichnis anlegen
						if (!fi.Directory.Exists)
							fi.Directory.Create();

						// Datei anlegen, aber nicht öffnen, sonst klappt das mit dem Read Share nicht
						File.WriteAllBytes(fi.FullName, new byte[0]);
					}

					// Öffne die Datei und baue den Cache auf
					logFile = new FileStream(fi.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
					Action createCache = CreateLogLineCache;
					createCache.BeginInvoke(ar => { try { ((Action)ar.AsyncState).EndInvoke(ar); } catch { } }, createCache);

					ConfigLogItemCount++;
				}
			}

			// Lese die Parameter für die Logdatei
			var xLog = config.ConfigNew.Element(DEConfigurationConstants.xnLog);
			if (xLog != null)
				SetLogSize(xLog.GetAttribute("min", MinLogSize), xLog.GetAttribute("max", MaxLogSize));
		} // proc OnBeginReadConfiguration

		#endregion

		#region -- IDELogConfig Members ---------------------------------------------------

		private void SetLogSize(uint minSize, uint maxSize)
		{
			lock (logFileLock)
			{
				this.LogMinSize = Math.Min(minSize, maxSize);
				this.LogMaxSize = Math.Max(maxSize, minSize);
			}
		} // func SetSize

		void ILogger.LogMsg(LogMsgType typ, string text)
		{
			Debug.Print("[{0}] {1}", Name, text);

			var logLine = new LogLine(DateTime.Now, typ, text);
			if (Server.Queue?.IsQueueRunning ?? false)
				Server.Queue.Factory.StartNew(() => AddToLog(logLine));
			else // Log ist noch nicht initialisiert
				AddToLog(logLine);
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
				var baseLog = this.GetService<IDEBaseLog>(typeof(DEServerBaseLog), true);
				return baseLog == null ? 0 : baseLog.TotalLogCount;
			}
			set
			{
				var baseLog = this.GetService<IDEBaseLog>(typeof(DEServerBaseLog), true);
				if (baseLog != null)
					baseLog.TotalLogCount = value;
			}
		} // prop ConfigLogItemCount

		#endregion

		#region -- LogFile ----------------------------------------------------------------

		private byte[] logFileBuffer = new byte[0x10000];

		/// <summary>Kürzt die Log-Datei um die angegebenen Bytes.</summary>
		private void TruncateLog(int bytes)
		{
			// Suche in Line die Position (es wird 32 Zeilenweise entfernt)
			var indexRemove = 0;
			while (indexRemove < linesOffsetCache.Count && linesOffsetCache[indexRemove] < bytes)
				indexRemove++;
			var removeBytes = indexRemove < linesOffsetCache.Count ? linesOffsetCache[indexRemove] : logFile.Length;

			// Kopiere Daten Nach vorne
			var readPos = removeBytes;
			var writePos = 0;
			var readed = 0;
			while (logFile.Length > readPos)
			{
				logFile.Seek(readPos, SeekOrigin.Begin);
				readed = logFile.Read(logFileBuffer, 0, logFileBuffer.Length);
				logFile.Seek(writePos, SeekOrigin.Begin);
				logFile.Write(logFileBuffer, 0, readed);

				readPos += logFileBuffer.Length;
				writePos += logFileBuffer.Length;
			}

			// Korrigiere den LineCache
			linesOffsetCache.RemoveRange(0, indexRemove);
			logLines -= indexRemove << 5;
			for (var i = 0; i < linesOffsetCache.Count; i++)
				linesOffsetCache[i] = linesOffsetCache[i] - removeBytes;

			// Verkleinern
			logFile.SetLength(logFile.Position);
			logFile.Position = logFile.Length;
		} // proc TruncateLog

		private void AddToLogLineCache(int lineIndex, long position)
		{
			if ((lineIndex & 0x1F) == 0)
			{
				var index = logLines >> 5;
				if (index != linesOffsetCache.Count)
					throw new InvalidDataException();
				linesOffsetCache.Add(position);
			}
		} // proc AddToLogLineCache

		private void CreateLogLineCache()
		{
			lock (logFileLock)
			{
				int readed;
				logFile.Position = 0;
				logLines = 0;
				AddToLogLineCache(logLines++, logFile.Position);
				do
				{
					readed = logFile.Read(logFileBuffer, 0, logFileBuffer.Length);

					for (var i = 0; i < readed; i++)
					{
						if (logFileBuffer[i] == '\n')
							AddToLogLineCache(logLines++, logFile.Position - readed + i + 1);
					}
				} while (readed > 0);
			}
		} // proc CreateLogLineCache

		private void AddToLog(LogLine logLine)
		{
			var lineData = logLine.GetLineData() + Environment.NewLine;
			lock (logFileLock)
				try
				{
					if (logFile == null)
						return;

					if (logFile.Length + lineData.Length > MaxLogSize)
						TruncateLog((int)(logFile.Length + lineData.Length - MinLogSize)); // Kürzt die Log-Datei

					// Position auf das Ende setzen
					AddToLogLineCache(logLines++, logFile.Position = logFile.Length);
					var buf = Encoding.Default.GetBytes(lineData);
					logFile.Write(buf, 0, buf.Length);
					logFile.Flush();

					OnPropertyChanged("LogFileSize");
					OnPropertyChanged("LogLineCount");
				}
				catch (Exception e)
				{
					Debug.Print(e.GetMessageString());
				}

			// Benachrichtigung über neue Zeile
			OnLinesAdded();
		} // proc AddToLog

		#endregion

		#region -- Http Schnittstelle -----------------------------------------------------

		protected virtual void OnLinesAdded()
		{
			FireEvent(LogLineListId, null, new XElement("lines", new XAttribute("lineCount", LogLineCount)));
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
		DisplayName("Größe (minimal)"),
		Description("Größe auf die die Logdatei gekürzt wird."),
		Category(LogCategory),
		Format("FILESIZE")
		]
		public uint LogMinSize
		{
			get { return MinLogSize; }
			private set { SetProperty(ref MinLogSize, value); }
		} // prop LogMinSize
		[
		PropertyName("tw_log_maxsize"),
		DisplayName("Größe (maximal)"),
		Description("Wird diese Größe überschritten, so wird die Logdatei gelürzt."),
		Category(LogCategory),
		Format("FILESIZE")
		]
		public uint LogMaxSize
		{
			get { return MaxLogSize; }
			private set { SetProperty(ref MaxLogSize, value); }
		} // prop LogMaxSize
		[
		PropertyName("tw_log_size"),
		DisplayName("Größe (aktuell)"),
		Description("Größe der Log-Datei."),
		Category(LogCategory),
		Format("FILESIZE")
		]
		public long LogFileSize { get { return logFile.Length; } }
		[
		PropertyName("tw_log_filename"),
		DisplayName("Datei"),
		Description("Vollständiger Pfad zu der Datei."),
		Category(LogCategory)
		]
		public string LogFileName { get { return Path.Combine(Server.LogPath, GetFullLogName()); } }
		[
		PropertyName("tw_log_lines"),
		DisplayName("Zeilen"),
		Description("Anzahl der Ereignisse in der Logdatei."),
		Category(LogCategory),
		Format("{0:N0}")
		]
		public int LogLineCount { get { return logLines - 1; } }

		public bool HasLog { get { lock (logFileLock) return logFile != null; } }

		#endregion	
	} // class ConfigLogItem

	#endregion
}
