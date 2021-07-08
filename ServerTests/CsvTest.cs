using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Data;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class CsvTest
	{
		private static readonly string sampleSimple01 = String.Join(Environment.NewLine,
			"Hallo;\"Welt\"",
			"\"Quote \"\"me\"\";\";1",
			"\"\"test\"\" ;\"\"");
		private static readonly string sampleSimple02 = String.Join(String.Empty,
			"Hallo     Welt      ",
			"\"Quote\"   1         ");
		private static readonly string sampleSimple03 = String.Join(Environment.NewLine,
			"Preis;Datum;Menge",
			"5.9;2015-02-05;3,2",
			";2015-02-02");

		[TestMethod]
		public void TestRead01()
		{
			var row = 0;
			using (var r = new TextCsvReader(new StringReader(sampleSimple01), new TextCsvSettings() { }))
			{
				while (r.ReadRow())
				{
					Console.WriteLine("{0}, {1}", r[0], r[1]);
					Assert.AreEqual(r.Count, 2);
					row++;
				}
				Assert.AreEqual(3, row);
			}
		}

		[TestMethod]
		public void TestRead02()
		{
			var row = 0;
			using (var r = new TextFixedReader(new StringReader(sampleSimple02), new TextFixedSettings() { Lengths = new int[] { 10, 10 } }))
			{
				while (r.ReadRow())
				{
					Assert.AreEqual(r.Count, 2);
					Console.WriteLine("{0}, {1}", r[0], r[1]);
					row++;
				}
				Assert.AreEqual(2, row);
			}
		}

		[TestMethod]
		public void TestRead03()
		{
			using (var r = new TextDataRowEnumerator(new TextCsvReader(new StringReader(sampleSimple03), new TextCsvSettings() { HeaderRow = 0, StartRow = 1 })))
			{
				var header = r.MoveToHeader();

				r.UpdateColumns(
					new TextDataRowColumn() { Name = header[0], DataType = typeof(decimal), FormatProvider = CultureInfo.InvariantCulture },
					new TextDataRowColumn() { Name = header[1], DataType = typeof(DateTime), FormatProvider = CultureInfo.InvariantCulture },
					new TextDataRowColumn() { Name = header[2], DataType = typeof(decimal?), FormatProvider = CultureInfo.GetCultureInfo("de-DE") }
				);

				r.IsParsedStrict = true;

				while (r.MoveNext())
				{
					var c = r.Current;
					if (r.Row == 0)
					{
						Assert.AreEqual(5.9m, c[0]);
						Assert.AreEqual(new DateTime(2015, 2, 5), c[1]);
						Assert.AreEqual(3.2m, c[2]);
					}
					else if (r.Row == 1)
					{
						Assert.AreEqual(0.0m, c["Preis"]);
						Assert.AreEqual(new DateTime(2015, 2, 2), c["Datum"]);
						Assert.AreEqual(null, c[2]);
					}
					else
						Assert.Fail();
				}

				Assert.AreEqual(1, r.Row);
			}
		}

		[TestMethod]
		public void TestRead04()
		{
			const string sampleRead04 = "1;<p style=\"\"margin-top: 0\"\">Applikationszange    </p>;3";

			var row = 0;
			using (var r = new TextCsvReader(new StringReader(sampleRead04), new TextCsvSettings() { }))
			{
				while (r.ReadRow())
				{
					Assert.AreEqual(r.Count, 3);
					Assert.AreEqual(r[0], "1");
					Assert.AreEqual(r[1], "<p style=\"margin-top: 0\">Applikationszange    </p>");
					Assert.AreEqual(r[2], "3");
					Console.WriteLine(r[1]);
					row++;
				}
				Assert.AreEqual(1, row);
			}
		}
		// TestMethodes for TextFixedWriter
		[TestMethod]
		public void TryTextFixedWriter01()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 5 } }))
				{
					w.WriteRow(new string[] { "hi" });
				}
				Assert.AreEqual("hi   \r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter02()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 8 } }))
				{
					w.WriteRow(new string[] { "Der Text ist zu lang" });
				}
				Assert.AreEqual("Der Text\r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter03()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 12 } }))
				{
					w.WriteRow(new string[] { "genaue Länge" });
				}
				Assert.AreEqual("genaue Länge\r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter04()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 5, 5 } }))
				{
					w.WriteRow(new string[] { "Wert1", "Wert2" });
				}
				Assert.AreEqual("Wert1Wert2\r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter05()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 12, 5 } }))
				{
					w.WriteRow(new string[] { "zuwenig Rows" });
				}
				Assert.AreEqual("zuwenig Rows     \r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter06()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 5, 5 }}))
				{
					w.WriteRow(new string[] { "zu", "viele", "Rows" });
				}
				Assert.AreEqual("zu   viele\r\n", sw.ToString());
			}
		}

		[TestMethod]
		public void TryTextFixedWriter07()
		{
			// HeaderRow > StartRow
			using (var sw = new StringWriter())
			{
				using (var w = new TextFixedWriter(sw, new TextFixedSettings() { Lengths = new int[] { 5 }, HeaderRow = 2, StartRow= 1 }))
				{
					w.WriteRow(new string[] { "hi" });
				}
				Assert.AreEqual("hi   \r\n", sw.ToString());
			}
		}


		//Tests for TextCsvWriter

		//Forced
		[TestMethod]
		public void TryTextCsvWriter01()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote = '\"', Delemiter = ',' }))
				{
					w.WriteRow(new string[] { "Test " });
					Assert.AreEqual("\"Test \"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter02()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote = '\"', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "\"Quote\"", "SimpleText" });
					Assert.AreEqual("\"\"\"Quote\"\"\",\"SimpleText\"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter03()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote = '\"', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "\"\"", "" });
					Assert.AreEqual("\"\"\"\"\"\",\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter04()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote = '\"', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "\"This is a \'Quote\'\"" });
					Assert.AreEqual("\"\"\"This is a \'Quote\'\"\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		// ForceText
		[TestMethod]
		public void TryTextCsvWriter05()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.ForceText, Quote = '\'', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "\'ForcedText\' ", "123" });
					Assert.AreEqual("\'ForcedText\' ,123\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter06()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.ForceText, Quote = '\"', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "Forced\"Text\" ", "123" });
					Assert.AreEqual("\"Forced\"Text\"\" ,123\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter07()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.ForceText, Quote = '\"', Delemiter = ',', HeaderRow = 0 }))
				{
					w.WriteRow(new string[] { "\"This is a \"Quote\"\"" });
					Assert.AreEqual("\"This is a \"Quote\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}


		//None
		[TestMethod]
		public void TryTextCsvWriter08()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.None, Quote = '\"', Delemiter = ',' }))
				{
					w.WriteRow(new string[] { "NoQuote", "NoQuote2" });
					Assert.AreEqual("NoQuote,NoQuote2\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter09()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.None, Quote = '\'', Delemiter = ',' }))
				{
					w.WriteRow(new string[] { "No \'Quote\'" });
					Assert.AreEqual("No \'Quote\'\r\n", w.BaseWriter.ToString());
				}
			}
		}


		//Normal
		[TestMethod]
		public void TryTextCsvWriter10()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Normal, Quote = '\"', Delemiter = ',' }))
				{
					w.WriteRow(new string[] { "normal ", "\"Quote\"" });
					Assert.AreEqual("normal ,\"\"\"Quote\"\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter11()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Normal, Quote = '\"', Delemiter = ',' }))
				{
					w.WriteRow(new string[] { "", "\"\"" });
					Assert.AreEqual(",\"\"\"\"\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}

		[TestMethod]
		public void TryTextCsvWriter12()
		{
			using (var sw = new StringWriter())
			{
				using (var w = new TextCsvWriter(sw, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote = '\"', Delemiter = ',' }))
				{
					w.WriteRow(new string[] {null});
					Assert.AreEqual("\"\"\r\n", w.BaseWriter.ToString());
				}
			}
		}



		// Tests für TextDataRowWriter



		private static readonly IDataColumn[] col = new IDataColumn[]
		{
			new SimpleDataColumn ("String", typeof(string)),
			new SimpleDataColumn ("Int", typeof(Int32)),
		};

		private static readonly IDataColumn[] col2 = new IDataColumn[]
		{
			new SimpleDataColumn ("\"String\"", typeof(string)),
			new SimpleDataColumn ("!§$%&/()=*+'#><|", typeof(Int32)),
		};

		private static readonly IDataColumn[] col3 = new IDataColumn[]
		{
		};



		[TestMethod]
		public void TryDataRowWriter01()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1 }, col))
                {
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col),
							new SimpleDataRow(new object[]{ "value2", 2 }, col),
							new SimpleDataRow(new object[]{ "value3", 3 }, col)
						}
					);
					
					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						"value1;1\r\n" +
						"value2;2\r\n" +
						"value3;3\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter02()
		{
			// mehr Felder als Columns
			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1, Quotation= CsvQuotation.Normal }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1, "extraValue" }, col),
							new SimpleDataRow(new object[]{ "value2", 2, "extraValue" }, col),
							new SimpleDataRow(new object[]{ "value3", 3, "extraValue" }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						"value1;1\r\n" +
						"value2;2\r\n" +
						"value3;3\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter03()
		{
			//null-values

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1 }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ null, 1}, col),
							new SimpleDataRow(new object[]{ "value2", null}, col),
							new SimpleDataRow(new object[]{ null, null}, col),
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						";1\r\n" +
						"value2;\r\n" +
						";\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter04()
		{
			//wrong Datatype

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1, Quotation= CsvQuotation.None }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ 1, 1 }, col),
							new SimpleDataRow(new object[]{ "value2", "intvalue" }, col),
							new SimpleDataRow(new object[]{ "value3", "3" }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("\"String\";Int\r\n" +
						"1;1\r\n" +
						"value2;intvalue\r\n" +
						"value3;3\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter05()
		{
			//Sonderzeichen
			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1 }, col2))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "äüö", -1 }, col2),
							new SimpleDataRow(new object[]{ "@><", 2 }, col2),
							new SimpleDataRow(new object[]{ "*'#", 3 }, col2),
							new SimpleDataRow(new object[]{ "!§$%&()=?²", 42 }, col2)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;!§$%&/()=*+'#><|\r\n" +
						"äüö;-1\r\n" +
						"@><;2\r\n" +
						"*'#;3\r\n" +
						"!§$%&()=?²;42\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter06()
		{
			//mit TextFixedSettings
			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextFixedSettings() { Padding = ' ', Lengths = new int[] {10,3 }, HeaderRow= 1 }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 123 }, col),
							new SimpleDataRow(new object[]{ "value2", 234 }, col),
							new SimpleDataRow(new object[]{ "value3", 345 }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String    Int\r\n" +
						"value1  123\r\n" +
						"value2  234\r\n" +
						"\"value3\"  345\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter07()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1, Quotation= CsvQuotation.Forced }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col),
							new SimpleDataRow(new object[]{ "value2", 2 }, col),
							new SimpleDataRow(new object[]{ "value3", 3 }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("\"String\";\"Int\"\r\n" +
						"\"value1\";\"1\"\r\n" +
						"\"value2\";\"2\"\r\n" +
						"\"value3\";\"3\"\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter08()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1, Quotation = CsvQuotation.ForceText }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col),
							new SimpleDataRow(new object[]{ "value2", 2 }, col),
							new SimpleDataRow(new object[]{ "value3", 3 }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						"\"value1\";1\r\n" +
						"\"value2\";2\r\n" +
						"\"value3\";3\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter09()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1, Quotation = CsvQuotation.None }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col),
							new SimpleDataRow(new object[]{ "value2", 2 }, col),
							new SimpleDataRow(new object[]{ "value3", 3 }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						"value1;1\r\n" +
						"value2;2\r\n" +
						"value3;3\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter10()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1 }, col))
				{
					w.Write(
						new IDataRow[]
						{
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter11()
		{

			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 0, StartRow = 1 }, col3))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col3),
							new SimpleDataRow(new object[]{ "value2", 2 }, col3),
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("\r\n\r\n\r\n", text);
				}
			}
		}

		[TestMethod]
		public void TryDataRowWriter12()
		{
			// HeaderRow > StartRow
			using (var sw = new StringWriter())
			{
				using (var w = new TextDataRowWriter(sw, new TextCsvSettings() { HeaderRow = 2, StartRow = 0 }, col))
				{
					w.Write(
						new IDataRow[]
						{
							new SimpleDataRow(new object[]{ "value1", 1 }, col),
							new SimpleDataRow(new object[]{ "value2", 2 }, col),
							new SimpleDataRow(new object[]{ "value3", 3 }, col)
						}
					);

					var text = sw.ToString();
					Assert.AreEqual("String;Int\r\n" +
						"value1;1\r\n" +
						"value2;2\r\n" +
						"value3;3\r\n", text);
				}
			}
		}


	}
}
