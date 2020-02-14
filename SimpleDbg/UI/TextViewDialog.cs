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
using System.Linq;
using Neo.Console;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	internal class TextViewDialog : ConsoleDialogOverlay
	{
		private readonly string text;
		private int virtualOffsetX;
		private int virtualOffsetY;
		private readonly int virtualWidth;
		private readonly int virtualHeight;
		private readonly List<(int startAt, int len)> lines = new List<(int startAt, int len)>();

		public TextViewDialog(ConsoleApplication app, string text)
		{
			this.text = text;
			lines.AddRange(Procs.SplitNewLinesTokens(text));

			virtualOffsetX = 0;
			virtualOffsetY = 0;
			virtualWidth = lines.Max(c => c.len);
			virtualHeight = lines.Count + 1;

			Application = app ?? throw new ArgumentNullException(nameof(app));
		} // ctor

		protected override void OnRender()
		{
			base.OnRender();

			RenderFrame(Title);

			var width = Content.Width - 1;
			var height = Content.Height - 1;
			for (var y = 1; y < height; y++)
			{
				var charOffset = Int32.MaxValue;
				var charEnd = -1;
				var idx = (y - 1) + virtualOffsetY;
				if (idx < lines.Count)
				{
					charOffset = lines[idx].startAt;
					charEnd = charOffset + lines[idx].len - 1;

					charOffset += virtualOffsetX;
				}

				for (var x = 1; x < width; x++)
				{
					if (charOffset <= charEnd)
					{
						Content.Set(x, y, text[charOffset], ForegroundColor, BackgroundColor);
						charOffset++;
					}
					else
						Content.Set(x, y, ' ', ForegroundColor, BackgroundColor);
				}
			}

			RenderVerticalScroll(Width - 1, 1, height - 1, ContentHeight, virtualOffsetY, virtualHeight);
			RenderHorizontalScroll(Width / 3, width - 3, height, ContentWidth, virtualOffsetX, virtualWidth);
		} // proc OnRender

		private bool ScrollTo(int offsetX, int offsetY)
		{
			if (ValidateScrollOffset(ref virtualOffsetX, offsetX, ContentWidth, virtualWidth)
				|| ValidateScrollOffset(ref virtualOffsetY, offsetY, ContentHeight, virtualHeight))
			{
				Invalidate();
			}
			return true;
		} // proc ScrollTo

		public override bool OnHandleEvent(EventArgs e)
		{
			if (e is ConsoleKeyDownEventArgs keyDown)
			{
				switch (keyDown.Key)
				{
					case ConsoleKey.LeftArrow:
						return ScrollTo(virtualOffsetX - 1, virtualOffsetY);
					case ConsoleKey.RightArrow:
						return ScrollTo(virtualOffsetX + 1, virtualOffsetY);
					case ConsoleKey.UpArrow:
						return ScrollTo(virtualOffsetX, virtualOffsetY - 1);
					case ConsoleKey.DownArrow:
						return ScrollTo(virtualOffsetX, virtualOffsetY + 1);
					case ConsoleKey.PageUp:
						return ScrollTo(virtualOffsetX, virtualOffsetY - ContentHeight + 1);
					case ConsoleKey.PageDown:
						return ScrollTo(virtualOffsetX, virtualOffsetY + ContentHeight - 1);
					case ConsoleKey.Home:
						return ScrollTo(0, 0);
					case ConsoleKey.End:
						return ScrollTo(virtualOffsetX, Int32.MaxValue);
				}
			}

			return base.OnHandleEvent(e);
		} // func OnHandleEvent

		private int ContentWidth => Width - 2;
		private int ContentHeight => Height - 2;

		public string Title { get; set; } = "Text";
	} // class TextViewDialog
}
