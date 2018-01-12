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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Configuration
{
	/// <summary>Configuration node, that read attributes and elements with support from the schema.</summary>
	public sealed class XConfigNode
	{
		private readonly IDEConfigurationAttribute[] attributes;
		private readonly XElement element;

		/// <summary></summary>
		/// <param name="configurationServer"></param>
		/// <param name="element"></param>
		public XConfigNode(IDEConfigurationService configurationServer, XElement element)
			: this(configurationServer[element.Name], element)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="elementDefinition"></param>
		/// <param name="element"></param>
		public XConfigNode(IDEConfigurationElement elementDefinition, XElement element)
		{
			if (elementDefinition == null)
				throw new ArgumentNullException(nameof(elementDefinition));
			if (element == null)
				throw new ArgumentNullException(nameof(element));
			if (!elementDefinition.IsName(element.Name))
				throw new ArgumentOutOfRangeException(nameof(element), $"Element '{elementDefinition.Name}' does not match with '{element.Name}'.");

			this.attributes = elementDefinition.GetAttributes().ToArray();
			this.element = element;
		} // ctor

		/// <summary></summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="name"></param>
		/// <returns></returns>
		public T GetAttribute<T>(string name)
			=> Procs.ChangeType<T>(GetAttribute(name));

		/// <summary></summary>
		/// <param name="name"></param>
		/// <returns></returns>
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
				if (String.IsNullOrEmpty(attributeValue))
					attributeValue = attribute.DefaultValue;

				if (String.IsNullOrEmpty(attributeValue))
					return Encoding.Default;
				else if (Int32.TryParse(attributeValue, out var codePage))
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
			else if (attribute.TypeName == "PathType")
			{
				if (String.IsNullOrEmpty(attributeValue))
					return null;
				return new DirectoryInfo(attributeValue);
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

		/// <summary></summary>
		public XElement Element => element;
	} // class XConfigNode
}
