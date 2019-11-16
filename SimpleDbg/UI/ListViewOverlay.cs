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
using System.Collections.Generic;
using System.Collections.Specialized;
using Neo.Console;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ListViewOverlay --------------------------------------------------

	internal class ListViewOverlay<T> : ConsoleFocusableOverlay
		where T : class
	{
		private readonly List<T> view = new List<T>();
		private IEnumerable<T> source;
		private int firstVisibleIndex = 0;
		private int selectedIndex = 0;

		public ListViewOverlay()
		{
			Position = ConsoleOverlayPosition.Window;
		} // ctor

		protected override void OnAdded()
		{
			base.OnAdded();

			AttachSourceEvents();
		} // proc OnAdded

		protected override void OnRemoved()
		{
			DetachSourceEvents();

			base.OnRemoved();
		} // proc OnRemoved

		private void AttachSourceEvents()
		{
			if (source is INotifyCollectionChanged ncc)
				ncc.CollectionChanged += Source_CollectionChanged;
			RefreshList();
		} // proc AttachSourceEvents

		private void DetachSourceEvents()
		{
			if (source is INotifyCollectionChanged ncc)
				ncc.CollectionChanged -= Source_CollectionChanged;
		} // proc DetachSourceEvents

		private void Source_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			switch(e.Action)
			{
				case NotifyCollectionChangedAction.Add:
					var insertAt = e.NewStartingIndex;
					var selectLast = selectedIndex == view.Count - 1;
					foreach (var c in e.NewItems)
					{
						var cur = (T)c;
						if (!IsItemFiltered(cur))
							view.Insert(insertAt++, cur);
					}

					if (selectLast)
						SelectLast();
					Invalidate();

					break;
				case NotifyCollectionChangedAction.Remove:
				case NotifyCollectionChangedAction.Move:
				case NotifyCollectionChangedAction.Reset:
					RefreshList();
					break;
			}
		} // event Source_CollectionChanged

		public void RefreshList()
		{
			var item = SelectedItem;

			view.Clear();
			if (source != null)
			{
				foreach (var cur in source)
				{
					if (!IsItemFiltered(cur))
						view.Add(cur);
				}
			}

			if (!SelectItem(item))
				SelectIndex(selectedIndex, false);

			Invalidate();
		} // proc RefreshList

		protected virtual bool IsItemFiltered(T cur)
			=> false;

		protected virtual ConsoleColor GetLineTextColor(int index, T item, bool selected)
			=> selected ? ForegroundHighlightColor : ForegroundColor;

		public virtual string GetLineText(int index, T item, int width)
		{
			var text = item?.ToString()?.GetFirstLine();
			if (text == null)
				return null;
			else if (text.Length > width)
				return text.Substring(0, width - 3) + "...";
			else
				return text;
		} // func GetLineText

		protected virtual int OnRenderLine(int top, int i, T item, string text, bool selected)
		{
			var (endLeft, _) = Content.Write(0, top, text,
				foreground: GetLineTextColor(i, item, selected),
				background: selected ? BackgroundHighlightColor : BackgroundColor
			);
			return endLeft;
		} // func OnRenderLine

		protected sealed override void OnRender()
		{
			if (Content == null)
				return;

			var width = Content.Width - 1;
			var height = Content.Height;
			var top = 0;

			// render list items
			var lastVisibleIndex = Math.Min(view.Count, firstVisibleIndex + height);
			for (var i = firstVisibleIndex; i < lastVisibleIndex; i++)
			{
				var item = view[i];
				var text = GetLineText(i, item, width);
				var selected = selectedIndex == i;

				var endLeft = OnRenderLine(top, i, item, text, selected);

				if (endLeft < width)
				{
					Content.Fill(endLeft, top, width - 1, top, ' ',
						selected ? ForegroundHighlightColor : ForegroundColor,
						selected ? BackgroundHighlightColor : BackgroundColor
					);
				}

				top++;
			}

			if (top < height)
				Content.Fill(0, top, width - 1, height - 1, ' ', ForegroundColor, BackgroundColor);

			// render scroll
			RenderVerticalScroll(width, height);
		} // proc OnRender

		private void RenderVerticalScroll(int width, int height)
		{
			var startAt = view.Count > 0 ? firstVisibleIndex * height / view.Count : -1;
			var endAt = startAt >= 0 ? (startAt + height * height / view.Count) : -1;

			for (var i = 0; i < height; i++)
			{
				var selected = i >= startAt && i <= endAt;
				Content.Set(width, i, selected ? Block : Shadow, ForegroundColor, BackgroundColor);
			}
		} // proc RenderVerticalScroll

		public override bool OnHandleEvent(EventArgs e)
		{
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.Key)
				{
					case ConsoleKey.DownArrow:
						SelectIndex(selectedIndex + 1, false);
						return true;
					case ConsoleKey.UpArrow:
						SelectIndex(selectedIndex - 1, true);
						return true;
					case ConsoleKey.PageDown:
						SelectIndex(selectedIndex + Height - 1, false);
						return true;
					case ConsoleKey.PageUp:
						SelectIndex(selectedIndex - Height + 1, false);
						return true;
					case ConsoleKey.End:
						SelectLast();
						return true;
					case ConsoleKey.Home:
						SelectFirst();
						return true;
				}
			}
			return base.OnHandleEvent(e);
		} // func OnHandleEvent

		public T GetItem(int index)
			=> index >= 0 && index < view.Count ? view[index] : null;

		public int GetItemIndex(Predicate<T> p)
		{
			for (var i = 0; i < view.Count; i++)
			{
				if (p(view[i]))
					return i;
			}
			return -1;
		} // func GetItemIndex

		public void SetFirstVisibleLine(int newFirstVisibleIndex)
		{
			if (newFirstVisibleIndex > view.Count)
				newFirstVisibleIndex = view.Count - 1;
			if (newFirstVisibleIndex < 0)
				newFirstVisibleIndex = 0;
			if (LastVisibleLine >= view.Count && view.Count >= Height)
				newFirstVisibleIndex = view.Count - Height;

			if (firstVisibleIndex != newFirstVisibleIndex)
			{
				firstVisibleIndex = newFirstVisibleIndex;
				Invalidate();
			}
		} // proc SetFirstVisibleLine

		public void SelectIndex(int newIndex, bool allowInvalidIndex = true)
		{
			if (newIndex <= -1 && allowInvalidIndex)
			{
				selectedIndex = -1;
				Invalidate();
			}
			else
			{
				if (newIndex < 0)
					newIndex = 0;
				if (newIndex >= view.Count)
					newIndex = view.Count - 1;

				if (newIndex != selectedIndex)
				{
					selectedIndex = newIndex;

					// make index visible
					if (selectedIndex < firstVisibleIndex)
						SetFirstVisibleLine(selectedIndex);
					else if (Height > 0 && selectedIndex > LastVisibleLine)
						SetFirstVisibleLine(selectedIndex - Height + 1);

					Invalidate();
				}
			}
		} // proc SelectIndex
		
		public void SelectFirst()
			=> SelectIndex(0, false);

		public void SelectLast()
			=> SelectIndex(view.Count - 1, false);

		public bool SelectItem(T item)
		{
			for (var i = 0; i < view.Count; i++)
			{
				if (view[i] == item)
				{
					SelectIndex(i, false);
					return true;
				}
			}
			return false;
		} // func SelectItem

		public int SelectedIndex => selectedIndex;
		public int FirstVisibleLine => firstVisibleIndex;
		public int LastVisibleLine => firstVisibleIndex + Height - 1;
		public int ItemCount => view.Count;

		public T SelectedItem => GetItem(selectedIndex);

		public IEnumerable<T> Source
		{
			get => source;
			set
			{
				if (source != value)
				{
					if (Application != null)
						DetachSourceEvents();
					
					source = value;

					if (Application != null)
						AttachSourceEvents();
				}
			}
		} // prop Source

		public ConsoleColor BackgroundHighlightColor { get; set; } = ConsoleColor.Cyan;
		public ConsoleColor ForegroundHighlightColor { get; set; } = ConsoleColor.Black;
	} // class ListView

	#endregion
}
