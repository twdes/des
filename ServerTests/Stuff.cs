using System;
using System.Collections.Generic;
using System.IO;
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
		private static readonly DateTime BaseStamp = new DateTime(2015, 05, 26, 12, 0, 0, 0);

		#region -- Basic --

		[TestMethod]
		public void ConvertDateTime01()
		{
			Assert.AreEqual("1982-05-26 03:48:03:236", LogLineParser.ConvertDateTime(new DateTime(1982, 5, 26, 3, 48, 3, 236)));
			Assert.AreEqual("1982-05-26 15:48:14:236", LogLineParser.ConvertDateTime(new DateTime(1982, 5, 26, 15, 48, 14, 236)));
		}

		[TestMethod]
		public void ParseDateTime01()
		{
			Assert.AreEqual(new DateTime(1982, 5, 26, 3, 48, 3, 236), LogLineParser.ParseDateTime("1982-05-26 03:48:03:236"));
			Assert.AreEqual(new DateTime(1982, 5, 26, 15, 48, 14, 236), LogLineParser.ParseDateTime("1982-05-26 15:48:14:236"));
		}

		[TestMethod]
		public void ParseLogLine01()
		{
			LogMsgType t;
			DateTime dt;
			string text;
			LogLineParser.Parse("2014-08-05 10:04:46:402\t0\tMain: Konfiguration wird geladen...", out t, out dt, out text);
			Assert.AreEqual(LogMsgType.Information, t);
			Assert.AreEqual(new DateTime(2014, 08, 5, 10, 4, 46, 402), dt);
			Assert.AreEqual("Main: Konfiguration wird geladen...", text);

			LogLineParser.Parse("2014-08-05 10:04:46:402\t2\tMain: Konfiguration wird geladen...", out t, out dt, out text);
			Assert.AreEqual(LogMsgType.Error, t);
			Assert.AreEqual(new DateTime(2014, 08, 5, 10, 4, 46, 402), dt);
			Assert.AreEqual("Main: Konfiguration wird geladen...", text);
		}

		#endregion

		[TestMethod]
		public void LogLineTest01()
		{
			var log = new DELogLine(BaseStamp, LogMsgType.Information, "Test data");
			Assert.AreEqual("2015-05-26 12:00:00:000\t0\tTest data", log.ToLineData());
		}

		[TestMethod]
		public void LogLineTest02()
		{
			var log = new DELogLine(BaseStamp, LogMsgType.Error, "Test\n\t\rdata\\test\0null");
			Assert.AreEqual("2015-05-26 12:00:00:000\t2\tTest\\n\\tdata\\\\test\\0null", log.ToLineData());
		}

		[TestMethod]
		public void LogLineTest03()
		{
			var log = new DELogLine("2015-05-26 12:00:00:000\t0\tTest data");
			Assert.AreEqual(BaseStamp, log.Stamp);
			Assert.AreEqual(LogMsgType.Information, log.Typ);
			Assert.AreEqual("Test data", log.Text);
		}

		[TestMethod]
		public void LogLineTest04()
		{
			var log = new DELogLine("2015-05-26 12:00:00:000\t2\tTest\\n\\tdata\\\\test\\0null");
			Assert.AreEqual(BaseStamp, log.Stamp);
			Assert.AreEqual(LogMsgType.Error, log.Typ);
			Assert.AreEqual("Test\r\n\tdata\\test\0null", log.Text);
		}

		[TestMethod]
		public void LogTest01()
		{
			const string fileName = @"Temp\\Test01.txt";
			if (File.Exists(fileName))
				File.Delete(fileName);

			using (var log = new DELogFile(fileName))
			{
				for (var i = 0; i < 100; i++)
					log.Add(new DELogLine(BaseStamp.AddSeconds(i), LogMsgType.Information, $"Test Line {i}"));
			}

			Assert.AreEqual(3990, new FileInfo(fileName).Length);

			using (var log = new DELogFile(fileName))
			{
				for (var i = 0; i < 100; i++)
					log.Add(new DELogLine(BaseStamp.AddSeconds(i), LogMsgType.Error, $"Test Line {i}"));
			}

			Assert.AreEqual(3990 * 2, new FileInfo(fileName).Length);

			using (var log = new DELogFile(fileName))
			{
				log.SetSize(3992, 7000);
				log.Add(new DELogLine(BaseStamp, LogMsgType.Warning, "Last Line"));
			}

			Assert.AreEqual(4187, new FileInfo(fileName).Length);

			using (var log = new DELogFile(fileName))
			{
				log.SetSize(0, 3000);
				log.Add(new DELogLine(BaseStamp, LogMsgType.Warning, "Last Line 2"));
			}

			Assert.AreEqual(396, new FileInfo(fileName).Length);
		}

		[TestMethod]
		public void LogTest02()
		{
			const string fileName = @"Temp\\Test02.txt";
			if (File.Exists(fileName))
				File.Delete(fileName);

			using (var log = new DELogFile(fileName))
			{
				for (var i = 0; i < 100; i++)
					log.Add(new DELogLine(BaseStamp.AddSeconds(i), LogMsgType.Information, $"Test Line {i}"));
			}

			Assert.AreEqual(3990, new FileInfo(fileName).Length);

			using (var log = new DELogFile(fileName))
			{
				var i = 0;
				foreach (DELogLine l in log)
					Assert.AreEqual($"Test Line {i++}", l.Text);

				i = 50;
				var e = log.GetEnumerator(50, 60);
				while (e.MoveNext())
					Assert.AreEqual($"Test Line {i++}", e.Current.Text);
			}
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
