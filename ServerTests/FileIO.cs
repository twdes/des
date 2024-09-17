using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using TecWare.DE.Server.IO;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class FileIO
	{
		[TestMethod]
		public void TestTransactionStream01()
		{
			var an = @"C:\Temp\Test.dat";
			var bn = @"M:\Backup\kiwi-z\Stein\Documents\American Truck Simulator\screenshot\ats_00010.png";

			using (var b = new FileInfo(bn).OpenRead())
			{
				using (var a = DEFile.OpenCopyAsync(an).Result)
				{
					b.CopyTo(a);
					a.Commit();
				}

				b.Position = 0;

				using (var c = new FileInfo(an).OpenRead())
				{
					var buf = new byte[1024];
					var buf2 = new byte[1024];
					while (true)
					{
						var r = b.Read(buf, 0, buf.Length);
						if (r == 0)
							break;
						var r2 = c.Read(buf2, 0, r);
						if (r != r2)
							Assert.Fail();
						if (!Procs.CompareBytes(buf, 0, buf2, 0, r))
							Assert.Fail();
					}
				}
			}
		}
	}
}