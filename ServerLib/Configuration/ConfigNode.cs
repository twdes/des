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
using System.Dynamic;
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
	public sealed class XConfigNode : DynamicObject, IPropertyEnumerableDictionary
	{
		#region -- class XConfigNodes -------------------------------------------------

		private sealed class XConfigNodes : DynamicObject, IReadOnlyList<XConfigNode>
		{
			private readonly XConfigNode[] elements;
			private readonly IDEConfigurationAttribute primaryKey;
			private readonly IDEConfigurationElement configurationElement;

			public XConfigNodes(XElement parentElement, IDEConfigurationElement configurationElement)
			{
				if (parentElement == null)
					throw new ArgumentNullException(nameof(parentElement));

				this.configurationElement = configurationElement ?? throw new ArgumentNullException(nameof(configurationElement));
				elements = parentElement.Elements(configurationElement.Name).Select(x => new XConfigNode(configurationElement, x)).ToArray();
				primaryKey = configurationElement.GetAttributes().FirstOrDefault(a => a.IsPrimaryKey);
			} // ctor

			public IEnumerator<XConfigNode> GetEnumerator()
				=> elements.Cast<XConfigNode>().GetEnumerator();

			IEnumerator IEnumerable.GetEnumerator()
				=> elements.GetEnumerator();

			private string GetPrimaryKeyValue(XConfigNode c)
			{
				var value = GetConfigurationValue(primaryKey, c.GetAttributeValueCore(primaryKey));
				return value?.ChangeType<string>();
			}  // func GetPrimaryKeyValue

			private IEnumerable<string> GetCurrentMembers()
			{
				foreach (var c in elements)
				{
					var value = GetPrimaryKeyValue(c);
					if (value != null)
						yield return value;
				}
			} // func  GetCurrentMembers

			public override IEnumerable<string> GetDynamicMemberNames()
				=> primaryKey == null ? Array.Empty<string>() : GetCurrentMembers();

			private XConfigNode GetMember(string memberName)
			{
				return elements.FirstOrDefault(x => String.Compare(memberName, GetPrimaryKeyValue(x), StringComparison.OrdinalIgnoreCase) == 0)
					?? new XConfigNode(configurationElement, null);
			} // func TryFindMember

			public override bool TryGetMember(GetMemberBinder binder, out object result)
			{
				if (primaryKey == null)
					return base.TryGetMember(binder, out result);
				else
				{
					result = GetMember(binder.Name);
					return true;
				}
			} // func TryGetMember

			public int Count => elements.Length;
			public XConfigNode this[int index] => index >= 0 && index < elements.Length ? elements[index] : null;
			public XConfigNode this[string member] => primaryKey != null ? GetMember(member) : null;
		} // class XConfigNodes

		#endregion

		private readonly IDEConfigurationElement configurationElement;
		private readonly Lazy<Dictionary<string, IDEConfigurationAnnotated>> getElements;
		private readonly XElement element;

		private XConfigNode(IDEConfigurationElement configurationElement, XElement element)
		{
			this.configurationElement = configurationElement ?? throw new ArgumentNullException(nameof(configurationElement));

			getElements = new Lazy<Dictionary<string, IDEConfigurationAnnotated>>(() =>
				{
					var r = new Dictionary<string, IDEConfigurationAnnotated>(StringComparer.OrdinalIgnoreCase);

					// value of the element
					if (configurationElement.Value != null)
						r[String.Empty] = null;

					// sub attributes
					foreach (var attr in configurationElement.GetAttributes())
						r[attr.Name.LocalName] = attr;

					// sub elements
					foreach (var el in configurationElement.GetElements())
						r[el.Name.LocalName] = el;
					return r;
				}
			);

			this.element = element;
		} // ctor

		private static FileSystemInfo GetFileSystemInfo(string value)
		{
			if (Directory.Exists(value))
				return new DirectoryInfo(value);
			else if (File.Exists(value))
				return new FileInfo(value);
			else if (Path.GetFileName(value).IndexOf('.') >= 0)
				return new FileInfo(value);
			else
				return new DirectoryInfo(value);
		} // func GetFileSystemInfo

		private static object GetConfigurationValueSingle(IDEConfigurationValue attr, string value)
		{
			var type = attr.Type;
			if (type == typeof(LuaType))
			{
				if (value == null)
					value = attr.DefaultValue ?? "object";
				return LuaType.GetType(value, false, false).Type;
			}
			else if (type == typeof(Encoding))
			{
				if (String.IsNullOrEmpty(value))
					value = attr.DefaultValue;

				if (String.IsNullOrEmpty(value))
					return Encoding.Default;
				else if (Int32.TryParse(value, out var codePage))
					return Encoding.GetEncoding(codePage);
				else
					return Encoding.GetEncoding(value);
			}
			else if (type == typeof(CultureInfo))
			{
				return String.IsNullOrEmpty(value)
					? CultureInfo.GetCultureInfo(attr.DefaultValue)
					: CultureInfo.GetCultureInfo(value);
			}
			else if (type == typeof(FileSystemInfo))
			{
				return String.IsNullOrEmpty(value)
					? null
					: GetFileSystemInfo(value);
			}
			else if (type == typeof(SecureString))
			{
				try
				{
					return Passwords.DecodePassword(value);
				}
				catch
				{
					return null;
				}
			}
			else if (type == typeof(FileSize))
			{
				return FileSize.TryParse(value ?? attr.DefaultValue, out var fileSize)
					? fileSize
					: FileSize.Empty;
			}
			else
			{
				try
				{
					return Procs.ChangeType(value ?? attr.DefaultValue, type);
				}
				catch
				{
					return Procs.ChangeType(attr.DefaultValue, type);
				}
			}
		} // func GetConfigurationValue

		internal static object GetConfigurationValue(IDEConfigurationValue attr, string value)
		{
			var type = attr.Type;
			if (attr.IsList)
				return Procs.GetStrings(value).Select(v => GetConfigurationValueSingle(attr, v)).ToArray();
			else
				return GetConfigurationValueSingle(attr, value);
		} // func GetAttributeValue

		internal string GetAttributeValueCore(IDEConfigurationAttribute attr)
		{
			var value = attr.IsElement
				? element?.Element(attr.Name)?.Value
				: element?.Attribute(attr.Name)?.Value;

			return value;
		} // func GetAttributeValueCore

		private PropertyValue GetPropertyValue(IDEConfigurationAnnotated item)
		{
			switch (item)
			{
				case null:
					return new PropertyValue("(Default)", configurationElement.Value.Type, GetConfigurationValue(configurationElement.Value, element?.Value));
				case IDEConfigurationAttribute attr:
					return new PropertyValue(attr.Name.LocalName, attr.IsList ? attr.Type.MakeArrayType() : attr.Type, GetConfigurationValue(attr, GetAttributeValueCore(attr)));
				case IDEConfigurationElement el:
					if (el.MinOccurs == 1 && el.MaxOccurs == 1)
						return new PropertyValue(el.Name.LocalName, typeof(XConfigNode), new XConfigNode(el, element?.Element(el.Name)));
					else
						return new PropertyValue(el.Name.LocalName, typeof(IEnumerable<XConfigNode>), element != null ? new XConfigNodes(element, el) : null);
				default:
					return null;
			}
		} // func GetPropertyValue

		/// <summary>Get a attribute value or default value.</summary>
		/// <param name="name">Name of the attribute.</param>
		/// <param name="value">Value of the property.</param>
		/// <returns><c>true</c>, if the property is defined and has a value.</returns>
		public bool TryGetProperty(string name, out object value)
		{
			if (name != null && getElements.Value.TryGetValue(name, out var item))
			{
				var prop = GetPropertyValue(item);
				if (prop != null)
				{
					value = prop.Value;
					return true;
				}
				else
				{
					value = null;
					return false;
				}
			}
			else
			{
				value = null;
				return false;
			}
		} // func TryGetProperty

		private IDEConfigurationElement GetElementDefinition(XName name, bool throwException)
		{
			var elementConfiguration = getElements.Value.Values.OfType<IDEConfigurationElement>().FirstOrDefault(cur => cur.Name == name);
			if (elementConfiguration == null && throwException)
				throw new ArgumentOutOfRangeException($"'{name}' is not defined on {Name}.");
			return elementConfiguration;
		} // func GetElementDefinition

		/// <summary>Returns all attributes as properties.</summary>
		/// <returns></returns>
		public IEnumerator<PropertyValue> GetEnumerator()
		{
			foreach (var attr in getElements.Value)
				yield return GetPropertyValue(attr.Value);
		} // func GetEnumerator

		IEnumerator IEnumerable.GetEnumerator()
			=> GetEnumerator();

		/// <summary>Return a specific element or the default representation.</summary>
		/// <param name="name"></param>
		/// <param name="throwException"></param>
		/// <returns></returns>
		public XConfigNode Element(XName name, bool throwException = true)
		{
			var elementConfiguration = GetElementDefinition(name, throwException);
			if (elementConfiguration == null)
			{
				if (throwException)
					throw new ArgumentOutOfRangeException($"'{name}' is not defined on {Name}.");
				else
					return null;
			}

			return new XConfigNode(elementConfiguration, element?.Element(name));
		} // func Element

		/// <summary>Returns all elements of the current node with a specific name or names.</summary>
		/// <param name="names">List of names to select.</param>
		/// <returns></returns>
		public IEnumerable<XConfigNode> Elements(params XName[] names)
		{
			if (element != null)
			{
				var comparer = (from n in names
								let ce = GetElementDefinition(n, true)
								where ce != null
								select ce).ToArray();

				if (comparer.Length == 0)
				{
					if (element != null)
					{
						foreach (var attr in getElements.Value.Values.OfType<IDEConfigurationElement>())
							yield return new XConfigNode(attr, element.Element(attr.Name));
					}
				}
				else if (comparer.Length == 1)
				{
					var name = comparer[0].Name;
					var configurationElement = comparer[0];
					if (configurationElement != null)
					{
						foreach (var x in element.Elements(name))
							yield return new XConfigNode(configurationElement, x);
					}
				}
				else
				{
					var idx = -1;
					foreach (var x in element.Elements())
					{
						idx = idx >= 0 && comparer[idx].Name == x.Name
							? idx
							: Array.FindIndex(comparer, c => c.Name == x.Name);

						if (idx >= 0)
							yield return new XConfigNode(comparer[idx], x);
					}
				}
			}
		} // func Elements

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
		/// <param name="binder"></param>
		/// <param name="result"></param>
		/// <returns></returns>
		public override bool TryGetMember(GetMemberBinder binder, out object result)
			=> TryGetProperty(binder.Name, out result) || base.TryGetMember(binder, out result);

		/// <summary>Return keys.</summary>
		/// <returns></returns>
		public override IEnumerable<string> GetDynamicMemberNames()
			=> getElements.Value.Keys;

		/// <summary>Contains this value only default values.</summary>
		public bool IsDefault => element == null;
		/// <summary>Elementname of this configuration node.</summary>
		public XName Name => element?.Name ?? configurationElement.Name;
		/// <summary>Raw content of this configuration node.</summary>
		public XElement Data => element;
		/// <summary>Value of the configuration element.</summary>
		public object Value => configurationElement.Value != null ? GetConfigurationValue(configurationElement.Value, element?.Value) : null;
		/// <summary>Description of this configuration element.</summary>
		public IDEConfigurationElement ConfigurationElement => configurationElement;

		/// <summary>Return member by name</summary>
		/// <param name="memberName"></param>
		/// <returns></returns>
		public object this[string memberName] => TryGetProperty(memberName, out var value) ? value : null;

		// -- Static ----------------------------------------------------------

		private static IDEConfigurationElement GetConfigurationElement(IDEConfigurationService configurationService, XName name)
		{
			if (configurationService == null)
				throw new ArgumentNullException(nameof(configurationService));

			var configurationElement = configurationService[name ?? throw new ArgumentNullException(nameof(name))];
			return configurationElement ?? throw new ArgumentNullException($"Configuration definition not found for element '{name}'.");
		} // proc CheckConfigurationElement

		/// <summary>Create XConfigNode reader.</summary>
		/// <param name="configurationElement">Configuration element</param>
		/// <param name="element">Xml element</param>
		/// <returns></returns>
		public static XConfigNode Create(IDEConfigurationElement configurationElement, XElement element)
		{
			if (configurationElement == null)
				throw new ArgumentNullException(nameof(configurationElement), $"Configuration definition not found for element '{element?.Name ?? "<null>"}'.");
			if (element != null && !configurationElement.IsName(element.Name))
				throw new ArgumentOutOfRangeException(nameof(element), $"Element '{configurationElement.Name}' does not match with '{element.Name}'.");

			return new XConfigNode(configurationElement, element);
		} // func Create

		/// <summary>Create a configuration node for this element.</summary>
		/// <param name="configurationService">Configuration service</param>
		/// <param name="element">Xml element</param>
		/// <returns></returns>
		public static XConfigNode Create(IDEConfigurationService configurationService, XElement element)
		{
			if (element == null)
				throw new ArgumentNullException(nameof(element));

			return Create(GetConfigurationElement(configurationService, element.Name), element);
		} // func Create
	} // class XConfigNode
}
