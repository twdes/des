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

		// TestMethodes for csv-writer
		private static bool GetQuotedNormal(string v, char quote)
		{
			if (String.IsNullOrEmpty(v))
				return false;

			for (var i = 0; i < v.Length; i++)
			{
				if (v[i] == quote || Char.IsControl(v[i]))
					return true;
			}

			return false;
		}

		private void TestGetQuotedNormal(bool expectedValue, string v, char quote)
		{
			Assert.AreEqual(expectedValue, GetQuotedNormal(v, quote));
		}


		[TestMethod]
		public void TestGetQuotedNormal01()
		{
			const char quote = '"';
			const bool expectedValue = true;
			TestGetQuotedNormal(expectedValue, sampleSimple01, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal02()
		{
			const char quote = '"';
			const bool expectedValue = true;
			TestGetQuotedNormal(expectedValue, sampleSimple02, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal03()
		{
			const char quote = '"';
			const bool expectedValue = false;
			TestGetQuotedNormal(expectedValue, sampleSimple03, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal04()
		{
			const string v = "'Hallo ich bin ein Quote'";
			const char quote = '\'';
			const bool expectedValue = true;
			TestGetQuotedNormal(expectedValue, v, quote);
		}
		public void TestGetQuotedNormal05()
		{
			const string v = "Ich bin kein Quote";
			const char quote = '\'';
			const bool expectedValue = true;
			TestGetQuotedNormal(expectedValue, v, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal06()
		{
			const string v = "Hallo ich bin ein Text";
			const char quote = '\'';
			const bool expectedValue = false;
			TestGetQuotedNormal(expectedValue, v, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal07()
		{
			const string v = "\"Hallo ich bin ein Text\"";
			const char quote = '\'';
			const bool expectedValue = false;
			TestGetQuotedNormal(expectedValue, v, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal08()
		{
			const string v = "";
			const char quote = '"';
			const bool expectedValue = false;
			TestGetQuotedNormal(expectedValue, v, quote);
		}

		[TestMethod]
		public void TestGetQuotedNormal09()
		{
			const string v = null;
			const char quote = '\'';
			const bool expectedValue = false;
			TestGetQuotedNormal(expectedValue, v, quote);
		}


		private string WriteRowValue(string v, bool quoted)
		{
			//var quoted = quoteValue(v, isText);
			var returnvalue = "";

			if (!quoted)
			{
				//BaseWriter.Write(v);
				returnvalue = v;
			}
			else if (v == null)
				//BaseWriter.Write("\"\"");
				returnvalue += "\"\"";
			else
			{
				var len = v.Length;
				//BaseWriter.Write("\"");
				returnvalue = "\"";
				for (var i = 0; i < len; i++)
				{
					if (v[i] == '"')
						//BaseWriter.Write("\"\"");
						returnvalue += "\"\"";
					else
						//BaseWriter.Write(v[i]);
						returnvalue += v[i].ToString();
				}
				//BaseWriter.Write("\"");
				returnvalue += "\"";
			}
			return returnvalue;
		} // proc WriteRowValue

		public void TestWriteRowValue(string v, bool quoted, string expectedValue)
		{
			Assert.AreEqual(expectedValue, WriteRowValue(v, quoted));
		}

		[TestMethod]
		public void TestWriteRowValue01()
		{
			var v = "Keine Quotes";
			var quoted = false;
			var expectedValue = "Keine Quotes";

			TestWriteRowValue(v, quoted, expectedValue);

		}

		[TestMethod]
		public void TestWriteRowValue02()
		{
			var v = "\"Quotes\"";
			var quoted = true;
			var expectedValue = "\"\"\"Quotes\"\"\"";

			TestWriteRowValue(v, quoted, expectedValue);

		}

		[TestMethod]
		public void TestWriteRowValue03()
		{
			var v = "A \"Quotes\"";
			var quoted = true;
			var expectedValue = "\"A \"\"Quotes\"\"\"";

			TestWriteRowValue(v, quoted, expectedValue);
		}

		[TestMethod]
		public void TestWriteRowValue04()
		{
			string v = null;
			var quoted = true;
			var expectedValue = "\"\"";

			TestWriteRowValue(v, quoted, expectedValue);
		}


		public string WriteRow(IEnumerable<string> values, char delemiter, bool quoted)
		{
			var returnvalue = "";
			//var delemiter = Settings.Delemiter;
			var i = 0;
			foreach (var v in values)
			{
				if (i > 0)
					//BaseWriter.Write(delemiter);
					returnvalue += delemiter.ToString();

				returnvalue += WriteRowValue(v, quoted);
				i++;
			}
			//BaseWriter.WriteLine();
			returnvalue += "\\";

			return returnvalue;
		} // proc WriteRow

		public void TestWriteRow(IEnumerable<string> values, char delemiter, bool quoted, string expectedValue)
        {
			Assert.AreEqual(expectedValue, WriteRow(values, delemiter, quoted));
        }

		[TestMethod]
		public void TestWriteRow01()
        {
			var values = new List<string>
			{
				"Vorname",
				"Nachname"
			};
			var delemiter = ',';
			var quoted = false;
			var expectedValue = "Vorname,Nachname\\";

			TestWriteRow(values, delemiter, quoted, expectedValue);
		}

		[TestMethod]
		public void TestWriteRow02()
		{
			var values = new List<string>
			{
				"\"Vorname\"",
				"\"Nachname\""
			};
			var delemiter = ',';
			var quoted = false;
			var expectedValue = "\"Vorname\",\"Nachname\"\\";

			TestWriteRow(values, delemiter, quoted, expectedValue);
		}

		[TestMethod]
		public void TestWriteRow03()
		{
			var values = new List<string>
			{
				";Vorname",
				"Nachname,"
			};
			var delemiter = ',';
			var quoted = false;
			var expectedValue = ";Vorname,Nachname,\\";

			TestWriteRow(values, delemiter, quoted, expectedValue);
		}


	}
}
