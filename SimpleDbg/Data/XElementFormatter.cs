#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
using System;
using System.IO;
using System.Xml.Linq;

namespace TecWare.DE.Server.Data
{
	internal static class XElementFormatter
	{
		private static bool IsComplexType(XElement x)
			=> x.HasAttributes || x.HasElements;

		private static void Format(TextWriter tw, string indent, XName name, string value)
		{
			tw.Write(indent);
			tw.Write(name.LocalName);
			tw.Write(" = ");
			tw.WriteLine(value);
		} // proc Format

		private static void Format(TextWriter tw, string indent, XElement x)
		{
			tw.Write(indent);
			tw.WriteLine(x.Name.LocalName);

			indent = indent + "    ";

			// write attributes
			var a = x.FirstAttribute;
			while (a != null)
			{
				Format(tw, indent, a.Name, a.Value);
				a = a.NextAttribute;
			}

			// write nodes
			var n = x.FirstNode;
			while (n != null)
			{
				if (n is XElement e)
				{
					if (IsComplexType(e))
						Format(tw, indent, e);
					else
						Format(tw, indent, e.Name, e.Value);
				}
				else if (n is XCData d)
					Format(tw, indent, ".", d.Value);
				else if (n is XText t)
					Format(tw, indent, ".", t.Value);

				n = n.NextNode;
			}

		} // proc Format

		public static string Format(this XElement x)
		{
			using (var tw = new StringWriter())
			{
				Format(tw, String.Empty, x);
				return tw.GetStringBuilder().ToString();
			}
		} // func Format
	} // class XElementFormatter
}
