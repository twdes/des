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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Neo.Console;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class SimpleDebugCommandHistory ----------------------------------------

	internal sealed class SimpleDebugCommandHistory : IConsoleReadLineHistory
	{
		private readonly FileInfo historyFile;
		private readonly List<string> history = new List<string>();
		private long historySize = 0L;
		private DateTime lastReadStamp;
		private int lastCheckTick;

		public SimpleDebugCommandHistory()
		{
			historyFile = new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TecWare", "SimpleDebug.txt"));

			Read();
		} // ctor

		private void Read()
		{
			historySize = 0;
			history.Clear();

			historyFile.Refresh();
			if (!historyFile.Exists)
				return;

			using (var sr = new StreamReader(historyFile.FullName, true))
			{
				var currentCommand = new StringBuilder();
				var first = true;
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					if (String.IsNullOrEmpty(line))
					{
						if (currentCommand.Length > 0)
							AppendCore(currentCommand.ToString());
						currentCommand.Clear();
						first = true;
					}
					else
					{
						if (first)
							first = false;
						else
							currentCommand.AppendLine();
						if (line != "\t")
							currentCommand.Append(line);
					}
				}

				if (currentCommand.Length > 0)
					AppendCore(currentCommand.ToString());
			}

			lastReadStamp = historyFile.LastWriteTimeUtc;
			lastCheckTick = Environment.TickCount;
		} // proc Read

		private void CheckForReload()
		{
			if (unchecked(Environment.TickCount - lastCheckTick) < 1000)
				return;

			// refresh file
			historyFile.Refresh();
			if (historyFile.LastWriteTimeUtc > lastReadStamp)
				Read();
			lastCheckTick = Environment.TickCount;
		} // proc CheckForReload

		private void Save()
		{
			if (!historyFile.Directory.Exists)
				historyFile.Directory.Create();

			using (var sw = new StreamWriter(historyFile.FullName, false, Encoding.UTF8))
			{
				var first = true;
				foreach (var cur in history)
				{
					if (first)
						first = false;
					else
						sw.WriteLine(); // command seperator

					foreach (var (startAt, len) in cur.SplitNewLinesTokens())
					{
						if (len == 0)
							sw.WriteLine("\t");
						else
							sw.WriteLine(cur.Substring(startAt, len));
					}
				}
			}

			historyFile.Refresh();
			lastReadStamp = historyFile.LastWriteTimeUtc;
			lastCheckTick = Environment.TickCount;
		} // proc Save

		private void RemoveAt(int idx)
		{
			if (idx < history.Count)
			{
				historySize -= history[idx].Length;
				history.RemoveAt(idx);
			}
		} // proc RemoveAt

		private void AppendCore(string command)
		{
			// remove beginning items
			while (historySize > 0x40000)
				RemoveAt(0);

			history.Add(command);
			historySize += command.Length;
		} // proc AppendCore

		public void Append(string command)
		{
			CheckForReload();

			while (true)
			{
				var idx = history.FindIndex(c => String.Compare(c, command, StringComparison.OrdinalIgnoreCase) == 0);
				if (idx == -1)
					break;
				RemoveAt(idx);
			}

			AppendCore(command);

			Save();
		} // proc Append

		public IEnumerator<string> GetEnumerator()
			=> history.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() 
			=> GetEnumerator();

		public int Count
		{
			get
			{
				CheckForReload();
				return history.Count;
			}
		} // prop Count

		public string this[int index] =>history[index];
	} // class SimpleDebugCommandHistory

	#endregion

	#region -- class SimpleDebugConsoleReadLineManager --------------------------------

	internal sealed class SimpleDebugConsoleReadLineManager : IConsoleReadLineManager, IConsoleReadLineScanner, IConsoleReadLineHistory
	{
		private readonly SimpleDebugCommandHistory history;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public SimpleDebugConsoleReadLineManager(SimpleDebugCommandHistory history)
		{
			this.history = history ?? throw new ArgumentNullException(nameof(history));
		} // ctor

		#endregion

		#region -- IConsoleReadLineManager implementation -----------------------------

		public bool CanExecute(string command)
			=> (command.Length > 0 && command[0] == ':') || command.EndsWith(Environment.NewLine);

		public string GetPrompt()
			=> "> ";

		#endregion

		#region -- IConsoleReadLineScanner implementation -----------------------------

		public int GetNextToken(int offset, string text, bool reverse)
			=> -1;

		private static ConsoleColor GetTokenColor(LuaToken typ)
		{
			switch (typ)
			{
				case LuaToken.KwAnd:
				case LuaToken.KwBreak:
				case LuaToken.KwCast:
				case LuaToken.KwConst:
				case LuaToken.KwDo:
				case LuaToken.KwElse:
				case LuaToken.KwElseif:
				case LuaToken.KwEnd:
				case LuaToken.KwFalse:
				case LuaToken.KwFor:
				case LuaToken.KwForEach:
				case LuaToken.KwFunction:
				case LuaToken.KwGoto:
				case LuaToken.KwIf:
				case LuaToken.KwIn:
				case LuaToken.KwLocal:
				case LuaToken.KwNil:
				case LuaToken.KwNot:
				case LuaToken.KwOr:
				case LuaToken.KwRepeat:
				case LuaToken.KwReturn:
				case LuaToken.KwThen:
				case LuaToken.KwTrue:
				case LuaToken.KwUntil:
				case LuaToken.KwWhile:
				case LuaToken.DotDotDot:
					return ConsoleColor.White;
				case LuaToken.Comment:
				case LuaToken.InvalidComment:
					return ConsoleColor.Green;
				case LuaToken.String:
				case LuaToken.InvalidString:
					return ConsoleColor.DarkYellow;
				case LuaToken.Assign:
				case LuaToken.BitAnd:
				case LuaToken.BitOr:
				case LuaToken.BracketClose:
				case LuaToken.BracketCurlyClose:
				case LuaToken.BracketCurlyOpen:
				case LuaToken.BracketOpen:
				case LuaToken.BracketSquareClose:
				case LuaToken.BracketSquareOpen:
				case LuaToken.Colon:
				case LuaToken.Comma:
				case LuaToken.Dot:
				case LuaToken.DotDot:
				case LuaToken.Equal:
				case LuaToken.Greater:
				case LuaToken.GreaterEqual:
				case LuaToken.Lower:
				case LuaToken.LowerEqual:
				case LuaToken.Minus:
				case LuaToken.NotEqual:
				case LuaToken.Percent:
				case LuaToken.Semicolon:
				case LuaToken.ShiftLeft:
				case LuaToken.ShiftRight:
				case LuaToken.Slash:
				case LuaToken.SlashShlash:
				case LuaToken.Star:
					return ConsoleColor.DarkGray;
				case LuaToken.Number:
					return ConsoleColor.DarkCyan;
				case LuaToken.InvalidChar:
					return ConsoleColor.Red;
				default:
					return ConsoleColor.Gray;
			}
		} // func GetTokenColor

		public void Scan(IConsoleReadLineScannerSource source)
		{
			if (source.LineCount == 0)
				return;

			var cmdLine = source[0];
			if (cmdLine.StartsWith(":")) // command
			{
				var startOfArgs = cmdLine.IndexOf(' ');
				if (startOfArgs == -1)
					startOfArgs = cmdLine.Length;
				source.AppendToken(0, 0, 0, startOfArgs, ConsoleColor.White);
				source.AppendToken(0, startOfArgs, 0, cmdLine.Length, ConsoleColor.Gray);
			}
			else
			{
				using (var lex = LuaLexer.Create("cmd.lua", source.TextReader, true, 0, 0, 0))
				{
					lex.Next();
					while (lex.Current.Typ != LuaToken.Eof)
					{
						source.AppendToken(lex.Current.Start.Line, lex.Current.Start.Col, lex.Current.End.Line, lex.Current.End.Col, GetTokenColor(lex.Current.Typ));

						lex.Next();
					}
				}
			}
		} // proc Scan

		#endregion

		#region -- IConsoleReadLineHistory implementation -----------------------------

		IEnumerator<string> IEnumerable<string>.GetEnumerator()
			=> history.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator()
			=> history.GetEnumerator();

		string IReadOnlyList<string>.this[int index]
			=> history[index];

		int IReadOnlyCollection<string>.Count
			=> history.Count;

		#endregion
	} // class SimpleDebugConsoleReadLineManager

	#endregion

	#region -- enum ConnectionState ---------------------------------------------------

	public enum ConnectionState
	{
		None = 0,
		Connecting = 1,
		ConnectedHttp = 2,
		ConnectedDebug = 4,
		ConnectedEvent = 8
	} // enum ConnectionState

	#endregion

	#region -- class ConnectionStateOverlay -------------------------------------------

	internal sealed class ConnectionStateOverlay : ConsoleOverlay
	{
		private ConnectionState currentState = ConnectionState.None;
		private string currentPath = "/";

		public ConnectionStateOverlay(ConsoleApplication app)
		{
			Application = app ?? throw new ArgumentNullException(nameof(app));
			ZOrder = 1000;

			// create char buffer
			Resize(20, 1);
			Left = -Width;
			Top = 0;

			// position
			Position = ConsoleOverlayPosition.Window;
			OnResize();
		} // ctor

		protected override void OnRender()
		{
			ConsoleColor GetStateColor()
			{
				if ((currentState & ConnectionState.ConnectedDebug) != 0)
					return ConsoleColor.DarkGreen;
				else if ((currentState & ConnectionState.ConnectedHttp) != 0)
					return ConsoleColor.DarkBlue;
				else
					return ConsoleColor.DarkRed;
			} // func GetStateColor

			var stateText = currentState == ConnectionState.None ? "None..." : (currentState == ConnectionState.Connecting ? "Connecting..." : currentPath);
			if (stateText.Length > 18)
				stateText = " ..." + stateText.Substring(stateText.Length - 15, 15) + " ";
			else
				stateText = " " + stateText.PadRight(19);

			Content.Write(0, 0, stateText, null, ConsoleColor.White, GetStateColor());
		} // proc OnRender

		private void SetStateCore(ConnectionState state, bool? set)
		{
			var newState = set.HasValue
				? (set.Value ? state | currentState : ~state & currentState)
				: state;

			if (newState != currentState)
			{
				currentState = newState;
				Invalidate();
			}
		} // proc SetStateCore

		private void SetPathCore(string newPath)
		{
			if(newPath != currentPath)
			{
				currentPath = newPath;
				Invalidate();
			}
		} // proc SetPathCore

		public void SetState(ConnectionState state, bool? set)
			=> Application?.Invoke(() => SetStateCore(state, set));

		public void SetPath(string path)
			=> Application?.Invoke(() => SetPathCore(path));
	} // class ConnectionStateOverlay

	#endregion

	#region -- class ActivityOverlay --------------------------------------------------

	internal sealed class ActivityOverlay : ConsoleOverlay
	{
		private const ConsoleColor backgroundColor = ConsoleColor.DarkCyan;

		#region -- class LastLogLine --------------------------------------------------

		private sealed class LastLogLine
		{
			private readonly string path;
			private readonly LogMsgType type;
			private readonly string text;

			public LastLogLine(string path, LogMsgType type, string text)
			{
				this.path = path ?? throw new ArgumentNullException(nameof(path));
				this.type = type;
				this.text = text ?? throw new ArgumentNullException(nameof(text));
			} // ctor

			public string Path => path;
			public LogMsgType Type => type;
			public string Text => text;
		} // class LastLogLine

		#endregion

		#region -- class LastProperty -------------------------------------------------

		private sealed class LastProperty
		{
			private readonly string path;
			private readonly string name;

			private PropertyInfo propertyInfo = null;
			private string value;
			private int lastUpdated;

			public LastProperty(string path, string name, string value)
			{
				this.path = path ?? throw new ArgumentNullException(nameof(path));
				this.name = name ?? throw new ArgumentNullException(nameof(name));

				SetValue(value ?? String.Empty);
			} // ctor

			public void SetValue(string value)
			{
				this.value = value;
				lastUpdated = Environment.TickCount;
			} // proc SetValue

			public void SetPropertyInfo(PropertyInfo pi)
				=> propertyInfo = pi;

			public string Path => path;
			public string Name => name;
			public string DisplayName => propertyInfo == null ? name : propertyInfo.Name;
			public string Value => propertyInfo == null ? value : propertyInfo.FormatValue(value);

			public bool HasPropertyInfo => propertyInfo != null;

			public int Score => unchecked(Environment.TickCount - lastUpdated);
		} // class LastProperty
		#endregion

		#region -- class PropertyInfo -------------------------------------------------

		private sealed class PropertyInfo
		{
			private readonly string name;
			private readonly Type type;
			private readonly string format;

			public PropertyInfo(string name, Type type, string format)
			{
				this.name = name ?? throw new ArgumentNullException(nameof(name));
				this.type = type ?? throw new ArgumentNullException(nameof(type));
				this.format = format;
			} // ctor

			public string FormatValue(string value)
			{
				try
				{
					var v = Procs.ChangeType(value, type);
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
			public string Format => format;
		} // class PropertyInfo

		#endregion

		private readonly DEHttpClient http;
		private readonly LastLogLine[] lastLogs;
		private readonly LastProperty[] lastProperties;

		private readonly Dictionary<string, int> lastLogNumber = new Dictionary<string, int>();
		private readonly Dictionary<string, PropertyInfo> lastPropertyInfo = new Dictionary<string, PropertyInfo>();

		private int logPathWidth = 5;
		private int logTextWidth = 5;
		private int propertyPathWidth = 5;
		private int propertyNameWidth = 5;
		private int propertyValueWidth = 5;

		public ActivityOverlay(DEHttpClient http, int height)
		{
			this.http = http ?? throw new ArgumentNullException(nameof(http));

			// create buffer
			lastLogs = new LastLogLine[height];
			lastProperties = new LastProperty[height];

			// set position
			Left = 0;
			Top -= height;
			Position = ConsoleOverlayPosition.Window;
		} // ctor

		private int WriteCellText(int left, int top, string value, int width, ConsoleColor foregroundColor)
		{

			if (String.IsNullOrEmpty(value))
			{
				Content.Fill(left, top, left + width - 1, top, ' ', foregroundColor, backgroundColor);
				return left + width;
			}
			else
			{
				Content.Set(left, top, ' ', foregroundColor, backgroundColor);
				if (value.Length > width - 2) // make it shorter
				{
					var tmp = value.Substring(0, width - 5) + "...";
					if (tmp.Length > width)
						tmp = new string('#', width);
					(left, _) = Content.Write(left + 1, top, tmp, null, foregroundColor, backgroundColor);
					Content.Set(left, top, ' ', foregroundColor, backgroundColor);
					return left + 1;
				}
				else // print string
				{
					(left, _) = Content.Write(left + 1, top, value, null, foregroundColor, backgroundColor);
					var endLeft = left + width - value.Length - 2;
					Content.Fill(left, top, endLeft, top, ' ', foregroundColor, backgroundColor);
					return endLeft + 1;
				}
			}
		} // func WriteCellText

		protected override void OnRender()
		{
			if (Content == null)
				return;

			ConsoleColor GetLogColor(LogMsgType type)
			{
				switch(type)
				{
					case LogMsgType.Error:
						return ConsoleColor.DarkRed;
					case LogMsgType.Warning:
						return ConsoleColor.Yellow;
					default:
						return ConsoleColor.White;
				}
			} // func GetLogColor

			for (var i = 0; i < lastLogs.Length; i++)
			{
				var left = 0;
				ref var ll = ref lastLogs[i];
				if (ll != null)
				{
					left = WriteCellText(left, i, ll.Path, logPathWidth, ConsoleColor.Gray);
					left = WriteCellText(left, i, ll.Text, logTextWidth, GetLogColor(ll.Type));
				}
				else
					left = WriteCellText(left, i, null, logPathWidth + logTextWidth, ConsoleColor.Yellow);

				Content.Set(left++, i, ' ', ConsoleColor.Black, ConsoleColor.Black);

				ref var lp = ref lastProperties[i];
				if (lp != null)
				{
					left = WriteCellText(left, i, lp.Path, propertyPathWidth, ConsoleColor.Gray);
					left = WriteCellText(left, i, lp.DisplayName, propertyNameWidth, ConsoleColor.Yellow);
					_ = WriteCellText(left, i, lp.Value, propertyValueWidth, ConsoleColor.White);
				}
				else
					WriteCellText(left, i, null, propertyPathWidth + propertyNameWidth + propertyValueWidth, ConsoleColor.Yellow);
			}
		} // proc OnRender

		public override void OnResize()
		{
			if (Application == null)
				return;

			var newWidth = Application.WindowRight - Application.WindowLeft + 1;
			if (Width != newWidth)
			{
				Resize(newWidth, lastLogs.Length);

				// calculate table offsets
				//   2    1
				// 20 r  20 15 r
				// p  t  p  n  v
				var logWidth = newWidth * 2 / 3;
				var propWidth = newWidth - logWidth - 1;

				logPathWidth = logWidth > 50 ? 20 : logWidth * 20 / 50;
				logTextWidth = logWidth - logPathWidth;

				propertyPathWidth = propWidth > 60 ? 20 : propWidth * 20 / 60;
				propertyNameWidth = propWidth > 60 ? 25 : propWidth * 25 / 60;
				propertyValueWidth = propWidth - propertyPathWidth - propertyNameWidth;

				// repaint
				Invalidate();
			}
		} // proc OnResize

		protected override void OnAdded()
		{
			base.OnAdded();
			OnResize();
		} // proc OnAdded

		private static LogMsgType GetFromString(string t)
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

		private async Task GetLogLineAsync(string path, int start, int count)
		{
			var xLines = await http.GetXmlAsync(Program.MakeUri(path,
				new PropertyValue("action", "listget"),
				new PropertyValue("id", "tw_lines"),
				new PropertyValue("desc", false),
				new PropertyValue("start", start),
				new PropertyValue("count", count)
			), rootName: "list");

			foreach (var x in xLines.Element("items").Elements("line"))
			{
				// move lines
				for (var i = lastLogs.Length - 2; i >= 0; i--)
					lastLogs[i + 1] = lastLogs[i];

				// set new line
				lastLogs[0] = new LastLogLine(
					path,
					GetFromString(x.GetAttribute("typ", "I")),
					x.Value
				);
			}

			Invalidate();
		} // proc GetLogLineAsync

		private readonly List<string> currentFetching = new List<string>();

		private bool TryGetPropertyInfo(LastProperty lp, out PropertyInfo pi)
			=> lastPropertyInfo.TryGetValue(lp.Path + ":" + lp.Name, out pi);

		private async Task GetPropertyInfos(string path)
		{
			// lock property fetch
			if (currentFetching.IndexOf(path) >= 0)
				return;
			currentFetching.Add(path);
			try
			{
				var xProperties = await http.GetXmlAsync(Program.MakeUri(path,
					new PropertyValue("action", "listget"),
					new PropertyValue("id", "tw_properties")
				), rootName: "list");

				foreach (var x in xProperties.Element("items").Elements("property"))
				{
					var name = x.GetAttribute("name", null);
					if (name == null)
						continue;

					var displayName = x.GetAttribute("displayname", name);
					var format = x.GetAttribute("format", null);
					var type = LuaType.GetType(x.GetAttribute("type", "string"), lateAllowed: true).Type ?? typeof(string);

					lastPropertyInfo[path + ":" + name] = new PropertyInfo(displayName, type, format);
				}

				// update current properties
				for (var i = 0; i < lastProperties.Length; i++)
				{
					var lp = lastProperties[i];
					if (lp != null
						&& !lp.HasPropertyInfo
						&& TryGetPropertyInfo(lp, out var pi))
					{
						lp.SetPropertyInfo(pi);
					}
				}
			}
			finally
			{
				currentFetching.Remove(path);
			}
			Invalidate();
		} // func GetPropertyInfos

		internal void EventReceived(object sender, DEHttpSocketEventArgs e)
		{
			if (e.Id == "tw_lines") // log line event
			{
				var lineCount = e.Values.GetAttribute("lineCount", -1);
				if (lineCount > 0)
				{
					var count = 1;
					// check last log line count, to get the difference
					if (lastLogNumber.TryGetValue(e.Path, out var lastLineCount))
					{
						if (lastLineCount < lineCount) // log not truncated calculate difference
						{
							count = lineCount - lastLineCount;
							if (count > lastLogs.Length)
								count = lastLogs.Length;
						}
					}
					lastLogNumber[e.Path] = lineCount;

					// get log info async
					GetLogLineAsync(e.Path, lineCount - count, count)
						.ContinueWith(t=>
						{
							Debug.Print(t.Exception.ToString());
						}, TaskContinuationOptions.OnlyOnFaulted
					);
				}
			}
			else if (e.Id == "tw_properties") // log property changed
			{
				if (e.Index.StartsWith("tw_log_"))
					return; // no log properties

				// is the property in the list
				var idx = Array.FindIndex(lastProperties, p => p != null && p.Path == e.Path && p.Name == e.Index);
				if (idx >= 0)
					lastProperties[idx].SetValue(e.Values?.Value);
				else
				{
					// find oldest item
					var lastIndex = 0;
					if (lastProperties[0] != null)
					{
						for (var i = 1; i < lastProperties.Length; i++)
						{
							if (lastProperties[i] == null)
							{
								lastIndex = i;
								break;
							}
							else if (lastProperties[i].Score > lastProperties[lastIndex].Score)
								lastIndex = i;
						}
					}

					var newProperty = new LastProperty(e.Path, e.Index, e.Values?.Value);
					if (TryGetPropertyInfo(newProperty, out var pi))
						newProperty.SetPropertyInfo(pi);
					else
					{
						GetPropertyInfos(e.Path)
							.ContinueWith(t =>
							{
								Debug.Print(t.Exception.ToString());
							}, TaskContinuationOptions.OnlyOnFaulted
						);
					}
					lastProperties[lastIndex] = newProperty;
				}

				Invalidate();
			}
		}
	} // class ActivityOverlay

	#endregion

	#region -- class SelectListOverlay ------------------------------------------------

	internal sealed class SelectListOverlay : ConsoleDialogOverlay
	{
		private readonly KeyValuePair<object, string>[] values;
		private int selectedIndex = -1;
		private string title;

		public SelectListOverlay(ConsoleApplication app, IEnumerable<KeyValuePair<object, string>> values)
		{
			this.values = values.ToArray() ?? throw new ArgumentNullException(nameof(values));

			Application = app ?? throw new ArgumentNullException(nameof(values));

			var windowWidth = app.WindowRight - app.WindowLeft + 1;
			var windowHeight = app.WindowBottom - app.WindowTop + 1;

			var maxWidth = this.values.Max(GetLineLength) + 2;
			var maxHeight = this.values.Length + 1;

			Position = ConsoleOverlayPosition.Window;
			Resize(
				Math.Min(windowWidth, maxWidth),
				Math.Min(windowHeight, maxHeight)
			);

			Left = (windowWidth - Width) / 2;
			Top = (windowHeight - Height) / 2;
		} // ctor

		protected override void OnRender()
		{
			RenderTitle("Use");

			for (var i = 0; i < values.Length; i++)
			{
				var top = i + 1;
				Content.Set(0, top, ' ', background: BackgroundColor);
				var foregroundColor = i == selectedIndex ? ConsoleColor.Black : ForegroundColor;
				var backgroundColor = i == selectedIndex ? ConsoleColor.Cyan : BackgroundColor;
				var (endLeft, _) = Content.Write(1, top, values[i].Value, foreground: foregroundColor, background: backgroundColor);
				if (endLeft < Width)
					Content.Fill(endLeft, top, Width - 2, top, ' ', background: backgroundColor);
				Content.Set(Width - 1, top, ' ', background: BackgroundColor);
			}
		} // proc OnRender

		public override bool OnHandleEvent(EventArgs e)
		{
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.Key)
				{
					case ConsoleKey.DownArrow:
						SelectIndex(selectedIndex + 1);
						return true;
					case ConsoleKey.UpArrow:
						SelectIndex(selectedIndex - 1);
						return true;
				}
			}

			return base.OnHandleEvent(e);
		} // func OnHandleEvent

		private void SelectValue(object key)
			=> SelectIndex(Array.FindIndex(values, v => Equals(v.Key, key)));

		public void SelectIndex(int newIndex)
		{
			if (newIndex == -1)
			{
				selectedIndex = -1;
				Invalidate();
			}
			else if (newIndex >= 0 && newIndex < values.Length && newIndex != selectedIndex)
			{
				selectedIndex = newIndex;
				Invalidate();
			}
		} // proc SelectIndex

		protected override void OnAccept()
		{
			if (selectedIndex == -1)
				return;
			base.OnAccept();
		} // proc OnAccept

		private static int GetLineLength(KeyValuePair<object, string> p)
			=> p.Value?.Length ?? 0;

		public string Title
		{
			get => title;
			set
			{
				if (title != value)
				{
					title = value;
					Invalidate();
				}
			}
		} // proc Title

		public object SelectedValue
		{
			get => selectedIndex >= 0 && selectedIndex < values.Length ? values[selectedIndex].Key : null;
			set => SelectValue(value);
		} // prop SelectedValue
	} // class UseListOverlay 

	#endregion

	#region -- class ConsoleView ------------------------------------------------------

	internal static class ConsoleView
	{
		#region -- WriteError, WriteWarning -------------------------------------------

		public static void WriteWarning(this ConsoleApplication app, string message)
			=> WriteLine(app, ConsoleColor.DarkYellow, message);

		public static void WriteError(this ConsoleApplication app, string message)
			=> WriteLine(app, ConsoleColor.Red, message);

		public static void WriteError(this ConsoleApplication app, Exception exception, string message = null)
		{
			if (exception == null)
				return;

			if (!String.IsNullOrEmpty(message))
				WriteError(app, message);

			if (exception is AggregateException aggEx)
			{
				foreach (var ex in aggEx.InnerExceptions)
					WriteError(app, ex);
			}
			else
			{
				// write exception
				using (app.Color(ConsoleColor.Red))
				{
					if (exception is DebugSocketException cde)
						app.WriteLine($"[R:{cde.ExceptionType}]");
					else
						app.WriteLine($"[{exception.GetType().Name}]");

					app.WriteLine($"  {exception.Message}");
				}

				// chain exceptions
				WriteError(app, exception.InnerException);
			}
		} // proc WriteError

		#endregion

		#region -- WriteLine ----------------------------------------------------------

		public static void WriteObject(this ConsoleApplication app, object o)
			=> app.WriteLine(o == null ? "<null>" : o.ToString());

		public static void Write(this ConsoleApplication app, ConsoleColor[] colors, string[] parts)
		{
			for (var i = 0; i < parts.Length; i++)
			{
				if (parts[i] == null)
					continue;
				using (app.Color(colors[i]))
					app.Write(parts[i]);
			}
		} // proc Write

		public static void WriteLine(this ConsoleApplication app, ConsoleColor color, string text)
		{
			using (app.Color(color))
				app.WriteLine(text);
		} // proc WriteLine

		public static void WriteLine(this ConsoleApplication app, ConsoleColor[] colors, string[] parts, bool rightAlign = false)
		{
			if (rightAlign)
				MoveRight(app, parts.Sum(c => c?.Length ?? 0) + 1);

			Write(app, colors, parts);
			app.WriteLine();
		} // proc WriteLine

		public static void MoveRight(this ConsoleApplication app, int right)
			=> app.CursorLeft = app.WindowRight - right + 1;

		#endregion
	} // class ConsoleView

	#endregion
}
