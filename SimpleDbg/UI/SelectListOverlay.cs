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
using System.Linq;

namespace TecWare.DE.Server.UI
{
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
	} // class SelectUseOverlay 

	#endregion
}
