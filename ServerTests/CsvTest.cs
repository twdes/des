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
			"Preis;Datum",
			"5.9;2015-02-05",
			"5.1;2015-02-02");

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
					new TextDataRowColumn() { Name = header[1], DataType = typeof(DateTime), FormatProvider = CultureInfo.InvariantCulture }
				);

				while (r.MoveNext())
				{
					var c = r.Current;
					if (r.Row == 0)
					{
						Assert.AreEqual(5.9m, c[0]);
						Assert.AreEqual(new DateTime(2015, 2, 5), c[1]);
					}
					else if (r.Row == 1)
					{
						Assert.AreEqual(5.1m, c["Preis"]);
						Assert.AreEqual(new DateTime(2015, 2, 2), c["Datum"]);
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
	}
}
