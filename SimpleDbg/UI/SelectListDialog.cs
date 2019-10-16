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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TecWare.DE.Server.Data;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ListDialogBase ---------------------------------------------------

	internal abstract class ListDialogBase : ConsoleDialogOverlay
	{
		private readonly IEnumerable source;

		private string title = String.Empty;

		private int firstLineIndex = 0;
		private int selectedIndex = -1;

		public ListDialogBase(ConsoleApplication app, IEnumerable source)
		{
			this.source = source ?? throw new ArgumentNullException(nameof(source));

			Application = app ?? throw new ArgumentNullException(nameof(app));
		} // ctor

		protected void ResizeToContent()
		{
			// calculate window size
			var maxWidth = 10;
			var maxHeight = 0;

			foreach (var c in source)
			{
				var width = (FormatLine(c)?.Length ?? 0) + 2;
				if (width > maxWidth)
					maxWidth = width;
				maxHeight++;
			}

			var app = Application;
			var windowWidth = app.WindowRight - app.WindowLeft + 1;
			var windowHeight = app.WindowBottom - app.WindowTop + 1;

			Position = ConsoleOverlayPosition.Window;
			Resize(
				Math.Min(windowWidth, maxWidth),
				Math.Min(windowHeight, maxHeight + 1)
			);

			Left = (windowWidth - Width) / 2;
			Top = (windowHeight - Height) / 2;
		} // proc ResizeToContent

		protected virtual int GetCount()
			=> source is IList l ? l.Count : -1;

		protected virtual object GetLine(int index)
			=> source is IList list ? list[index] : GetLines(index, 1).OfType<object>().FirstOrDefault();

		protected virtual IEnumerable GetLines(int startAt, int count)
		{
			if (source is IList list)
			{
				for (var i = 0; i < count; i++)
				{
					var idx = i + startAt;
					if (idx >= list.Count)
						yield break;
					else
						yield return list[idx];
				}
			}
			else
			{
				var e = source.GetEnumerator();
				try
				{
					var idx = 0;
					while (idx < firstLineIndex)
					{
						if (!e.MoveNext())
							yield break;
					}

					var endAt = startAt + count;
					while (idx < endAt)
					{
						if (e.MoveNext())
							yield return e.Current;
						else
							yield break;
					}
				}
				finally
				{
					if (e is IDisposable d)
						d.Dispose();
				}
			}
		} // func GetLines

		protected virtual object GetSelectedValue(object line)
			=> line;

		protected virtual string FormatLine(object line)
			=> line?.ToString().GetFirstLine();

		protected virtual void RenderLine(object line, int left, int top, int right, bool selected)
		{
			var content = TableColumn.TableStringPad(FormatLine(line), right - left + 1);
			Content.Write(left, top, content, null,
				selected ? ForegroundHighlightColor : ForegroundColor,
				selected ? BackgroundHighlightColor : BackgroundColor
			);
		} // proc RenderLine

		protected void RenderList(int left, int top, int right, int bottom)
		{
			var y = top;
			var idx = firstLineIndex;
			foreach (var cur in GetLines(firstLineIndex, bottom - top + 1))
			{
				RenderLine(cur, left, y, right, selectedIndex == idx);
				idx++;
				y++;
			}

			// clear rest
			Content.Fill(left, y, right, bottom, ' ', ForegroundColor, BackgroundColor);
		} // proc RenderList

		protected override void OnRender()
		{
			if (Content == null)
				return;

			// render title
			var top = 0;
			if (!String.IsNullOrEmpty(title))
			{
				RenderTitle(title);
				top = 1;
			}

			// render list full size
			RenderList(1, top, Content.Width - 2, Content.Height - 1);

			// render frame
			var leftSite = Content.Width - 1;
			Content.Fill(0, top, 0, Content.Height - 1, ' ', ForegroundColor, BackgroundColor);
			Content.Fill(leftSite, top, leftSite, Content.Height - 1, ' ', ForegroundColor, BackgroundColor);
		} // proc OnRender

		private void SelectValue(object key)
		{
			var idx = 0;
			foreach (var c in source)
			{
				if (Equals(key, GetSelectedValue(c)))
					SelectIndex(idx);
				idx++;
			}
		} // proc SelectValue
   
		public void SelectIndex(int newIndex, bool allowInvalidIndex = true)
		{
			if (newIndex == -1 && allowInvalidIndex)
			{
				selectedIndex = -1;
				Invalidate();
			}
			else if (newIndex >= 0 && newIndex < GetCount() && newIndex != selectedIndex)
			{
				selectedIndex = newIndex;
				Invalidate();
			}
		} // proc SelectIndex

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
						SelectIndex(selectedIndex - 1, false);
						return true;
				}
			}

			return base.OnHandleEvent(e);
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

		public int SelectedIndex => selectedIndex;

		public object SelectedValue
		{
			get => GetSelectedValue(GetLine(selectedIndex));
			set => SelectValue(value);
		} // prop SelectedValue

		protected IEnumerable Source => source;

		public ConsoleColor BackgroundHighlightColor { get; set; } = ConsoleColor.Cyan;
		public ConsoleColor ForegroundHighlightColor { get; set; } = ConsoleColor.Black;
	} // class ListDialogBase

	#endregion

	#region -- class LogViewDialog ----------------------------------------------------

	internal sealed class LogViewDialog : ListDialogBase
	{
		public LogViewDialog(ConsoleApplication app, IReadOnlyList<LogLine> lines)
			: base(app, lines)
		{
			Resize(app.WindowRight - 3, app.WindowBottom - 1);
			Left = 2;
			Top = 1;
			Position = ConsoleOverlayPosition.Window;
		} // ctor

		protected override object GetLine(int index)
			=> Lines[index];

		protected override int GetCount()
			=> Lines.Count;

		protected override string FormatLine(object line) 
			=> base.FormatLine(line);

		private IReadOnlyList<LogLine> Lines => (IReadOnlyList<LogLine>)base.Source;
	} // class LogViewDialog

	#endregion

	#region -- class SelectListDialog -------------------------------------------------

	internal sealed class SelectListDialog : ListDialogBase
	{
		public SelectListDialog(ConsoleApplication app, IEnumerable<KeyValuePair<object, string>> values)
			: base(app, values.ToArray())
		{
			ResizeToContent();
		} // ctor

		protected override object GetSelectedValue(object line)
			=> ((KeyValuePair<object, string>)line).Key;

		protected override string FormatLine(object line)
			=> ((KeyValuePair<object, string>)line).Value.GetFirstLine();

		protected override void OnAccept()
		{
			if (SelectedIndex == -1)
				return;
			base.OnAccept();
		} // proc OnAccept
	} // class SelectListDialog 

	#endregion
}
