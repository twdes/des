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
using System.Linq;
using Neo.Console;
using TecWare.DE.Server.Data;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ListDialogBase ---------------------------------------------------

	internal abstract class ListDialogBase<T> : ConsoleDialogOverlay
		where T : class
	{
		private readonly ListView<T> list;

		private string title = String.Empty;

		public ListDialogBase(ConsoleApplication app, IEnumerable<T> source)
		{
			Application = app ?? throw new ArgumentNullException(nameof(app));

			InsertControl(0, list = CreateListView());
			list.Source = source;

			Resize(app.WindowRight - 3, app.WindowBottom - 1);
			Left = 2;
			Top = 1;
			Position = ConsoleOverlayPosition.Window;

			OnResize();
		} // ctor

		protected virtual ListView<T> CreateListView()
			=> new ListView<T>();

		protected void ResizeToContent()
		{
			// calculate window size
			var maxWidth = (Title?.Length ?? 0) + 8;
			var maxHeight = 0;

			foreach (var c in list.Source)
			{
				var width = list.GetLineText(-1, c, Int32.MaxValue).Length;
				if (width > maxWidth)
					maxWidth = width;
				maxHeight++;
			}

			var app = Application;
			var windowWidth = app.WindowRight - app.WindowLeft + 1;
			var windowHeight = app.WindowBottom - app.WindowTop - app.ReservedBottomRowCount + 1;

			Position = ConsoleOverlayPosition.Window;
			Resize(
				Math.Min(windowWidth - 4, maxWidth + 2),
				Math.Min(windowHeight - 2, maxHeight + 2)
			);

			Left = (windowWidth - Width) / 2;
			Top = (windowHeight - Height) / 2;
			
			OnResize();
		} // proc ResizeToContent

		protected override void OnRender()
		{
			RenderFrame(title);
		} // proc OnRender

		public override void OnResize()
		{
			base.OnResize();

			list.Resize(Width - 1, Height - 2, true);
			list.Left = Left + 1;
			list.Top = Top + 1;
		} // proc OnResize

		public override bool OnHandleEvent(EventArgs e)
		{
			return list.OnHandleEvent(e)
				|| base.OnHandleEvent(e);
		} // func OnHandleEvent

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
		} // prop Title

		public int SelectedIndex => list.SelectedIndex;

		public IEnumerable Source => list.Source;
		public ListView<T> View => list;

		public ConsoleColor BackgroundHighlightColor { get; set; } = ConsoleColor.Cyan;
		public ConsoleColor ForegroundHighlightColor { get; set; } = ConsoleColor.Black;
	} // class ListDialogBase

	#endregion

	#region -- class LogViewDialog ----------------------------------------------------

	internal sealed class LogViewDialog : ListDialogBase<LogLine>
	{
		#region -- class SelectListView -----------------------------------------------

		internal sealed class LogListView : ListView<LogLine>
		{
			public override string GetLineText(int index, LogLine item, int width)
			{
				var time = index > 0 && index != FirstVisibleLine && GetItem(index - 1).Stamp.Date == item.Stamp.Date
					? item.Stamp.ToString("       HH:mm:ss,fff") // only hours
					: item.Stamp.ToString("dd.MM. HH:mm:ss,fff"); // full stamp
				
				return time + " " + LogLine.ToMsgTypeString(item.Type) + " " + item.Text.GetFirstLine();
			} // func GetLineText

			protected override ConsoleColor GetLineTextColor(int i, LogLine item, bool selected)
			{
				if (selected)
					return ForegroundHighlightColor;
				else
				{
					switch (item.Type)
					{
						case LogMsgType.Error:
							return ConsoleColor.DarkRed;
						case LogMsgType.Warning:
							return ConsoleColor.Yellow;
						default:
							return ConsoleColor.Gray;
					}
				}
			} // func GetLineTextColor
		} // class LogListView

		#endregion

		public LogViewDialog(ConsoleApplication app, IReadOnlyList<LogLine> lines)
			: base(app, lines)
		{
		} // ctor

		protected override ListView<LogLine> CreateListView()
			=> new LogListView();
	} // class LogViewDialog

	#endregion

	#region -- class SelectPairItem ---------------------------------------------------

	internal class SelectPairItem<T>
	{
		public SelectPairItem(T key, string text)
		{
			Key = key;
			Text = text;
		} // ctor

		public SelectPairItem(KeyValuePair<T, string> pair)
			: this(pair.Key, pair.Value)
		{
		} // ctor

		public T Key { get; }
		public string Text { get; }
	} // class SelectPairItem<T>

	#endregion

	#region -- class SelectListDialog -------------------------------------------------

	internal sealed class SelectListDialog<T> : ListDialogBase<SelectPairItem<T>>
	{
		#region -- class SelectListView -----------------------------------------------

		internal sealed class SelectListView : ListView<SelectPairItem<T>>
		{
			public override string GetLineText(int index, SelectPairItem<T> item, int width)
			{
				var text = item.Text;
				if (text == null)
					return null;
				else
				{
					var firstLine = text.GetFirstLine();
					if (firstLine.Length > width - 2 && firstLine.Length > 3)
						return " " + firstLine.Substring(0, width - 3) + "... ";
					else
						return " " + firstLine + " ";
				}
			} // func GetLineText
		} // class SelectListView

		#endregion

		public SelectListDialog(ConsoleApplication app, IEnumerable<SelectPairItem<T>> values)
			: base(app, values.ToArray())
		{
			ResizeToContent();
		} // ctor

		protected override ListView<SelectPairItem<T>> CreateListView()
			=> new SelectListView();

		protected override void OnAccept()
		{
			if (SelectedIndex == -1)
				return;
			base.OnAccept();
		} // proc OnAccept

		public T SelectedValue
		{
			get
			{
				var tmp = View.SelectedItem;
				return tmp == null ? default : tmp.Key;
			}
			set => View.SelectIndex(View.GetItemIndex(c => Equals(c.Key, value)));
		} // proc SelectedValue
	} // class SelectListDialog 

	#endregion
}
