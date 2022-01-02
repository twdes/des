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
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class TableColumn ------------------------------------------------------

	public abstract class TableColumn
	{
		public const string NullValue = "-NULL-";
		public const string ErrorValue = "-ERR-";

		#region -- class TypedTableColumn ---------------------------------------------

		private sealed class TypedTableColumn : TableColumn
		{
			private readonly Type type;

			public TypedTableColumn(string name, Type type, int width)
				: base(name, type.Name, type, width)
			{
				this.type = type;
			} // ctor

			protected override string FormatValueCore(object value)
				=> base.FormatValueCore(Procs.ChangeType(value, type));
		} // class TypedTableColumn

		#endregion

		private readonly string name;
		private readonly string typeString;
		private int width;

		protected TableColumn(string name, string typeString, Type type, int width)
		{
			this.name = name ?? String.Empty;
			this.typeString = typeString ?? "string";
			this.width = width == 0 ? GetDefaultWidth(Type.GetTypeCode(type ?? GetDefaultType(typeString))) : width;
		} // ctor

		protected static Type GetDefaultType(string typeString)
			=> LuaType.GetType(typeString, lateAllowed: true).Type ?? typeof(object);

		protected static int GetDefaultWidth(TypeCode typeCode)
		{
			switch (typeCode)
			{
				case TypeCode.SByte:
				case TypeCode.Byte:
				case TypeCode.Int16:
				case TypeCode.UInt16:
				case TypeCode.Int32:
				case TypeCode.UInt32:
				case TypeCode.Int64:
				case TypeCode.UInt64:
					return 10;
				case TypeCode.Boolean:
					return 5;
				case TypeCode.DateTime:
					return 20;
				default:
					return -1;
			}
		} // func GetDefaultWidth
		
		protected virtual string FormatValueCore(object value)
		{
			return value == null
				? NullValue
				: value.ChangeType<string>();
		} // func FormatValueCore

		public string FormatValue(object value)
		{
			try
			{
				return FormatValueCore(value);
			}
			catch (FormatException)
			{
				return value?.ToString() ?? NullValue;
			}
			catch
			{
				return ErrorValue;
			}
		} // proc FormatValue

		public string Name => name;
		public string TypeName => typeString;
		public int Width => width > 0 ? width : 0;

		public int MaxWidth { get; set; } = 0;
		public int MinWidth { get; set; } = 10;

		internal static string TableStringPad(string value, int maxWidth)
		{
			if (String.IsNullOrEmpty(value))
				return new string(' ', maxWidth);
			else
			{
				value = value.GetFirstLine();
				if (value.Length > maxWidth)
				{
					return maxWidth < 10
						? value.Substring(0, maxWidth)
						: value.Substring(0, maxWidth - 3) + "...";
				}
				else
					return value.PadRight(maxWidth);
			}
		} // func TableStringPad

		internal static TableColumn[] CalculateTableLayoutCore(int totalWidth, IEnumerable<TableColumn> columns)
		{
			var fixedWidth = 0;
			var variableWidth = 0;
			var columnsList = new List<TableColumn>(columns is ICollection c1 ? c1.Count : 4);

			totalWidth--;

			foreach(var col in columns)
			{
				columnsList.Add(col);
				if (col.width > 0)
					fixedWidth += col.width + 1;
				else if(col.width < 0)
					variableWidth += Math.Abs(col.width);
			}

			// calc variable column with
			if (variableWidth > 0)
			{
				var totalVariableWidth = totalWidth - fixedWidth;
				if (totalVariableWidth > 0)
				{
					foreach (var c in columnsList)
					{
						if (c.width >= 0)
							continue;

						var varColumnWidth = (totalVariableWidth * Math.Abs(c.width) / variableWidth) - 1;
						if (varColumnWidth < c.MinWidth)
							varColumnWidth = c.MinWidth;
						
						if (c.MaxWidth > 0 && c.MaxWidth < varColumnWidth)
							c.width = c.MaxWidth;
						else
							c.width = varColumnWidth;
					}
				}
				else
				{
					foreach (var c in columnsList)
					{
						if (c.width >= 0)
							continue;

						c.width = c.MinWidth;
					}
				}
			}

			// clear invisible columns
			var currentWidth = 0;
			for (var i = 0; i < columnsList.Count; i++)
			{
				var col = columnsList[i];
				if (currentWidth < totalWidth)
				{
					var newCurrentWidth = currentWidth + col.width + 1;
					if (newCurrentWidth > totalWidth // use rest
						|| (i == columnsList.Count - 1 && newCurrentWidth < totalWidth)) // expand to rest
						col.width = totalWidth - currentWidth;

					currentWidth = newCurrentWidth;
				}
				else
				{
					columnsList.RemoveRange(i, columnsList.Count - i);
					break;
				}
			}

			return columnsList.ToArray();
		} // func CalculateTableLayout

		/// <summary></summary>
		/// <param name="name"></param>
		/// <param name="type"></param>
		/// <param name="width"></param>
		/// <returns></returns>
		public static TableColumn Create(string name, Type type, int width = 0, int minWidth = 10, int maxWidth = 0)
		{
			return new TypedTableColumn(
				name ?? throw new ArgumentNullException(nameof(name)),
				type ?? throw new ArgumentNullException(nameof(type)),
				width
			)
			{ MinWidth = minWidth, MaxWidth = maxWidth };
		} // func Create
	} // class TableColumn

	#endregion

	public delegate string GetTableColumnFormattedText(TableColumn column, int index);
	public delegate string GetTableColumnForegroundColor(TableColumn column, int index);

	#region -- class ConsoleTable -----------------------------------------------------

	public sealed class ConsoleTable
	{
		private readonly ConsoleApplication app;
		private readonly TableColumn[] columns;

		private ConsoleTable(ConsoleApplication app, TableColumn[] columns)
		{
			this.app = app ?? throw new ArgumentNullException(nameof(app));
			this.columns = columns ?? throw new ArgumentNullException(nameof(columns));
		} // ctor

		public ConsoleTable WriteHeader(bool includeTypes = true)
		{
			// header
			WriteCore((col, _) => col.Name);
			// type
			if (includeTypes)
			{
				using (app.Color(ConsoleColor.DarkGray))
					WriteCore((col, _) => col.TypeName);
			}
			// sep
			WriteCore((col, _) => new string('-', col.Width));
			return this;
		} // proc WriteHeader

		public ConsoleTable WriteCore(GetTableColumnFormattedText getValue)
		{
			for (var i = 0; i < columns.Length; i++)
			{
				if (i > 0)
					app.Write(" ");

				var col = columns[i];
				app.Write(TableColumn.TableStringPad(getValue(col, i), col.Width));
			}
			app.WriteLine();
			return this;
		} // func WriteCore

		public ConsoleTable WriteRaw(params string[] values)
			=> WriteCore((col, i) => i < values.Length ? values[i] : null);

		public ConsoleTable Write(params object[] values)
			=> WriteCore((col, i) => i < values.Length ? col.FormatValue(values[i]) : TableColumn.ErrorValue);

		public static ConsoleTable Create(ConsoleApplication app, int totalWidth, params TableColumn[] columns)
			=> new ConsoleTable(app, TableColumn.CalculateTableLayoutCore(totalWidth, columns));

		public static ConsoleTable Create(ConsoleApplication app, int totalWidth, IEnumerable<TableColumn> columns)
			=> new ConsoleTable(app, TableColumn.CalculateTableLayoutCore(totalWidth, columns));
	} // class ConsoleTable

	#endregion

	#region -- class CharBufferTable --------------------------------------------------

	public sealed class CharBufferTable
	{
		private readonly int totalWidth;
		private readonly TableColumn[] columns;

		private CharBufferTable(int totalWidth, TableColumn[] columns)
		{
			this.columns = columns ?? throw new ArgumentNullException(nameof(columns));

			this.totalWidth = totalWidth;
		} // ctor

		public int Write(CharBuffer buffer, int left, int top, GetTableColumnFormattedText getValue, ConsoleColor[] foregroundColor, ConsoleColor backgroundColor)
		{
			for (var i = 0; i < columns.Length; i++)
			{
				var color = i < foregroundColor.Length ? foregroundColor[i] : foregroundColor[foregroundColor.Length - 1];
				if (i > 0)
					(left, _) = buffer.Write(left, top, " ", null, color, backgroundColor);

				var col = columns[i];
				(left, _) = buffer.Write(left, top, TableColumn.TableStringPad(getValue(col, i), col.Width), null, color, backgroundColor);
			}
			(left, _) = buffer.Write(left, top, " ", null, backgroundColor, backgroundColor);
			return left;
		} // func Write

		public int WriteHeader(CharBuffer buffer, int left, int top, ConsoleColor foregroundColor, ConsoleColor backgroundColor)
			=> Write(buffer, left, top, (col, i) => col.Name, foregroundColor, backgroundColor);

		public int Write(CharBuffer buffer, int left, int top, object[] values, ConsoleColor[] foregroundColor, ConsoleColor backgroundColor)
			=> Write(buffer, left, top, (col, i) => col.FormatValue(i < values.Length ? values[i] : null), foregroundColor, backgroundColor);

		public int Write(CharBuffer buffer, int left, int top, GetTableColumnFormattedText getValue, ConsoleColor foregroundColor, ConsoleColor backgroundColor)
			=> Write(buffer, left, top, getValue, new ConsoleColor[] { foregroundColor }, backgroundColor);

		public int WriteEmpty(CharBuffer buffer, int left, int top, ConsoleColor backgroundColor)
		{
			var endAt = left + totalWidth;
			buffer.Fill(left, top, endAt - 1, top, ' ', backgroundColor, backgroundColor);
			return endAt;
		} // proc WriteEmpty

		public int TotalWidth => totalWidth;

		public static CharBufferTable Create(int totalWidth, params TableColumn[] columns)
			=> new CharBufferTable(totalWidth, TableColumn.CalculateTableLayoutCore(totalWidth, columns));

		public static CharBufferTable Create(int totalWidth, IEnumerable<TableColumn> columns)
			=> new CharBufferTable(totalWidth, TableColumn.CalculateTableLayoutCore(totalWidth, columns));
	} // class CharBufferTable

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
