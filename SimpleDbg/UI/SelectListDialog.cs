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
using System.Text;
using Neo.Console;
using TecWare.DE.Server.Data;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ListDialogBase ---------------------------------------------------

	internal abstract class ListDialogBase<T> : ConsoleDialogOverlay
		where T : class
	{
		private readonly ListViewOverlay<T> list;
		private readonly ReadLineOverlay filter;

		private readonly SequenceTimer timer;

		private string title = String.Empty;

		public ListDialogBase(ConsoleApplication app, IEnumerable<T> source)
		{
			timer = new SequenceTimer(SetFilterstringCore);

			InsertControl(0, list = CreateListView());
			InsertControl(1, filter = new ReadLineOverlay()
			{
				ForegroundColor = ConsoleColor.Yellow,
				BackgroundColor = ConsoleColor.DarkBlue
			});

			list.Source = source;
			filter.CursorChanged += Filter_CursorChanged;
			filter.TextChanged += Filter_TextChanged;

			Filter_CursorChanged(filter, EventArgs.Empty);

			Application = app ?? throw new ArgumentNullException(nameof(app));
		} // ctor

		protected virtual ListViewOverlay<T> CreateListView()
			=> new ListViewOverlay<T>();

		protected virtual bool ResizeToContent => false;

		protected sealed override void GetResizeConstraints(out int maxWidth, out int maxHeight, out bool includeReservedRows)
		{
			if (ResizeToContent)
			{
				// calculate max window size
				maxWidth = (Title?.Length ?? 0) + 30;
				maxHeight = 2;
				includeReservedRows = false;

				foreach (var c in list.Source)
				{
					var width = list.GetLineText(-1, c, Int32.MaxValue).Length;
					if (width > maxWidth)
						maxWidth = width;
					maxHeight++;
				}
			}
			else
				base.GetResizeConstraints(out maxWidth, out maxHeight, out includeReservedRows);
		} // func GetResizeConstraints

		protected override void OnRender()
		{
			RenderFrame(title);
		} // proc OnRender

		protected override void OnResizeContent()
		{
			base.OnResizeContent();

			list.Resize(Width - 1, Height - 2, true);
			list.Left = Left + 1;
			list.Top = Top + 1;

			filter.Resize(Math.Min(Width - 4, 20), 1);
			filter.Left = Left + Width - filter.Width - 3;
			filter.Top = Top + Height - 1;
			Filter_CursorChanged(filter, EventArgs.Empty);
		} // proc OnResize

		protected virtual void SetFilterString(string text)
		{
		} // proc SetFilterString

		private void SetFilterstringCore()
			=> SetFilterString(filter.Text);

		private void Filter_TextChanged(object sender, EventArgs e)
			=> timer.Start(300);

		private void Filter_CursorChanged(object sender, EventArgs e)
			=> SetCursor(filter.Left + filter.CursorLeft - Left, filter.Top + filter.CursorTop - Top, filter.CursorSize);

		public override bool OnHandleEvent(EventArgs e)
		{
			return list.OnHandleEvent(e)
				|| filter.OnHandleEvent(e)
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
		public ListViewOverlay<T> View => list;
		
		public ConsoleColor BackgroundHighlightColor { get; set; } = ConsoleColor.Cyan;
		public ConsoleColor ForegroundHighlightColor { get; set; } = ConsoleColor.Black;
	} // class ListDialogBase

	#endregion

	#region -- class LogViewDialog ----------------------------------------------------

	internal sealed class LogViewDialog : ListDialogBase<LogLine>
	{
		#region -- class SelectListView -----------------------------------------------

		internal sealed class LogListView : ListViewOverlay<LogLine>
		{
			private readonly LogViewDialog parent;

			public LogListView(LogViewDialog parent)
				=> this.parent = parent ?? throw new ArgumentNullException(nameof(parent));

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

			protected override bool IsItemFiltered(LogLine cur) 
				=> parent.IsItemFiltered(cur);
		} // class LogListView

		#endregion

		#region -- class FilterRule ---------------------------------------------------

		private abstract class FilterRule
		{
			public abstract bool IsMatch(LogLine l);
		} // class FilterRule

		#endregion

		#region -- class TextFilterRule -----------------------------------------------

		private sealed class TextFilterRule : FilterRule
		{
			private readonly string text;

			public TextFilterRule(string text)
				=> this.text = text ?? throw new ArgumentNullException(nameof(text));

			public override bool Equals(object obj)
				=> obj is TextFilterRule o && String.Compare(o.text, text, StringComparison.CurrentCultureIgnoreCase) == 0;

			public override int GetHashCode()
				=> text.ToLower().GetHashCode();

			public override bool IsMatch(LogLine l)
				=> l.Text.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
		} // class TextFilterRule

		#endregion

		#region -- class TypeFilterRule -----------------------------------------------

		private sealed class TypeFilterRule : FilterRule
		{
			private readonly LogMsgType type;

			public TypeFilterRule(LogMsgType type)
				=> this.type = type;
			
			public override bool Equals(object obj)
				=> obj is TypeFilterRule o && o.type == type;

			public override int GetHashCode()
				=> type.GetHashCode();

			public override bool IsMatch(LogLine l)
				=> l.Type == type;
		} // class TypeFilterRule

		#endregion

		#region -- class DateFilterRule -----------------------------------------------

		private sealed class DateFilterRule : FilterRule
		{
			private readonly bool compareLower;
			private readonly DateTime stamp;

			public DateFilterRule(bool compareLower, DateTime stamp)
			{
				this.compareLower = compareLower;
				this.stamp = stamp;
			} // ctor

			public override bool Equals(object obj)
				=> obj is DateFilterRule o && o.compareLower == compareLower && o.stamp == stamp;

			public override int GetHashCode()
				=> compareLower.GetHashCode() ^ stamp.GetHashCode();
			
			public override bool IsMatch(LogLine l)
				=> compareLower ? l.Stamp <= stamp : l.Stamp >= stamp;
		} // class DateFilterRule

		#endregion

		private string originalFilterString = String.Empty;
		private List<FilterRule> filterRules = new List<FilterRule>();

		#region -- Ctor/Dtor ----------------------------------------------------------

		public LogViewDialog(ConsoleApplication app, IReadOnlyList<LogLine> lines)
			: base(app, lines)
		{
		} // ctor

		protected override void OnAdded()
		{
			base.OnAdded();
			if (SelectedIndex == -1)
				View.SelectLast();
		} // proc OnAdded

		#endregion

		#region -- SetFilter ----------------------------------------------------------

		private void AppendRule(List<FilterRule> newRules, ref int ruleTyp, StringBuilder buffer)
		{
			switch (ruleTyp)
			{
				case 0: // Text
					if (buffer.Length > 0)
						newRules.Add(new TextFilterRule(buffer.ToString()));
					break;
				case 1: // Typ
					if (buffer.Length > 0)
						switch (Char.ToUpper(buffer[0]))
						{
							case 'I':
								newRules.Add(new TypeFilterRule(LogMsgType.Information));
								break;
							case 'W':
								newRules.Add(new TypeFilterRule(LogMsgType.Warning));
								break;
							case 'E':
								newRules.Add(new TypeFilterRule(LogMsgType.Error));
								break;
							default:
								throw new Exception("Typ unbekannt.");
						}
					break;
				case 2: // Greater Date
				case 3: // Lower Date
					try
					{
						newRules.Add(new DateFilterRule(ruleTyp == 3, DateTime.Parse(buffer.ToString())));
					}
					catch (Exception)
					{
						throw new Exception("Datum nicht gelesen");
					}
					break;
			}
			ruleTyp = -1;
			buffer.Length = 0;
		} // proc AppendRule

		private static bool IsSameFilterRules(List<FilterRule> a, List<FilterRule> b)
		{
			if (a.Count != b.Count)
				return false;

			for (var i = 0; i < a.Count; i++)
			{
				if (!a[i].Equals(b[i]))
					return false;
			}

			return true;
		} // func IsSameFilterRules

		private bool SetFilterString(string filterString, out int pos, out string errorText)
		{
			pos = 0;
			errorText = null;

			if (filterString == originalFilterString)
				return false;

			// Parse die Filterregeln
			var newRules = new List<FilterRule>();
			if (!String.IsNullOrEmpty(filterString))
			{
				try
				{
					// Zerlege die Zeichenfolge
					var buffer = new StringBuilder();
					var ruleTyp = -1;
					var state = 0;
					foreach (var c in filterString)
					{
						pos++;
						switch (state)
						{
							#region -- 0 Basis --
							case 0:
								if (c == '"') // Complete text
								{
									state = 1;
									ruleTyp = 0;
								}
								else if (c == '#') // Search for LogMsg
								{
									ruleTyp = 1;
									state = 5;
								}
								else if (c == '>') // Search for lower date
								{
									ruleTyp = 2;
									state = 5;
								}
								else if (c == '<') // Search for greater date
								{
									ruleTyp = 3;
									state = 5;
								}
								else if (Char.IsWhiteSpace(c))
								{
									ruleTyp = 0;
									AppendRule(newRules, ref ruleTyp, buffer);
								}
								else
								{
									state = 5;
									ruleTyp = 0;
									buffer.Append(c);
								}
								break;
							#endregion
							#region -- 1-3 Text --
							case 1: // -- Text? --
								if (c == '"')
								{
									state = 0;
									buffer.Append(c);
								}
								else
								{
									buffer.Append(c);
									state = 2;
								}
								break;
							case 2:
								if (c == '"')
									state = 3;
								else
									buffer.Append(c);
								break;
							case 3:
								if (c == '"')
								{
									state = 2;
									buffer.Append(c);
								}
								else
								{
									AppendRule(newRules, ref ruleTyp, buffer);
									ruleTyp = -1;
									state = 0;
								}
								break;
							#endregion
							#region -- 5 Append Buffer --
							case 5:
								if (Char.IsWhiteSpace(c))
								{
									AppendRule(newRules, ref ruleTyp, buffer);
									state = 0;
								}
								else
									buffer.Append(c);
								break;
								#endregion
						}
					}

					// Füge die ausstehende Regel an
					AppendRule(newRules, ref ruleTyp, buffer);
				}
				catch (Exception e)
				{
					errorText = e.Message;
					return false;
				}
			}

			// Tausche den Filter aus
			if (IsSameFilterRules(filterRules, newRules))
				return false;

			filterRules.Clear();
			filterRules = newRules;
			originalFilterString = filterString;

			// Refresh list
			View.RefreshList();

			return true;
		} // proc SetFilterString

		protected override void SetFilterString(string text)
			=> SetFilterString(text, out var _, out var _);

		private bool IsItemFiltered(LogLine item)
			=> filterRules.Any(r => !r.IsMatch(item));

		#endregion

		protected override ListViewOverlay<LogLine> CreateListView()
			=> new LogListView(this);
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

		internal sealed class SelectListView : ListViewOverlay<SelectPairItem<T>>
		{
			private readonly SelectListDialog<T> parent;

			public SelectListView(SelectListDialog<T> parent)
			{
				this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
			} // ctor

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

			protected override bool IsItemFiltered(SelectPairItem<T> cur)
				=> parent.IsItemFiltered(cur);
		} // class SelectListView

		#endregion

		private string filterText = String.Empty;

		public SelectListDialog(ConsoleApplication app, IEnumerable<SelectPairItem<T>> values)
			: base(app, values)
		{
		} // ctor

		protected override ListViewOverlay<SelectPairItem<T>> CreateListView()
			=> new SelectListView(this);

		protected override bool ResizeToContent => true;

		protected override void OnAccept()
		{
			if (SelectedIndex == -1)
				return;
			base.OnAccept();
		} // proc OnAccept

		private bool IsItemFiltered(SelectPairItem<T> item)
			=> filterText.Length > 0 ? item.Text.IndexOf(filterText, StringComparison.CurrentCultureIgnoreCase) == -1 : false;

		protected override void SetFilterString(string text)
		{
			if (filterText != text)
			{
				filterText = text ?? String.Empty;
				View.RefreshList();
			}
		} // proc SetFilterString

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
