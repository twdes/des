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
	internal sealed class PropertyOverlay : ConsoleOverlay
	{
		private const ConsoleColor backgroundColor = ConsoleColor.DarkCyan;

		private readonly DEHttpClient http;
		private string currentPath;

		private CharBufferTable propertyTable = null;
		private List<LogProperty> properties = new List<LogProperty>();

		public PropertyOverlay(DEHttpClient http, string currentPath)
		{
			this.http = http ?? throw new ArgumentNullException(nameof(http));
			
			SetPath(currentPath);
			Top = 2;
			Position = ConsoleOverlayPosition.Window;
		} // ctor

		private LogProperty[] GetProperties()
		{
			lock (properties)
				return properties.ToArray();
		} // func GetProperties

		protected override void OnRender()
		{
			var content = Content;
			if (content == null || propertyTable == null)
				return;

			var props = GetProperties();
			var curCat = String.Empty;
			var endTop = 0;
			foreach (var p in props)
			{
				// write category
				if (curCat != p.Info.Category)
				{
					curCat = p.Info.Category;

					content.Set(0, endTop, HorizontalThinLine, ConsoleColor.DarkGray, backgroundColor);
					content.Set(1, endTop, HorizontalThinLine, ConsoleColor.DarkGray, backgroundColor);
					content.Set(2, endTop, ' ', ConsoleColor.DarkGray, backgroundColor);
					var (endLeft, _) = content.Write(3, endTop, curCat, 1, ConsoleColor.DarkGray, backgroundColor);
					content.Set(endLeft++, endTop, ' ', ConsoleColor.DarkGray, backgroundColor);
					while (endLeft < content.Width)
						content.Set(endLeft++, endTop, HorizontalThinLine, ConsoleColor.DarkGray, backgroundColor);

					endTop++;
				}

				// write property
				content.Set(0, endTop, ' ', ConsoleColor.Gray, backgroundColor);
				content.Set(1, endTop, ' ', ConsoleColor.Gray, backgroundColor);
				propertyTable.Write(content, 2, endTop, new object[] { p.Info.DisplayName, p.FormattedValue }, new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.White }, backgroundColor);

				endTop++;
			}
		
			content.Fill(0, endTop, Width - 1, Height - 1, ' ', ConsoleColor.Gray, backgroundColor);
		} // proc OnRender

		protected override void OnParentResize()
		{
			base.OnParentResize();

			var windowWidth = Application.WindowRight - Application.WindowLeft + 1;
			var windowHeight = Application.WindowBottom - Application.WindowTop + 1;
			var newWidth = Math.Min(windowWidth / 3, 80);
			var newHeight = windowHeight - Application.ReservedBottomRowCount - 3;

			if (newWidth != Width || newHeight != Height)
			{
				Resize(newWidth, newHeight);
				Left = -newWidth;

				propertyTable = CharBufferTable.Create(newWidth - 1,
					TableColumn.Create("Name", typeof(string), -25, maxWidth: 30),
					TableColumn.Create("Value", typeof(string), -15)
				);

				Invalidate();
			}

			if (Application.ReservedRightColumnCount != newWidth)
				Application.BeginInvoke(() => Application.ReservedRightColumnCount = newWidth);
		} //proc OnParentResize

		private void UpdateProperty(string path, LogProperty property)
		{
			lock (properties)
			{
				for (var i = 0; i < properties.Count; i++)
				{
					if (properties[i].Info.CompareTo(property.Info) > 0)
					{
						properties.Insert(i, property);
						return;
					}
				}
				properties.Add(property);
			}
		} // proc UpdateProperty

		internal void EventReceived(object sender, DEHttpSocketEventArgs e)
		{
			if (e.Id == "tw_properties") // log property changed
			{
				// update value if in list
				var isChanged = false;
				lock (properties)
				{
					foreach (var p in properties)
						if (currentPath == e.Path && p.Info.Name == e.Index)
						{
							p.SetValue(e.Values?.Value);
							isChanged = true;
						}
				}
				if (isChanged)
					Invalidate();
			}
		} // event EventReceived

		public void SetPath(string newPath)
		{
			if (newPath != currentPath)
			{
				currentPath = newPath;

				// clear unused properties
				lock (properties)
					properties.Clear();

				// fetch properties
				LogProperty.GetLogPropertiesAsync(http, currentPath, UpdateProperty).ContinueWith(t =>
					{
						try
						{
							Invalidate();
						}
						catch (Exception ex)
						{
							Debug.Print(ex.GetInnerException().ToString());
						}
					},
					TaskContinuationOptions.ExecuteSynchronously
				);
			}
		} // proc SetPath
	} // class PropertyOverlay
}
