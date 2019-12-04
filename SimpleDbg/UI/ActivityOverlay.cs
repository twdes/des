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
using Neo.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using TecWare.DE.Networking;
using TecWare.DE.Server.Data;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ActivityOverlay --------------------------------------------------

	internal sealed class ActivityOverlay : ConsoleOverlay
	{
		private const ConsoleColor backgroundColor = ConsoleColor.DarkCyan;

		#region -- class LastLogLine --------------------------------------------------

		private struct LastLogLine
		{
			public string Path;
			public LogLine Line;
		} // class LastLogLine

		#endregion

		#region -- class LastProperty -------------------------------------------------

		private sealed class LastProperty
		{
			private readonly string path;
			private readonly string name;

			private LogPropertyInfo propertyInfo = null;
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
				if (this.value != value)
				{
					this.value = value;
					lastUpdated = Environment.TickCount;
				}
			} // proc SetValue

			public void SetPropertyInfo(LogPropertyInfo pi)
				=> propertyInfo = pi;

			public string Path => path;
			public string Name => name;
			public string DisplayName => propertyInfo?.DisplayName ?? name;
			public string Value => propertyInfo?.FormatValue(value) ?? value;

			public bool HasPropertyInfo => propertyInfo != null;

			public int Score => unchecked(Environment.TickCount - lastUpdated);
		} // class LastProperty
		#endregion

		private readonly DEHttpClient http;
		private readonly LastLogLine[] lastLogs;
		private readonly LastProperty[] lastProperties;

		private readonly Dictionary<string, int> lastLogNumber = new Dictionary<string, int>();

		private CharBufferTable logTable = null;
		private CharBufferTable propertyTable = null;

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
				
		protected override void OnRender()
		{
			var content = Content;
			if (logTable == null || propertyTable == null || content == null)
				return;

			ConsoleColor GetLogColor(LogMsgType type)
			{
				switch (type)
				{
					case LogMsgType.Error:
						return ConsoleColor.DarkRed;
					case LogMsgType.Warning:
						return ConsoleColor.Yellow;
					default:
						return ConsoleColor.White;
				}
			} // func GetLogColor

			var logColorMask = new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.Gray, ConsoleColor.Yellow };
			var propertyColorMask = new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.Yellow, ConsoleColor.White };
			var values = new object[3];
			for (var i = 0; i < lastLogs.Length; i++)
			{
				var left = 0;
				ref var ll = ref lastLogs[i];
				if (ll.Line != null)
				{
					values[0] = ll.Path;
					values[1] = ll.Line.Stamp.ToString("HH:mm:ss");
					values[2] = ll.Line.Text;
					logColorMask[2] = GetLogColor(ll.Line.Type);
					left = logTable.Write(content, left, i, values, logColorMask, backgroundColor);
				}
				else
					left = logTable.WriteEmpty(content, left, i, backgroundColor);

				content.Set(left++, i, ' ', ConsoleColor.Black, ConsoleColor.Black);

				ref var lp = ref lastProperties[i];
				if (lp != null)
				{
					values[0] = lp.Path;
					values[1] = lp.DisplayName;
					values[2] = lp.Value;
					propertyTable.Write(content, left, i, values, propertyColorMask, backgroundColor);
				}
				else
					propertyTable.WriteEmpty(content, left, i, backgroundColor);
			}
		} // proc OnRender

		protected override void OnParentResize()
		{
			base.OnParentResize();

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

				logTable = CharBufferTable.Create(logWidth, 
					TableColumn.Create("path", typeof(string), -2, maxWidth: 20),
					TableColumn.Create("time", typeof(string), 8, minWidth: 5, maxWidth: 5),
					TableColumn.Create("text", typeof(string), -4)
				);

				propertyTable = CharBufferTable.Create(propWidth,
					TableColumn.Create("path", typeof(string), -20, maxWidth: 20),
					TableColumn.Create("name", typeof(string), -25, maxWidth: 30),
					TableColumn.Create("value", typeof(string), -15)
				);

				// repaint
				Invalidate();
			}
		} // proc OnParentResize

		private void AppendLogLine(string path, LogLine line)
		{
			// move lines
			for (var i = lastLogs.Length - 2; i >= 0; i--)
				lastLogs[i + 1] = lastLogs[i];

			// set new line
			lastLogs[0].Path = path;
			lastLogs[0].Line = line;

			Invalidate();
		} // proc AppendLogLine

		internal void EventReceived(object sender, DEHttpSocketEventArgs e)
		{
			if (LogLine.TryGetLogEvent(e, out var lineCount)) // log line event
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
				LogLine.GetLogLinesAsync(http, e.Path, lineCount - count, count, AppendLogLine).Silent();
			}
			else if (e.Id == "tw_properties") // log property changed
			{
				if (e.Index.StartsWith("tw_log_")) // ignore log changes
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
					LogProperty.GetLogPropertyInfoAsync(http, e.Path, e.Index)
						.ContinueWith(t =>
						{
							try
							{
								newProperty.SetPropertyInfo(t.Result);
								Invalidate();
							}
							catch (Exception ex)
							{
								Debug.Print(ex.GetInnerException().ToString());
							}
						},
						TaskContinuationOptions.ExecuteSynchronously
					);
					lastProperties[lastIndex] = newProperty;
				}

				Invalidate();
			}
		}
	} // class ActivityOverlay

	#endregion
}
