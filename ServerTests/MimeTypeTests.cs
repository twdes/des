using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Networking;

namespace TecWare.DE.Server
{
	[TestClass]
	public class MimeTypeTests
	{
		[TestMethod]
		public void MimeTypeGet()
		{
			Assert.IsTrue(MimeTypeMapping.TryGetMapping(MimeTypes.Text.Plain, out var mapping));
			Assert.AreEqual(".txt", mapping.Extensions[0]);
			Assert.IsFalse(mapping.IsCompressedContent);

			MimeTypeMapping.Update("quark", false, true, ".quark", ".txt");
			Assert.AreEqual(".quark", MimeTypeMapping.GetExtensionFromMimeType("quark"));
			Assert.AreEqual("quark", MimeTypeMapping.GetMimeTypeFromExtension(".quark"));

			MimeTypeMapping.Update(MimeTypes.Text.Plain, false, false, ".txt", ".text");
			Assert.AreEqual(MimeTypes.Text.Plain, MimeTypeMapping.GetMimeTypeFromExtension(".text"));
		}
	}
}
