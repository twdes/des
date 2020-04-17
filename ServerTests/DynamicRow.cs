using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Data;

namespace TecWare.DE.Server
{
	[TestClass]
	public class DynamicRow
	{
		private static readonly SimpleDataColumn[] columns1 = new SimpleDataColumn[]
		{
			new SimpleDataColumn("A1", typeof(string)),
			new SimpleDataColumn("B2", typeof(string))
		};

		private static readonly SimpleDataColumn[] columns2 = new SimpleDataColumn[]
		{
			new SimpleDataColumn("B2", typeof(string)),
			new SimpleDataColumn("C3", typeof(string))
		};

		private class Row : DynamicDataRow
		{
			private readonly string[] values;
			private readonly IDataColumn[] columns;

			public Row(string[] values, IDataColumn[] columns)
			{
				this.values = values;
				this.columns = columns;
			}

			protected override void SetValueCore(int index, object value)
				=> values[index] = value as string;

			public override bool IsDataOwner => true;

			public override object this[int index] => values[index];
			public override IReadOnlyList<IDataColumn> Columns => columns;
		} // class Row

		[TestMethod]
		public void TestDynamic01()
		{
			dynamic r1 = new Row(new string[] { "a", "b" }, columns1);
			dynamic r2 = new Row(new string[] { "c", "d" }, columns2);
			dynamic r3 = new Row(new string[] { "e", "f" }, columns1);

			void AssertValues()
			{
				Assert.AreEqual(r1.A1, "a");
				Assert.AreEqual(r1.B2, "b");
				Assert.AreEqual(r2.B2, "c");
				Assert.AreEqual(r2.C3, "d");
				Assert.AreEqual(r3.A1, "e");
				Assert.AreEqual(r3.B2, "f");
			}

			AssertValues();
			AssertValues();

			r1.A1 = "a1";
			r1.B2 = "b1";
			r2.B2 = "c1";
			r2[1] = "d1";
			r3.A1 = "e1";
			r3["B2"] = "f1";

			Assert.AreEqual(r1.A1, "a1");
			Assert.AreEqual(r1.B2, "b1");
			Assert.AreEqual(r2.B2, "c1");
			Assert.AreEqual(r2[1], "d1");
			Assert.AreEqual(r3["A1"], "e1");
			Assert.AreEqual(r3.B2, "f1");
			Assert.AreEqual(r1.NNN, null);
			Assert.AreEqual(r1["NNN", false], null);
		}
	}
}
