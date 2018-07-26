using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Networking;
using TecWare.DE.Server.Http;

namespace TecWare.DE.Server
{
	[TestClass]
	public class HtmlMimeType
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

	[TestClass]
	public class HtmlParser
	{
		private static string T(params string[] lines)
			=> String.Join(Environment.NewLine, lines) + Environment.NewLine;

		private static void TestParser(bool isExpectedPlainHtml, bool isExpectedOpenOutput, string expected, string lines)
		{
			var source = String.Join(Environment.NewLine, lines);
			using (var tr = new StringReader(source))
			{
				var s = HttpResponseHelper.ParseHtml(tr, 0, out var isPlainHtml, out var openOutput);
				Assert.AreEqual(isExpectedPlainHtml, isPlainHtml);
				Assert.AreEqual(isExpectedOpenOutput, openOutput);
				Assert.AreEqual(expected ?? source, s);
			}
		}

		[TestMethod]
		public void ParsePlainTest()
			=> TestParser(true, true, null, "<html> < a% >");


		[TestMethod]
		public void ParseLuaTest()
			=> TestParser(false, true, T("print(\"<html>\");","test(); ","print(\"</html>\");"), "<html><%test(); %></html>");

		[TestMethod]
		public void ParseOutputTest()
			=> TestParser(false, false, T("otext();", "test();", "print(\"<html>\");", "test(); ", "print(\"</html>\");"), "  <%otext();%> <%test();%> <html><%test(); %></html>");

		[TestMethod]
		public void ParseVarTest()
			=> TestParser(false, true, T("print(\"<html>\");", "printValue(test, \"N0\");", "print(\"</html>\");"), "<html><%=test::N0%></html>");
	}
}
