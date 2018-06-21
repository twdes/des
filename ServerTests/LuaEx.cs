using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class LuaEx
	{
		[TestMethod]
		public void TestToXml()
		{
			using (var lua = new Lua())
			{
				var f = lua.CreateLambda<Func<LuaTable>>("test.lua", "return { ['test  a'] = 12, IsActive = false, hallo = 'Welt', 1, 4, sub = { sub = 'test', guid = clr.System.Guid.NewGuid() }, subarray = { 1, 2, 3, 4, 5 } }");
				var t = f();

				var x = t.ToXml();
				Console.WriteLine(x.ToString());

				var t2 = Procs.CreateLuaTable(x);
				Assert.AreEqual(12, t2["test  a"]);
				Assert.AreEqual("Welt", t2["hallo"]);
				Assert.AreEqual(4, t2[2]);
				// todo:
			}
		}
	}
}
