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
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Neo.Console;
using Neo.IronLua;
using TecWare.DE.Data;
using TecWare.DE.Networking;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Data
{
	#region -- class LogLine ----------------------------------------------------------

	internal sealed class LogLine
	{
		private readonly LogMsgType type;
		private readonly DateTime stamp;
		private readonly string text;

		public LogLine(LogMsgType type, DateTime stamp, string text)
		{
			this.type = type;
			this.stamp = stamp;
			this.text = (text ?? String.Empty).Replace("\t", "    ");
		} // ctor

		public LogMsgType Type => type;
		public DateTime Stamp => stamp;
		public string Text => text;

		public static string ToMsgTypeString(LogMsgType type)
		{
			switch (type)
			{
				case LogMsgType.Error:
					return "E";
				case LogMsgType.Warning:
					return "W";
				default:
					return "I";
			}
		} // func ToMsgTypeString

		private static LogMsgType FromMsgTypeString(string t)
		{
			switch (String.IsNullOrEmpty(t) ? 'I' : Char.ToUpper(t[0]))
			{
				case 'E':
					return LogMsgType.Error;
				case 'W':
					return LogMsgType.Warning;
				default:
					return LogMsgType.Information;
			}
		} // func GetFromString

		private static DateTime GetDateTime(string stamp)
			=> DateTime.TryParse(stamp, out var dt) ? dt : DateTime.MinValue;

		public static bool TryGetLogEvent(DEHttpSocketEventArgs e, out int lineCount)
		{
			if (e.Id == "tw_lines") // log line event
			{
				lineCount = e.Values.GetAttribute("lineCount", -1);
				return lineCount > 0;
			}
			else
			{
				lineCount = -1;
				return false;
			}
		} // func TryGetLogEvent

		public static async Task GetLogLinesAsync(DEHttpClient http, string path, int start, int count, Action<string, LogLine> process)
		{
			var xLines = await http.GetXmlAsync(Program.MakeUri(path,
				new PropertyValue("action", "listget"),
				new PropertyValue("id", "tw_lines"),
				new PropertyValue("desc", false),
				new PropertyValue("start", start),
				new PropertyValue("count", count)
			), rootName: "list");

			var lines = xLines.Element("items")?.Elements("line");
			if (lines != null)
			{
				foreach (var x in lines)
				{
					process(
						path,
						new LogLine(
							FromMsgTypeString(x.GetAttribute("typ", "I")),
							GetDateTime(x.GetAttribute("stamp", null)),
							x.Value
						)
					);
				}
			}
		} // func GetLogLinesAsync
	} // class LogLine

	#endregion

	#region -- class LogPropertyInfo --------------------------------------------------

	internal sealed class LogPropertyInfo
	{
		private readonly string name;
		private readonly string displayName;
		private readonly Type dataType;
		private readonly string description;
		private readonly string format;

		public LogPropertyInfo(string name, string displayName, Type dataType, string description, string format)
		{
			this.name = name ?? throw new ArgumentNullException(nameof(name));
			this.displayName = String.IsNullOrEmpty(displayName) ? null : displayName;
			this.dataType = dataType ?? typeof(string);
			this.description = description;
			this.format = format;
		} // ctor

		public string FormatValue(string value)
		{
			try
			{
				var v = Procs.ChangeType(value, DataType);
				if (String.IsNullOrEmpty(format))
					return v.ToString();
				else if (format[0] == '{')
					return String.Format(format, v);
				else
					return v is IFormattable f ? f.ToString(format, CultureInfo.CurrentCulture) : v.ToString();
			}
			catch
			{
				return value;
			}
		} // func FormatValue

		public string Name => name;
		public string DisplayName => displayName ?? name;
		public Type DataType => dataType ?? typeof(string);
		public string Description => description ?? String.Empty;
		public string Format => format;
	} // class LogPropertyInfo

	#endregion

	#region -- class LogProperty ------------------------------------------------------

	internal sealed class LogProperty : ObservableObject
	{
		private readonly LogPropertyInfo info;
		private string value;

		private LogProperty(LogPropertyInfo info, string value = null)
		{
			this.info = info ?? throw new ArgumentNullException(nameof(info));
			this.value = value;
		} // ctor

		public void SetValue(string value)
		{
			if (this.value != value)
			{
				this.value = value;
				OnPropertyChanged(nameof(FormattedValue));
				OnPropertyChanged(nameof(RawValue));
			}
		} // proc SetValue

		public LogPropertyInfo Info => info;
		public string FormattedValue => info.FormatValue(value);
		public string RawValue => value;

		#region -- class WaitForPropertyInfo ------------------------------------------

		private sealed class WaitForPropertyInfo
		{
			private readonly string name;
			private readonly TaskCompletionSource<LogPropertyInfo> task;

			public WaitForPropertyInfo(string name)
			{
				this.name = name ?? throw new ArgumentNullException(nameof(name));
				task = new TaskCompletionSource<LogPropertyInfo>();
			} // ctor

			public bool SetResult(LogPropertyInfo propertyInfo)
			{
				if (propertyInfo.Name == name)
				{
					task.TrySetResult(propertyInfo);
					return true;
				}
				else
					return false;
			} // func SetResult

			public Task<LogPropertyInfo> Task => task.Task;
		} // class WaitForPropertyInfo

		#endregion

		private static readonly Dictionary<string, LogPropertyInfo> propertyStore = new Dictionary<string, LogPropertyInfo>();
		private static readonly List<WaitForPropertyInfo> waits = new List<WaitForPropertyInfo>();
		private static readonly List<string> currentPropertiesFetching = new List<string>();

		private static void UpdatePropertyInfo(LogPropertyInfo propertyInfo)
		{
			// update store
			propertyStore[propertyInfo.Name] = propertyInfo;

			// invoke events
			for (var i = waits.Count - 1; i >= 0; i--)
			{
				if (waits[i].SetResult(propertyInfo))
					waits.RemoveAt(i);
			}
		} // proc UpdatePropertyInfo

		private static async Task GetLogPropertyInfosAsync(DEHttpClient http, string path)
		{
			// lock property fetch
			if (currentPropertiesFetching.IndexOf(path) >= 0)
				return;
			currentPropertiesFetching.Add(path);
			try
			{
				await GetLogPropertiesAsync(http, path);
			}
			finally
			{
				currentPropertiesFetching.Remove(path);
			}
		} // func GetLogPropertyInfosAsync

		public static Task<LogPropertyInfo> GetLogPropertyInfoAsync(DEHttpClient http, string path, string name)
		{
			if (propertyStore.TryGetValue(name, out var propertyInfo))
				return Task.FromResult(propertyInfo);

			// register wait info
			var w = new WaitForPropertyInfo(name);
			waits.Add(w);

			// fork fetch properties
			GetLogPropertyInfosAsync(http, path).Silent();
			
			return w.Task;
		} // func GetLogPropertyInfo

		public static async Task GetLogPropertiesAsync(DEHttpClient http, string path, Action<string, LogProperty> process = null)
		{
			var xProperties = await http.GetXmlAsync(Program.MakeUri(path,
				new PropertyValue("action", "listget"),
				new PropertyValue("id", "tw_properties")
			), rootName: "list");

			var properties = xProperties.Element("items")?.Elements("property");
			if (properties != null)
			{
				foreach (var x in properties)
				{
					var name = x.GetAttribute("name", null);
					if (name == null)
						continue;

					// parse info
					var propertyInfo = new LogPropertyInfo(name,
						x.GetAttribute("displayname", name),
						LuaType.GetType(x.GetAttribute("type", "string"), lateAllowed: true).Type,
						x.GetAttribute("description", name),
						x.GetAttribute("format", null)
					);
					UpdatePropertyInfo(propertyInfo);

					// process value
					process?.Invoke(path, new LogProperty(propertyInfo, x.Value));
				}
			}
		} // func GetLogProperties
	} // class LogPropertyValue

	#endregion

	#region -- class LogLines ---------------------------------------------------------

	internal abstract class LogLines : IReadOnlyList<LogLine>, INotifyCollectionChanged
	{
		public event NotifyCollectionChangedEventHandler CollectionChanged;

		#region -- class LogLineBuffer ------------------------------------------------

		internal class LogLineBuffer
		{
			private readonly LogLines lines;
			private readonly LogLine[] buffer = new LogLine[1024];
			private int bufferCount = 0;
			private int insertAt = 0;

			private readonly List<LogLine> staged = new List<LogLine>();

			public LogLineBuffer(LogLines lines)
			{
				this.lines = lines ?? throw new ArgumentNullException(nameof(lines));
			} // ctor

			private void FlushCore()
			{
				lines.Insert(insertAt, buffer, bufferCount);
				insertAt += bufferCount;
				bufferCount = 0;
			} // proc FlushCore

			public void Flush()
			{
				if (bufferCount > 0)
					FlushCore();
			} // proc Flush

			private void AddCore(LogLine item)
			{
				if (bufferCount >= buffer.Length)
					FlushCore();

				buffer[bufferCount++] = item;
			} // proc AddCore

			public void Add(LogLine item)
			{
				for (var i = 0; i < staged.Count; i++)
					AddCore(staged[i]);
				staged.Clear();

				AddCore(item);
			} // proc Add

			public void Stage(LogLine item)
				=> staged.Add(item);

			public void PatchDate(DateTime stamp)
			{
				for (var i = 0; i < staged.Count; i++)
					staged[i] = new LogLine(staged[i].Type, stamp, staged[i].Text);
			} // proc PatchDate
		} // class LogLineBuffer

		#endregion

		private readonly List<LogLine> lines = new List<LogLine>();

		protected void Clear()
		{
			lines.Clear();
			CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		} // proc Clear

		protected void Insert(int insertAt, LogLine[] lines, int count)
		{
			var app = ConsoleApplication.Current;
			if (app.IsInvokeRequired)
				app.Invoke(() => Insert(insertAt, lines, count));
			else
			{
				var startIndex = this.lines.Count;
				this.lines.InsertRange(insertAt,
					lines.Length == count
						? lines
						: lines.Take(count)
				);
				var length = this.lines.Count - startIndex;
				if (length > 0)
				{
					var slice = new LogLine[length];
					this.lines.CopyTo(startIndex, slice, 0, length);
					CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
						NotifyCollectionChangedAction.Add,
						slice,
						insertAt
					)); 
				}
			}
		} // proc Append

		public IEnumerator<LogLine> GetEnumerator()
			=> lines.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator()
			=> GetEnumerator();

		public LogLine this[int index] => lines[index];

		public int Count => lines.Count;
	} // class LogLines

	#endregion

	#region -- class LogFileLines -----------------------------------------------------

	internal sealed class LogFileLines : LogLines
	{
		public LogFileLines(string fileName)
		{
			FetchLinesAsync(fileName).Silent(e => ConsoleApplication.Current.WriteError(e, "Log not parsed."));
		} // ctor

		private static readonly Regex logcatLine = new Regex(@"^(?<mo>\d{2})-(?<d>\d{2})\s+(?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})\.(?<f>\d{3})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<typ>\w)\s+(?<text>.*)", RegexOptions.Singleline | RegexOptions.Compiled);

		private static bool TryParseLogcat(string line, LogLineBuffer buffer)
		{
			if (line.StartsWith("--------- beginning of"))
			{
				buffer.Stage(new LogLine(LogMsgType.Information, DateTime.MinValue, line.TrimStart('-', ' ')));
				return true;
			}
			else
			{
				var m = logcatLine.Match(line);
				if (m.Success)
				{
					var dt = new DateTime(
						year: DateTime.Now.Year,
						month: Int32.Parse(m.Groups["mo"].Value),
						day: Int32.Parse(m.Groups["d"].Value),
						hour: Int32.Parse(m.Groups["h"].Value),
						minute: Int32.Parse(m.Groups["mi"].Value),
						second: Int32.Parse(m.Groups["s"].Value),
						millisecond: Int32.Parse(m.Groups["f"].Value)
					);

					LogMsgType typ;
					switch (m.Groups["typ"].Value)
					{
						case "E":
							typ = LogMsgType.Error;
							break;
						case "W":
							typ = LogMsgType.Warning;
							break;
						case "D":
							typ = LogMsgType.Debug;
							break;
						default:
							typ = LogMsgType.Information;
							break;
					}

					buffer.PatchDate(dt);
					buffer.Add(new LogLine(typ, dt, m.Groups["text"].Value));
					return true;
				}
				else
					return false;
			}
		} // func TryParseLogcat

		private async Task FetchLinesAsync(string fileName)
		{
			using (var src = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (var tr = new StreamReader(src, Encoding.Default, true))
			{
				var buf = new LogLineBuffer(this);

				var state = 0;
				string line;
				while ((line = await tr.ReadLineAsync()) != null)
				{
					if (state == 0)
					{
						if (TryParseLogcat(line, buf))
							state = 2;
						else
						{
							LogLineParser.Parse(line, out var typ, out var stamp, out var text);
							if (text != null) // valid log format
							{
								buf.Add(new LogLine(typ, stamp, text));
								state = 1;
							}
							else
							{
								buf.Add(new LogLine(LogMsgType.Information, DateTime.MinValue, line));
								state = 10;
							}
						}
					}
					else if (state == 1) // parse valid log
					{
						LogLineParser.Parse(line, out var typ, out var stamp, out var text);
						buf.Add(new LogLine(typ, stamp, text));
					}
					else if (state == 2)
					{
						TryParseLogcat(line, buf);
					}
					else
						buf.Add(new LogLine(LogMsgType.Information, DateTime.MinValue, line));
				}

				buf.Flush();
			}
		} // proc FetchLinesAsync
	} // class LogFileLines

	#endregion

	#region -- class LogHttpLines -----------------------------------------------------

	internal sealed class LogHttpLines : LogLines
	{
		private readonly DEHttpClient http;
		private readonly string path;

		private readonly AsyncQueue queue = new AsyncQueue();
		private int lastLineCount;

		public LogHttpLines(DEHttpClient http, string path)
		{
			this.http = http ?? throw new ArgumentNullException(nameof(http));
			this.path = path ?? throw new ArgumentNullException(nameof(path));

			queue.OnException = e => ConsoleApplication.Current.Invoke(()=> ConsoleApplication.Current.WriteError(e, "Log not parsed."));
			queue.Enqueue(FetchLinesAsync);
		} // ctor

		private async Task FetchLinesAsync()
		{
			var buffer = new LogLineBuffer(this);
			await LogLine.GetLogLinesAsync(http, path, 0, Int32.MaxValue, (_, log) => buffer.Add(log));
			lastLineCount = Count;
			buffer.Flush();
		} // proc FetchLinesAsync

		private async Task FetchNextAsync(int nextLineCount)
		{
			if (lastLineCount < nextLineCount) // log not truncated calculate difference
			{
				var count = nextLineCount - lastLineCount;
				if (count > 0)
				{
					var buffer = new LogLineBuffer(this);
					await LogLine.GetLogLinesAsync(http, path, lastLineCount, count, (_, log) => buffer.Add(log));
					lastLineCount = count + lastLineCount;
					buffer.Flush();
				}
			}
			else // fetch all
			{
				Clear();
				await FetchLinesAsync();
			}
		} // func FetchNextAsync

		internal void EventReceived(object sender, DEHttpSocketEventArgs e)
		{
			if (e.Path == path && LogLine.TryGetLogEvent(e, out var lineCount))
				queue.Enqueue(() => FetchNextAsync(lineCount));
		} // evetn EventReceived
	} // class LogHttpLines

	#endregion
}
