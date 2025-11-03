using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Odette;
using TecWare.DE.Odette.Services;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class Oftp
	{
		private class DummyReader : IOdetteFileReader
		{
			private byte[][] src;
			private int recordIndex;
			private int recordOffset;

			public DummyReader(byte[][] src)
			{
				this.src = src;
				this.recordIndex = 0;
				this.recordOffset = 0;
			} // ctor

			public void Dispose()
			{
			}

			public Task<(int readed, bool isEoR)> ReadAsync(byte[] buf, int offset, int count)
			{
				if (recordIndex == src.Length)
					return Task.FromResult((-1, true));
				else
				{
					bool isEoR;
					var srcBuffer = src[recordIndex];
					var l = Math.Min(count, srcBuffer.Length - recordOffset);
					Array.Copy(srcBuffer, recordOffset, buf, offset, l);
					recordOffset += l;
					if (recordOffset == srcBuffer.Length)
					{
						recordIndex++;
						recordOffset = 0;
						isEoR = true;
					}
					else
						isEoR = false;
					return Task.FromResult((l, isEoR));
				}
			} // func Read

			public Task SetTransmissionErrorAsync(OdetteAnswerReason answerReason, string reasonText, bool retryFlag) { throw new NotImplementedException(); }
			public Task SetTransmissionStateAsync() { throw new NotImplementedException(); }

			public IOdetteFile Name { get { throw new NotImplementedException(); } }
			public long RecordCount { get { throw new NotImplementedException(); } }
			public long TotalLength { get { throw new NotImplementedException(); } }
			public string UserData { get { throw new NotImplementedException(); } }
		} // class DummyReader

		public class DummyWriter : IOdetteFileWriter
		{
			private List<MemoryStream> dst;

			public DummyWriter()
			{
				dst = new List<MemoryStream>
				{
					new MemoryStream() // initial record
				};
			} // ctor

			public void AssertData(byte[][] src)
			{
				Assert.AreEqual(src.Length, dst.Count - 1, "Sub record count test failed."); // equal?

				for (var i = 0; i < src.Length; i++)
				{
					var dstR = dst[i].ToArray();
					var srcR = src[i];
					Assert.HasCount(dstR.Length, srcR, $"Sub record length test failed for index {i:N0}.");
					for (var j = 0; j < dstR.Length; j++)
						Assert.AreEqual(dstR[j], srcR[j], $"Sub record compare test failed for index {i:N0} at byte {j:N0}.");
				}
			} // proc AssertData

			public void Dispose()
			{
			}

			public Task WriteAsync(byte[] buf, int offset, int count, bool isEoR)
			{
				dst[dst.Count - 1].Write(buf, offset, count);
				if (isEoR)
					dst.Add(new MemoryStream());
				return Task.CompletedTask;
			} // proc Write

			public Task CommitFileAsync(long recordCount, long unitCount) { throw new NotImplementedException(); }

			public IOdetteFile Name { get { throw new NotImplementedException(); } }

			public long RecordCount { get { throw new NotImplementedException(); } }
			public long TotalLength { get { throw new NotImplementedException(); } }
		} // class DummyWriter

		private void TestSubRecordChain(byte[][] src, bool allowCompressing, int bufferSize)
		{
			var reader = new DummyReader(src);
			var writer = new DummyWriter();
			var buffer = new byte[bufferSize];

			while (true)
			{
				var eos = OdetteFtpCore.FillFromStreamInternalAsync(reader, allowCompressing, buffer).Result;
				OdetteFtpCore.WriteToStreamInternalAsync(writer, buffer, eos.dataLength).Wait();
				if (eos.endOfStream)
					break;
			}

			writer.AssertData(src);
		} // proc TestSubRecordChain

		private static byte Header(int len, bool repeat, bool eof)
		{
			if (eof)
				len |= 0x80;
			if (repeat)
				len |= 0x40;

			return (byte)len;
		} // func Header

		[TestMethod]
		public void SubRecordWriterTest01()
		{
			var writer = new DummyWriter();

			var buf = new byte[] { 0, Header(3, true, false), (byte)'=', Header(3, false, false), (byte)' ', (byte)'H', (byte)'a', Header(2, true, false), (byte)'l', Header(2, false, false), (byte)'o', (byte)' ', Header(3, true, true), (byte)'=',
				Header(4, false, true), (byte)'W', (byte)'e', (byte)'l', (byte)'t'
			};

			Assert.IsTrue(OdetteFtpCore.WriteToStreamInternalAsync(writer, buf, buf.Length).Result, "eor not reached.");

			writer.AssertData(
				new byte[][]
				{
					new byte[] {(byte)'=',(byte)'=',(byte)'=',(byte)' ', (byte)'H', (byte)'a',(byte)'l',(byte)'l',(byte)'o', (byte)' ', (byte)'=', (byte)'=', (byte)'=' },
					new byte[] { (byte)'W', (byte)'e', (byte)'l', (byte)'t' }
				}
			);
		} // proc SubRecordWriterTest01

		[TestMethod]
		public void SubRecordReaderTest02()
		{
			// uncompressed
			TestSubRecordChain(
				new byte[][]
				{
								Encoding.ASCII.GetBytes("= 1 = Hallo Welt ====="),
								Encoding.ASCII.GetBytes("= 2 = Hallo Welt ====="),
								Encoding.ASCII.GetBytes("= 3 = Hallo Welt =====")
				}, false, 1024);
		}

		[TestMethod]
		public void SubRecordReaderTest03()
		{
			// uncompressed
			TestSubRecordChain(
				new byte[][]
				{
					Encoding.ASCII.GetBytes("= 1 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 2 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 3 = Hallo Welt =====")
				}, false, 16);
		}

		[TestMethod]
		public void SubRecordReaderTest04()
		{
			// compressed
			TestSubRecordChain(
				new byte[][]
				{
					Encoding.ASCII.GetBytes("= 1 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 2 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 3 = Hallo Welt =====")
				}, true, 1024);
		}

		[TestMethod]
		public void SubRecordReaderTest05()
		{
			// compressed
			TestSubRecordChain(
				new byte[][]
				{
					Encoding.ASCII.GetBytes("= 1 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 2 = Hallo Welt ====="),
					Encoding.ASCII.GetBytes("= 3 = Hallo Welt =====")
				}, true, 16);
		}

		[TestMethod]
		public void SubRecordReaderTest06()
		{
			// compressed with buffer move
			TestSubRecordChain(
				new byte[][]
				{
					Encoding.ASCII.GetBytes("===== Hallo ===== WELT ===================================================================")
				}, true, 16);
		}

		[TestMethod]
		public void CertificateSelect01()
		{
			foreach (var c in ProcsDE.FindCertificate("store://currentuser/my/CN=OFTP2Test"))
				Console.WriteLine(c.Subject);
		}

		[TestMethod]
		public void RegEx01()
		{
			var names = new Tuple<bool, string>[]
			   {
					new Tuple<bool, string>(false, ""),
					new Tuple<bool, string>(true, "ABZ#WICHTIG.TXT#201501011405580045.state"),
					new Tuple<bool, string>(true, "ABZ#& ()&.-#201512041207500008.state"),
					new Tuple<bool, string>(true, "A#WA DDD#201512041200270002.state"),
					new Tuple<bool, string>(true, "A 2#600_KB Test.txt#201512041200270002.state"),
					new Tuple<bool, string>(false, "A##201512041200270002.state")
			   };

			foreach (var t in names)
			{
				var m = Regex.Match(t.Item2, DirectoryFileServiceItem.fileSelectorRegEx);
				Assert.AreEqual(t.Item1, m.Success, String.Format("{0}", t));
				if (m.Success)
				{
					Assert.HasCount(4, m.Groups);
					Console.WriteLine("{0} -> Orignator: {1}, File: {2}, Date: {3}", t.Item2, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
				}
			}
		}
	}
}
