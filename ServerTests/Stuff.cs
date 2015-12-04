using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class LogTests
	{
		[TestMethod]
		public void ConvertDateTime()
		{
			Assert.AreEqual("1982-05-26 03:48:03:236", LogLineParser.ConvertDateTime(new DateTime(1982, 5, 26, 3, 48, 3, 236)));
			Assert.AreEqual("1982-05-26 15:48:14:236", LogLineParser.ConvertDateTime(new DateTime(1982, 5, 26, 15, 48, 14, 236)));
		}

		[TestMethod]
		public void ParseDateTime()
		{
			Assert.AreEqual(new DateTime(1982, 5, 26, 3, 48, 3, 236), LogLineParser.ParseDateTime("1982-05-26 03:48:03:236"));
			Assert.AreEqual(new DateTime(1982, 5, 26, 15, 48, 14, 236), LogLineParser.ParseDateTime("1982-05-26 15:48:14:236"));
		}

		[TestMethod]
		public void ParseLogLine()
		{
			LogMsgType t;
			DateTime dt;
			string text;
			LogLineParser.Parse("2014-08-05 10:04:46:402	0	Main: Konfiguration wird geladen...", out t, out dt, out text);
			Assert.AreEqual(LogMsgType.Information, t);
			Assert.AreEqual(new DateTime(2014, 08, 5, 10, 4, 46, 402), dt);
			Assert.AreEqual("Main: Konfiguration wird geladen...", text);

			LogLineParser.Parse("2014-08-05 10:04:46:402	2	Main: Konfiguration wird geladen...", out t, out dt, out text);
			Assert.AreEqual(LogMsgType.Error, t);
			Assert.AreEqual(new DateTime(2014, 08, 5, 10, 4, 46, 402), dt);
			Assert.AreEqual("Main: Konfiguration wird geladen...", text);
		}
	}

	[TestClass]
	public class CronBoundTests
	{
		[TestMethod]
		public void TimeTest01()
		{
			var cron = new CronBound("12:25");
			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 25, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 0, 6)));
		}
	}
}
