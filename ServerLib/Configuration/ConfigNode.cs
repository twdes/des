using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Configuration
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class XConfigNode
	{
		private readonly IDEConfigurationAttribute[] attributes;
		private readonly XElement element;

		public XConfigNode(IDEConfigurationElement elementDefinition, XElement element)
		{
			this.attributes = elementDefinition.GetAttributes().ToArray();
			this.element = element;
		} // ctor

		public T GetAttribute<T>(string name)
		=> Procs.ChangeType<T>(GetAttribute(name));

		public object GetAttribute(string name)
		{
			var attribute = attributes.FirstOrDefault(c => String.Compare(c.Name.LocalName, name, StringComparison.OrdinalIgnoreCase) == 0);
			if (attribute == null)
				throw new ArgumentException(String.Format("@{0} is not defined.", name));

			var attributeValue = element.Attribute(attribute.Name)?.Value ?? attribute.DefaultValue;

			if (attribute.TypeName == "LuaType")
			{
				if (attributeValue == null)
					attributeValue = "object";
				return LuaType.GetType(attributeValue, false, false).Type;
			}
			else if (attribute.TypeName == "EncodingType")
			{
				int codePage;

				if (String.IsNullOrEmpty(attributeValue))
					attributeValue = attribute.DefaultValue;

				if (String.IsNullOrEmpty(attributeValue))
					return Encoding.Default;
				else if (int.TryParse(attributeValue, out codePage))
					return Encoding.GetEncoding(codePage);
				else
					return Encoding.GetEncoding(attributeValue);
			}
			else if (attribute.TypeName == "language")
			{
				return String.IsNullOrEmpty(attributeValue) ?
					CultureInfo.GetCultureInfo(attribute.DefaultValue) :
					CultureInfo.GetCultureInfo(attributeValue);
			}
			else
			{
				var type = attribute.Type;
				try
				{
					return Procs.ChangeType(attributeValue, type);
				}
				catch
				{
					return Procs.ChangeType(attribute.DefaultValue, type);
				}
			}
		} // func GetAttribute

		public XElement Element => element;
	} // class XConfigNode
}
