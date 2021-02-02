using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Data;

namespace TecWare.DE.Server
{
	[TestClass]
	public class CsvTest
	{
		private static readonly string sampleSimple01 = String.Join(Environment.NewLine,
			"Hallo;\"Welt\"",
			"\"Quote \"\"me\"\";\";1");
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
					Assert.AreEqual(r.Count, 2);
					Console.WriteLine("{0}, {1}", r[0], r[1]);
					row++;
				}
				Assert.AreEqual(2, row);
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
		public void TestTextFixedWriter(string expectedValue, TextFixedWriter sw)
		{
			Assert.AreEqual(expectedValue, sw.BaseWriter.ToString());
		}


		[TestMethod]
		public void TryTextFixedWriter01()
		{
			StringWriter textWriter = new StringWriter();
			textWriter.Write("hi");

			TestTextFixedWriter("hi", new TextFixedWriter(textWriter, new TextFixedSettings() { Lengths = new int[] { 2 } }));
		}

		[TestMethod]
		public void TryTextFixedWriter02()
		{
			var textWriter = new StringWriter();
			textWriter.Write("Der Text ist zu lang");

			TestTextFixedWriter("Der Text ist zu lang", new TextFixedWriter(textWriter, new TextFixedSettings() { Lengths = new int[] { 5 } }));
		}

		[TestMethod]
		public void TryTextFixedWriter03()
		{
			var textWriter = new StringWriter();
			textWriter.Write("zu kurz");

			TestTextFixedWriter("zu kurz", new TextFixedWriter(textWriter, new TextFixedSettings() { Lengths = new int[] { 3 } }));
		}

		//Tests for TextCsvWriter

		public void TestTextCsvWriter(string expectedValue, TextCsvWriter actualValue)
        {
			Assert.AreEqual(expectedValue, actualValue.BaseWriter.ToString());
        }

		[TestMethod]
		public void TryTextCsvWriter01()
        {
			var textWriter = new StringWriter();
			textWriter.Write("\" ForcedQuotation\", 123");
			TestTextCsvWriter("\" ForcedQuotation\", \"123\"", new TextCsvWriter(textWriter, new TextCsvSettings() { Quotation = CsvQuotation.Forced, Quote='\"', Delemiter = ',' }));
		}

		[TestMethod]
		public void TryTextCsvWriter02()
		{
			var input = String.Join(Environment.NewLine,
				"ForcedText;",
				123);
			var textWriter = new StringWriter();
			textWriter.Write(input);
			TestTextCsvWriter("\"ForcedText\";123", new TextCsvWriter(textWriter, new TextCsvSettings() { Quotation = CsvQuotation.ForceText, Quote = '\"', Delemiter= ';' }));
		}

		[TestMethod]
		public void TryTextCsvWriter03()
		{
			var textWriter = new StringWriter();
			textWriter.Write("noQuotation, 123");
			TestTextCsvWriter("noQuotation, 123", new TextCsvWriter(textWriter, new TextCsvSettings() { Quotation = CsvQuotation.None, Quote = '\"', Delemiter = ',' }));
		}

		[TestMethod]
		public void TryTextCsvWriter04()
		{
			var textWriter = new StringWriter();
			textWriter.Write("\"noQuotation\", 123");
			TestTextCsvWriter("\"noQuotation\", 123", new TextCsvWriter(textWriter, new TextCsvSettings() { Quotation = CsvQuotation.None, Quote = '\"', Delemiter = ',' }));
		}

		[TestMethod]
		public void TryTextCsvWriter05()
		{
			var textWriter = new StringWriter();
			textWriter.Write("\"normal\", 123");
			TestTextCsvWriter("\"normal\", 123", new TextCsvWriter(textWriter, new TextCsvSettings() { Quotation = CsvQuotation.Normal, Quote = '\"', Delemiter = ',' }));
		}

		// Tests für TextDataRowWriter

	
		public void TestTextDataRowWriter(string expectedValue, StringWriter sw, TextFixedSettings settings, params IDataColumn[] columns)
        {
			Assert.AreEqual(expectedValue, new TextDataRowWriter(sw, settings, columns).CoreWriter.BaseWriter.ToString());
        }

		[TestMethod]
		public void TryTextDataRowWriter01()
        {
			var textWriter = new StringWriter();
			textWriter.Write(sampleSimple01);
			TestTextDataRowWriter(sampleSimple01, textWriter, new TextFixedSettings() { Lengths = new int[] { 2 } });
        }

		[TestMethod]
		public void TryTextDataRowWriter02()
		{
			var textWriter = new StringWriter();
			textWriter.Write(sampleSimple02);
			TestTextDataRowWriter(sampleSimple02, textWriter, new TextFixedSettings() { Lengths = new int[] { 2 } });
		}

		[TestMethod]
		public void TryTextDataRowWriter03()
		{
			var textWriter = new StringWriter();
			textWriter.Write(sampleSimple03);
			TestTextDataRowWriter(sampleSimple03, textWriter, new TextFixedSettings() { Lengths = new int[] { 2 } });
		}


	}
}
