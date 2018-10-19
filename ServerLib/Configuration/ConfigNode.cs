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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Configuration
{
	/// <summary>Configuration node, that read attributes and elements with support from the schema.</summary>
	public sealed class XConfigNode : IPropertyEnumerableDictionary
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
		
		/// <summary>Get a attribute value or default value.</summary>
		/// <param name="name">Name of the attribute.</param>
		/// <param name="value">Value of the property.</param>
		/// <returns><c>true</c>, if the property is defined and has a value.</returns>
		public bool TryGetProperty(string name, out object value)
		{
			var attribute = attributes.FirstOrDefault(c => String.Compare(c.Name.LocalName, name, StringComparison.OrdinalIgnoreCase) == 0);
			if (attribute == null)
			{
				value = null;
				return false;
			}

			var attributeValue = element?.Attribute(attribute.Name)?.Value ?? attribute.DefaultValue;

			var type = attribute.Type;
			if (type == typeof(LuaType))
			{
				if (attributeValue == null)
					attributeValue = "object";
				value = LuaType.GetType(attributeValue, false, false).Type;
				return true;
			}
			else if (type == typeof(Encoding))
			{
				if (String.IsNullOrEmpty(attributeValue))
					attributeValue = attribute.DefaultValue;

				if (String.IsNullOrEmpty(attributeValue))
					value = Encoding.Default;
				else if (Int32.TryParse(attributeValue, out var codePage))
					value = Encoding.GetEncoding(codePage);
				else
					value = Encoding.GetEncoding(attributeValue);

				return true;
			}
			else if (type == typeof(CultureInfo))
			{
				value = String.IsNullOrEmpty(attributeValue) ?
					CultureInfo.GetCultureInfo(attribute.DefaultValue) :
					CultureInfo.GetCultureInfo(attributeValue);
				return true;
			}
			else if (type == typeof(DirectoryInfo))
			{
				value = String.IsNullOrEmpty(attributeValue)
						? null
						: new DirectoryInfo(attributeValue);
				return true;
			}
			else if (type == typeof(SecureString))
			{
				try
				{
					value = Passwords.DecodePassword(attributeValue);
				}
				catch
				{
					value = null;
					return false;
				}
				return true;
			}
			else if (type == typeof(FileSize))
			{
				value = FileSize.TryParse(attributeValue, out var fileSize)
					? fileSize
					: FileSize.Empty;
				return true;
			}
			else
			{
				try
				{
					value = Procs.ChangeType(attributeValue, type);
				}
				catch
				{
					value = Procs.ChangeType(attribute.DefaultValue, type);
				}
				return true;
			}
		} // func TryGetProperty

		/// <summary>Returns all attributes as properties.</summary>
		/// <returns></returns>
		public IEnumerator<PropertyValue> GetEnumerator()
		{
			foreach (var attr in attributes)
			{
				if (TryGetProperty(attr.Name.LocalName, out var value))
					yield return new PropertyValue(attr.Name.LocalName, attr.Type, value);
			}
		} // func GetEnumerator

		IEnumerator IEnumerable.GetEnumerator()
			=> GetEnumerator();

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
			=> TryGetProperty(name, out var value)
				? value
				: throw new ArgumentException(String.Format("@{0} is not defined.", name));

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
			if (element != null && !configurationElement.IsName(element.Name))
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
