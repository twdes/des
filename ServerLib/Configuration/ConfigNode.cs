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
using System.Collections.Generic;
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
		private readonly IDEConfigurationElement configurationElement;
		private readonly IDEConfigurationAttribute[] attributes;
		private readonly XElement element;

		private XConfigNode(IDEConfigurationElement configurationElement, XElement element)
		{
			this.configurationElement = configurationElement;
			this.attributes = configurationElement.GetAttributes().ToArray();
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

			var attributeValue = element?.Attribute(attribute.Name)?.Value ?? attribute.DefaultValue;

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
			else if (attribute.TypeName == "FileSize")
			{
				return FileSize.TryParse(attributeValue, out var fileSize)
					? fileSize
					: FileSize.Empty;
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
		public XName Name => element?.Name ?? configurationElement.Name;
		/// <summary></summary>
		public XElement Element => element;
		/// <summary></summary>
		public IDEConfigurationElement ConfigurationElement => configurationElement;

		// -- Static ----------------------------------------------------------

		private static IDEConfigurationElement GetConfigurationElement(IDEConfigurationService configurationService, XName name)
		{
			if (configurationService == null)
				throw new ArgumentNullException(nameof(configurationService));

			var configurationElement = configurationService[name ?? throw new ArgumentNullException(nameof(name))];
			return configurationElement ?? throw new ArgumentNullException($"Configuration definition not found for element '{name}'.");
		} // proc CheckConfigurationElement

		/// <summary>Create XConfigNode reader.</summary>
		/// <param name="configurationElement"></param>
		/// <param name="element"></param>
		/// <returns></returns>
		public static XConfigNode Create(IDEConfigurationElement configurationElement, XElement element)
		{
			if (configurationElement == null)
				throw new ArgumentNullException(nameof(configurationElement), $"Configuration definition not found for element '{element?.Name ?? "<null>"}'.");
			if (element != null && configurationElement.IsName(element.Name))
				throw new ArgumentOutOfRangeException(nameof(element), $"Element '{configurationElement.Name}' does not match with '{element.Name}'.");

			return new XConfigNode(configurationElement, element);
		} // func Create

		/// <summary></summary>
		/// <param name="configurationService"></param>
		/// <param name="element"></param>
		/// <returns></returns>
		public static XConfigNode Create(IDEConfigurationService configurationService, XElement element)
		{
			if (element == null)
				throw new ArgumentNullException(nameof(element));

			return Create(GetConfigurationElement(configurationService, element.Name), element);
		} // func Create

		/// <summary></summary>
		/// <param name="configurationService"></param>
		/// <param name="baseElement"></param>
		/// <param name="name"></param>
		/// <returns></returns>
		public static XConfigNode GetElement(IDEConfigurationService configurationService, XElement baseElement, XName name)
			=> new XConfigNode(
				GetConfigurationElement(configurationService, name),
				baseElement?.Element(name)
			);

		/// <summary></summary>
		/// <param name="configurationService"></param>
		/// <param name="baseElement"></param>
		/// <returns></returns>
		public static IEnumerable<XConfigNode> GetElements(IDEConfigurationService configurationService, XElement baseElement)
		{
			IDEConfigurationElement lastConfigurationElement = null;
			foreach (var cur in baseElement.Elements())
			{
				if (lastConfigurationElement == null
					|| !lastConfigurationElement.IsName(cur.Name))
				{
					var tmp = configurationService[cur.Name];
					if (tmp == null)
						break;
					lastConfigurationElement = tmp;
				}

				yield return new XConfigNode(lastConfigurationElement, cur);
			}
		} // func GetElements

		/// <summary></summary>
		/// <param name="configurationService"></param>
		/// <param name="baseElement"></param>
		/// <param name="name"></param>
		/// <returns></returns>
		public static IEnumerable<XConfigNode> GetElements(IDEConfigurationService configurationService, XElement baseElement, XName name)
		{
			var configurationElement = GetConfigurationElement(configurationService, name);
			if (baseElement != null)
			{
				foreach (var cur in baseElement.Elements(name))
					yield return new XConfigNode(configurationElement, cur);
			}
		} // func GetElements
	} // class XConfigNode
}
