using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
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
			var cron = new CronBound("0,10,*");
			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 30, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 25, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 03, 13, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 58, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 03, 13, 10, 0), cron.GetNext(new DateTime(2015, 12, 03, 13, 00, 6)));
		}

		[TestMethod]
		public void TimeTest02()
		{
			var cron = new CronBound("12:00,10,*");
			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 08, 25, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 08, 58, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 04, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 14, 25, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 04, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 14, 58, 6)));

			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 30, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 25, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 04, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 58, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 04, 12, 00, 0), cron.GetNext(new DateTime(2015, 12, 03, 13, 00, 6)));

			cron = new CronBound("12:25");
			Assert.AreEqual(new DateTime(2015, 12, 04, 12, 25, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 30, 6)));
			Assert.AreEqual(new DateTime(2015, 12, 03, 12, 25, 0), cron.GetNext(new DateTime(2015, 12, 03, 12, 0, 6)));

			cron = new CronBound("");
			Assert.AreEqual(new DateTime(2016, 02, 09, 13, 0, 0), cron.GetNext(new DateTime(2016, 02, 09, 12, 0, 0)));
			Assert.AreEqual(new DateTime(2016, 02, 10, 00, 0, 0), cron.GetNext(new DateTime(2016, 02, 09, 23, 0, 0)));
			Assert.AreEqual(new DateTime(2016, 02, 10, 01, 0, 0), cron.GetNext(new DateTime(2016, 02, 10, 00, 0, 0)));
		}

		[TestMethod]
		public void TimeTest03()
		{
			var cron = new CronBound("1,31,Mi,So 1:00");

			Assert.AreEqual(new DateTime(2016, 02, 01, 1, 0, 0), cron.GetNext(new DateTime(2016, 02, 01, 0, 2, 0)));
			Assert.AreEqual(new DateTime(2016, 02, 01, 1, 0, 0), cron.GetNext(new DateTime(2016, 01, 31, 10, 0, 0)));
			Assert.AreEqual(new DateTime(2016, 02, 28, 1, 0, 0), cron.GetNext(new DateTime(2016, 02, 27, 10, 0, 0)));

			cron = new CronBound("31 1:00");

			Assert.AreEqual(new DateTime(2016, 01, 31, 1, 0, 0), cron.GetNext(new DateTime(2016, 01, 05, 0, 2, 0)));
			Assert.AreEqual(new DateTime(2016, 02, 29, 1, 0, 0), cron.GetNext(new DateTime(2016, 01, 31, 1, 2, 0)));
			Assert.AreEqual(new DateTime(2016, 03, 31, 1, 0, 0), cron.GetNext(new DateTime(2016, 02, 29, 10, 0, 0)));
		}

		[TestMethod]
		public void XmlSplitPaths()
		{
			var t = Procs.SplitPaths("a b c").ToArray();
			Assert.AreEqual("a", t[0]);
			Assert.AreEqual("b", t[1]);
			Assert.AreEqual("c", t[2]);

			t = Procs.SplitPaths("a \"b\" \"c\\\"").ToArray();
			Assert.AreEqual("a", t[0]);
			Assert.AreEqual("b", t[1]);
			Assert.AreEqual("c\\", t[2]);
		}

		[TestMethod]
		public void XmlRemoveInvalidChars()
		{
			Assert.AreEqual(ProcsDE.RemoveInvalidXmlChars(null), null);
			Assert.AreEqual(ProcsDE.RemoveInvalidXmlChars(String.Empty), String.Empty);
			Assert.AreEqual(ProcsDE.RemoveInvalidXmlChars("String.Empty"), "String.Empty");
			Assert.AreEqual(XmlConvert.VerifyXmlChars(ProcsDE.RemoveInvalidXmlChars("String\x1A.Empty")), "String.Empty");
			Assert.AreEqual(XmlConvert.VerifyXmlChars(ProcsDE.RemoveInvalidXmlChars("\x001AEmp\x001Aty\x001A")), "Empty");
			Assert.AreEqual(XmlConvert.VerifyXmlChars(ProcsDE.RemoveInvalidXmlChars("String\x001AEmp\x10000ty")), "StringEmp\x10000ty");
		}

	}

	[TestClass]
	public class CertFindTest
	{
		[TestMethod]
		public void CertFind01()
		{
			var certs = ProcsDE.FindCertificate("store://cu/root/CN=GlobalSign").ToArray();
			Assert.IsTrue(certs.Count() != 0);
		}

		[TestMethod]
		public void CertFind02()
		{
			var certs = ProcsDE.FindCertificate("store://cu/root/subject:CN=GlobalSign").ToArray();
			Assert.IsTrue(certs.Count() != 0);
		}

		[TestMethod]
		public void CertFind03()
		{
			var certs = ProcsDE.FindCertificate("store://cu/root/thumbprint:75e0abb6138512271c04f85fddde38e4b7242efe").ToArray();
			//X509KeyUsageExtension a;
			//a.KeyUsages = X509KeyUsageFlags.
			Assert.IsTrue(certs.Count() == 1);
		}
	}
}
