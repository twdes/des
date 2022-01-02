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
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.Console;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.UI
{
	#region -- class ListDialog -------------------------------------------------------

	internal class ListDialog : ListDialogBase<XElement>
	{
		#region -- class ListGetTableColumn -------------------------------------------

		private abstract class ListGetTableColumn : TableColumn
		{
			private readonly Type type;

			public ListGetTableColumn(string name, string typeName, Type type, int width)
				: base(name, typeName, type, width)
			{
				this.type = type;
			} // ctor

			protected abstract string GetRawValue(XElement x);

			protected sealed override string FormatValueCore(object value)
			{
				var rawValue = GetRawValue((XElement)value);
				try
				{
					return base.FormatValueCore(Procs.ChangeType(rawValue, type));
				}
				catch (FormatException)
				{
					return rawValue;
				}
			} // func FormatValueCore

			public static TableColumn Create(XElement xTypeDescription, XElement xCol)
			{
				if (xCol.Name == "attribute")
				{
					var attrName = xCol.Attribute("name")?.Value;
					var typeName = xCol.Attribute("type")?.Value;

					return new ListGetAttributeTableColumn(attrName, typeName, attrName,
						attrName == "typ" && xTypeDescription.Name == "line" ? 3 : 0
					);
				}
				else if (xCol.Name == "element")
				{
					var elementName = xCol.Attribute("name")?.Value;
					var typeName = xCol.Attribute("type")?.Value;
					var isArray = typeName.EndsWith("[]");
					var xSubType = xTypeDescription.Parent.Element(isArray ? typeName.Substring(0, typeName.Length - 2) : typeName);
					if (xSubType != null)
						return new ListGetElementTypeTableColumn(elementName, xTypeDescription.Name.Namespace + elementName, xSubType, isArray);
					else if (elementName == null)
						return new ListGetValueTableColumn(typeName);
					else
						return new ListGetElementTableColumn(elementName, typeName, xTypeDescription.Name.Namespace + elementName);
				}
				else
					return null;
			} // func Create

			public static IEnumerable<TableColumn> Create(XElement xTypeDescription)
				=> xTypeDescription.Elements().Select(c => Create(xTypeDescription, c)).Where(c => c != null);
		} // class ListGetTableColumn

		#endregion

		#region -- class ListGetAttributeTableColumn ----------------------------------

		private sealed class ListGetAttributeTableColumn : ListGetTableColumn
		{
			private readonly XName xAttribute;

			public ListGetAttributeTableColumn(string name, string typeName, XName xAttribute, int width)
				: base(name, typeName, GetDefaultType(typeName), width)
			{
				this.xAttribute = xAttribute ?? throw new ArgumentNullException(nameof(xAttribute));
			} // ctor

			protected override string GetRawValue(XElement x)
				=> x.Attribute(xAttribute)?.Value;
		} // class ListGetAttributeTableColumn

		#endregion

		#region -- class ListGetAttributeTableColumn ----------------------------------

		private sealed class ListGetElementTableColumn : ListGetTableColumn
		{
			private readonly XName xElementName;

			public ListGetElementTableColumn(string name, string typeName, XName xElementName)
				: base(name, typeName, GetDefaultType(typeName), 0)
			{
				this.xElementName = xElementName ?? throw new ArgumentNullException(nameof(xElementName));
			} // ctor

			protected override string GetRawValue(XElement x)
				=> x.Element(xElementName)?.Value;
		} // class ListGetElementTableColumn

		#endregion

		#region -- class ListGetAttributeTableColumn ----------------------------------

		private sealed class ListGetValueTableColumn : ListGetTableColumn
		{
			public ListGetValueTableColumn(string typeName)
				: base(".", typeName, GetDefaultType(typeName), 0)
			{
			}

			protected override string GetRawValue(XElement x)
				=> x.Value;
		} // class ListGetValueTableColumn

		#endregion

		#region -- class ListGetElementTypeTableColumn --------------------------------

		private sealed class ListGetElementTypeTableColumn : ListGetTableColumn
		{
			private readonly XName xElementName;
			private readonly bool isArray;
			private readonly XName xTypeElementName;
			private readonly TableColumn[] columns;

			public ListGetElementTypeTableColumn(string name, XName xElementName, XElement xType, bool isArray)
				: base(name, xType.Name.LocalName + (isArray ? "[]" : String.Empty), typeof(string), -1)
			{
				this.xElementName = xElementName ?? throw new ArgumentNullException(nameof(xElementName));
				this.isArray = isArray;
				columns = Create(xType ?? throw new ArgumentNullException(nameof(xType))).ToArray();
				xTypeElementName = xType.Name;
			} // ctor

			private void FormatValueShort(StringBuilder sb, XElement x)
			{
				sb.Append('[');

				var first = true;
				foreach (var t in columns)
				{
					if (first)
						first = false;
					else
						sb.Append(',');

					sb.Append('"').Append(t.Name).Append("\":");
					sb.Append('"').Append(t.FormatValue(x)).Append('"');
				}

				sb.Append(']');
			} // proc FormatValueShort

			protected override string GetRawValue(XElement x)
			{
				var sb = new StringBuilder();

				var xValue = x?.Element(xElementName);
				if (xValue == null)
					sb.Append(NullValue);
				else if (isArray)
				{
					var rowCount = 0;

					{
						foreach (var cur in xValue.Elements(xTypeElementName))
						{
							if (rowCount >= 10)
								break;
							else if (rowCount > 0)
								sb.Append(",");

							FormatValueShort(sb, cur);

							rowCount++;
						}
					}

					if (rowCount == 0)
						sb.Append(NullValue);
				}
				else
					FormatValueShort(sb, x.Element(xElementName).Element(xTypeElementName));

				return sb.ToString();
			} // func GetRawValue
		} // class ListGetElementTypeTableColumn

		#endregion

		#region -- class ListOverlay --------------------------------------------------

		private sealed class ListOverlay : ListViewOverlay<XElement>
		{
			private readonly ListDialog parent;
			private XElement typeDef;
			private CharBufferTable table = null;

			public ListOverlay(ListDialog parent)
			{
				this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
			} // ctor

			public void SetTypeDef(XElement typeDef)
			{
				this.typeDef = typeDef ?? throw new ArgumentNullException(nameof(typeDef));
			} // proc SetTypeDef

			private static bool ContainsString(string value, string[] expr)
			{
				foreach (var x in expr)
				{
					if (value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)
						return true;
				}
				return false;
			} // func ContainsString

			private static bool ContainsString(XAttribute x, string[] expr)
				=> ContainsString(x.Value, expr);

			private static bool ContainsString(XElement x, string[] expr)
			{
				var attr = x.FirstAttribute;
				while (attr != null)
				{
					if (ContainsString(attr, expr))
						return true;
					attr = attr.NextAttribute;
				}

				var cur = x.FirstNode;
				while (cur != null)
				{
					if (ContainsString(cur, expr))
						return true;
					cur = cur.NextNode;
				}

				return false;
			} // func ContainsString

			private static bool ContainsString(XNode x, string[] expr)
			{
				switch (x)
				{ case XElement e:
						return ContainsString(e, expr);
					case XCData t2:
						return ContainsString(t2.Value, expr);
					case XText t:
						return ContainsString(t.Value, expr);
				}
				return false;
			} // func ContainsString

			protected override bool IsItemFiltered(XElement cur)
			{
				var expr = parent.filterExpressions;
				return expr.Length != 0 && !ContainsString(cur, expr);
			} // func IsItemFiltered

			protected override void OnPreRender(ref int top, int width, int height)
			{
				if (table == null || table.TotalWidth != width)
					table = CharBufferTable.Create(width, ListGetTableColumn.Create(typeDef));

				var endLeft = table.WriteHeader(Content, 0, top, ForegroundColor, BackgroundColor);
				if (endLeft < width)
					Content.Fill(endLeft, top, width - 1, top, ' ', ForegroundColor, BackgroundColor);
				Content.Set(width, top, VerticalDoubleLine, ForegroundColor, BackgroundColor);
				top++;

				Content.Fill(0, top, width - 1, top, HorizontalThinLine, ForegroundColor, BackgroundColor);
				Content.Set(width, top, VerticalDoubleToHorizontalThinLineRight, ForegroundColor, BackgroundColor);
				top++;
			} // proc OnPreRender

			protected override int OnRenderLine(int top, int width, int i, XElement item, bool selected)
				=> table.Write(Content, 0, top, (col, _) => col.FormatValue(item), GetLineTextColor(i, item, selected), selected ? BackgroundHighlightColor : BackgroundColor);
		} // class ListOverlay

		#endregion

		private readonly XElement xTypes;
		private readonly int itemsCount;
		private readonly int totalCount;

		private string[] filterExpressions = Array.Empty<string>();

		public ListDialog(ConsoleApplication app, string listId, XElement xTypes, XElement xItemType, XElement[] items, int totalCount)
			: base(app, items)
		{
			Title = listId;

			itemsCount = items.Length;
			this.totalCount = totalCount;

			this.xTypes = xTypes ?? throw new ArgumentNullException(nameof(xTypes));

			// set type def
			View.SetTypeDef(xItemType);
		} // ctor

		protected override ListViewOverlay<XElement> CreateListView()
			=> new ListOverlay(this);

		protected override void OnRender()
		{
			base.OnRender();
			if (Content != null)
				Content.Set(0, 2, VerticalDoubleToHorizontalThinLineLeft, ForegroundColor, BackgroundColor);

			if (totalCount > 0 && (totalCount == 0 || totalCount > itemsCount))
				Content.Write(3, Height - 1, $" {itemsCount:N0}/{totalCount:N0} ", foreground: ForegroundColor, background: BackgroundColor);
			else
				Content.Write(3, Height - 1, $" {itemsCount:N0} items ", foreground: ForegroundColor, background: BackgroundColor);
		} // proc OnRender

		protected override void SetFilterString(string text)
		{
			filterExpressions = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			View.RefreshList();
		} // proc SetFilterString 

		private new ListOverlay View => (ListOverlay)base.View;

		private static bool TryGetListInfoData(XElement xList, out XElement xTypes, out IEnumerable<XElement> xItemSource, out XElement xItemType, out int totalCount)
		{
			totalCount = -1;
			xItemSource = null;
			xItemType = null;

			// parse type
			xTypes = xList.Element("typedef");
			if (xTypes == null)
				return false;

			// get items
			var xItems = xList.Element("items");
			if (xItems == null)
				return false;

			// get first root element
			var xFirstElement = xItems.Elements().FirstOrDefault();
			if (xFirstElement == null)
				return false;

			var rootTypeName = xFirstElement.Name;
			xItemType = xTypes.Element(rootTypeName);
			if (xItemType == null)
				return false;

			totalCount = xItems.GetAttribute("tc", -1);
			xItemSource = xItems.Elements(rootTypeName);

			return true;
		} // func TryGetListInfoData

		public static async Task ShowListAsync(ConsoleApplication app, string title, XElement xList)
		{
			if (!TryGetListInfoData(xList, out var xTypes, out var xItems, out var xItemType, out var totalCount))
				return;
			{
				var dlg = new ListDialog(app, title, xTypes, xItemType, xItems.ToArray(), totalCount);
				await dlg.ShowDialogAsync();
			}
		} // func ShowListAsync

		public static void PrintList(ConsoleApplication app, XElement xList)
		{
			if (!TryGetListInfoData(xList, out var xTypes, out var xItems, out var xItemType, out var totalCount))
				return;

			var table = ConsoleTable.Create(app, Console.WindowWidth, ListGetTableColumn.Create(xItemType))
				.WriteHeader();

			// print columns
			var count = 0;
			foreach (var x in xItems)
			{
				table.WriteCore((col, _) => col.FormatValue(x));
				count++;
			}

			if (totalCount >= 0 && (count == 0 || totalCount > count))
				app.WriteLine(new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.White }, new string[] { "==> ", $"{count:N0} from {totalCount:N0}" }, true);
			else if (count >= 0)
				app.WriteLine(new ConsoleColor[] { ConsoleColor.Gray, ConsoleColor.White }, new string[] { "==> ", $"{count:N0} lines" }, true);
		} // proc PrintList
	} // class ListDialog

	#endregion
}
