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
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Neo.IronLua;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationHelper;

namespace TecWare.DE.Server.Configuration
{
	#region -- class DEConfigurationHelper --------------------------------------------

	internal static class DEConfigurationHelper
	{
		#region -- GetTypeFromXmlType -------------------------------------------------

		public static Type GetTypeFromXmlType(string typeName, XmlTypeCode typeCode)
		{
			switch (typeName)
			{
				case "LuaType":
					return typeof(LuaType);
				case "EncodingType":
					return typeof(Encoding);
				case "language":
					return typeof(CultureInfo);
				case "PathType":
					return typeof(DirectoryInfo);
				case "FileSize":
					return typeof(FileSize);
				case "PasswordType":
					return typeof(SecureString);
				default:
					switch (typeCode)
					{
						case XmlTypeCode.Boolean:
							return typeof(bool);

						case XmlTypeCode.Byte:
							return typeof(sbyte);
						case XmlTypeCode.UnsignedByte:
							return typeof(byte);
						case XmlTypeCode.Short:
							return typeof(short);
						case XmlTypeCode.UnsignedShort:
							return typeof(ushort);
						case XmlTypeCode.Int:
						case XmlTypeCode.Integer:
						case XmlTypeCode.NegativeInteger:
						case XmlTypeCode.NonNegativeInteger:
						case XmlTypeCode.NonPositiveInteger:
							return typeof(int);
						case XmlTypeCode.UnsignedInt:
							return typeof(uint);
						case XmlTypeCode.Long:
							return typeof(long);
						case XmlTypeCode.UnsignedLong:
							return typeof(ulong);

						case XmlTypeCode.String:
						case XmlTypeCode.NormalizedString:
						case XmlTypeCode.Text:
						case XmlTypeCode.Name:
							return typeof(string);

						case XmlTypeCode.Decimal:
							return typeof(decimal);
						case XmlTypeCode.Float:
							return typeof(float);
						case XmlTypeCode.Double:
							return typeof(double);

						case XmlTypeCode.Date:
							return typeof(DateTime);
						case XmlTypeCode.DateTime:
							return typeof(DateTime);

						default:
							return typeof(object);
					}
			}
		} // func GetTypeFromXmlType

		#endregion

		public static bool GetListTypeVariation(XmlSchemaType xmlType)
			=> xmlType is XmlSchemaSimpleType simpleType
				? simpleType.Content is XmlSchemaSimpleTypeList
				: false;

		public static string GetXmlText(XmlNode[] elements)
		{
			var sb = new StringBuilder();
			foreach (var n in elements)
			{
				if (n is XmlText t)
					sb.Append(t.Value);
				else if (n is XmlCDataSection cd)
					sb.Append(cd.Value);
				else
					sb.Append(n.InnerText);
			}
			return sb.ToString();
		} // func GetXmlText

		public static XmlSchemaObjectCollection GetSubSequences(XmlSchemaComplexType complexType)
		{
			if (complexType == null)
				return null;

			switch (complexType.Particle ?? complexType.ContentTypeParticle)
			{
				case XmlSchemaSequence seq:
					return seq.Items;
				case XmlSchemaChoice choice:
					return choice.Items;
				case XmlSchemaAll all:
					return all.Items;
				default:
					return null;
			}
		} // func GetSubSequences

		public static XName GetXName(XmlQualifiedName xmlName)
			=> xmlName == null || String.IsNullOrEmpty(xmlName.Name)
				? null
				: XName.Get(xmlName.Name, xmlName.Namespace);

		internal static string FindClassTypeString(XmlSchemaElement element, out XmlSchemaAppInfo appInfo)
		{
			appInfo = element.Annotation?.Items.OfType<XmlSchemaAppInfo>().FirstOrDefault();
			if (appInfo != null)
				return appInfo.Markup.OfType<XmlElement>().Where(x => x.LocalName == "class").FirstOrDefault()?.InnerText;
			return null;
		} // func FindClassTypeString

		internal static Type FindClassType(XmlSchemaElement element, IServiceProvider sp)
		{
			var typeString = FindClassTypeString(element, out var appInfo);
			if (!String.IsNullOrEmpty(typeString))
			{
				try
				{
					if (typeString.IndexOf(',') == -1) // this is relative type
					{
						var sourceUri = DEConfigItem.GetSourceUri(appInfo);
						var posType = sourceUri.LastIndexOf(',');
						if (posType != -1)
							typeString = typeString + ", " + sourceUri.Substring(0, posType);
					}

					return Type.GetType(typeString, true, false);
				}
				catch (Exception e)
				{
					sp.LogProxy().Warn(new DEConfigurationException(appInfo, "Could not resolve type.", e)); // todo: exception mit position im schema
				}
			}
			return null;
		} // func FindClassType

		internal static IEnumerable<string> FindTypeNames(XmlSchemaType type)
		{
			if (type == null)
				yield break;

			if (!(type.QualifiedName is null || String.IsNullOrEmpty(type.QualifiedName.Name)))
				yield return type.QualifiedName.Name;

			if (type is XmlSchemaComplexType ct)
			{
				foreach (var y in FindTypeNames(ct.BaseXmlSchemaType))
					yield return y;
			}
			else if (type is XmlSchemaSimpleType st)
			{
				if (st.Content is XmlSchemaSimpleTypeUnion stu)
				{
					foreach (var c in stu.BaseMemberTypes)
					{
						foreach (var y in FindTypeNames(c))
							yield return y;
					}
				}
			}
		} // func FindTypeNames
	} // class DEConfigurationHelper

	#endregion

	#region -- class DEConfigurationBase<T> -------------------------------------------

	internal class DEConfigurationBase<T> : IDEConfigurationAnnotated
		where T : XmlSchemaAnnotated
	{
		private readonly T item;
		private Lazy<string> getDocumentation;

		public DEConfigurationBase(T item)
		{
			this.item = item ?? throw new ArgumentNullException("item");

			getDocumentation = new Lazy<string>(() =>
				{
					var doc = item.Annotation?.Items.OfType<XmlSchemaDocumentation>().FirstOrDefault();
					if (doc == null)
					{
						if (item is XmlSchemaElement element && element.ElementSchemaType != null)
							doc = FindDocumentTag(element.ElementSchemaType);
					}
					return doc == null ? null : GetXmlText(doc.Markup);
				}
			);
		} // ctor

		private static XmlSchemaDocumentation FindDocumentTag(XmlSchemaType elementSchemaType)
		{
			return elementSchemaType == null ?
				null :
				elementSchemaType.Annotation?.Items.OfType<XmlSchemaDocumentation>().FirstOrDefault() ?? FindDocumentTag(elementSchemaType.BaseXmlSchemaType);
		} // func FindDocumentTag

		public string Documentation => getDocumentation.Value;
		public T Item => item;
	} // class DEConfigurationAttributeBase

	#endregion

	#region -- class DEConfigurationAttribute -----------------------------------------

	internal class DEConfigurationAttribute : DEConfigurationBase<XmlSchemaAttribute>, IDEConfigurationAttribute
	{
		private readonly Lazy<string> typeName;
		private readonly Lazy<Type> type;
		private readonly Lazy<bool> isPrimaryKey;

		internal DEConfigurationAttribute(XmlSchemaAttribute attribute)
			: base(attribute)
		{
			this.typeName = new Lazy<string>(() => FindTypeNames(Item.AttributeSchemaType).FirstOrDefault() ?? String.Empty);
			this.type = new Lazy<Type>(() => GetTypeFromXmlType(TypeName, Item.AttributeSchemaType.TypeCode));
			this.isPrimaryKey = new Lazy<bool>(() => FindTypeNames(Item.AttributeSchemaType).Contains("KeyType"));
		} // ctor

		public XName Name => GetXName(Item.QualifiedName);
		public string TypeName => typeName.Value;
		public Type Type => type.Value;

		public string DefaultValue => Item.DefaultValue;

		public bool IsElement => false;
		public bool IsList => GetListTypeVariation(Item.AttributeSchemaType);
		public bool IsPrimaryKey => isPrimaryKey.Value;

		public int MinOccurs => Item.Use == XmlSchemaUse.Required ? 1 : 0;
		public int MaxOccurs => 1;
	} // class DEConfigurationAttribute

	#endregion

	#region -- class DEConfigurationElementAttribute ----------------------------------

	/// <summary></summary>
	internal class DEConfigurationElementAttribute : DEConfigurationBase<XmlSchemaElement>, IDEConfigurationAttribute
	{
		private readonly Lazy<string> typeName;
		private readonly Lazy<Type> type;
		private readonly Lazy<bool> isPrimaryKey;

		internal DEConfigurationElementAttribute(XmlSchemaElement element)
			: base(element)
		{
			typeName = new Lazy<string>(() => FindTypeNames(Item.ElementSchemaType).FirstOrDefault() ?? String.Empty);
			type = new Lazy<Type>(() => GetTypeFromXmlType(TypeName, Item.ElementSchemaType.TypeCode));
			isPrimaryKey = new Lazy<bool>(() => FindTypeNames(Item.ElementSchemaType).Contains("KeyType"));
		} // ctor

		public XName Name => GetXName(Item.QualifiedName);
		public string TypeName => typeName.Value;
		public Type Type => type.Value;

		public string DefaultValue => Item.DefaultValue;

		public bool IsElement => true;
		public bool IsList => GetListTypeVariation(Item.ElementSchemaType);
		public bool IsPrimaryKey => isPrimaryKey.Value;

		public int MinOccurs => Item.MinOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MinOccurs);
		public int MaxOccurs => Item.MaxOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MaxOccurs);
	} // class DEConfigurationElementAttribute

	#endregion

	#region -- class DEConfigurationElementValue --------------------------------------

	/// <summary></summary>
	internal class DEConfigurationElementValue : IDEConfigurationValue
	{
		private readonly XmlSchemaSimpleType schemaType;
		private readonly Lazy<string> typeName;
		private readonly Lazy<Type> type;

		internal DEConfigurationElementValue(XmlSchemaSimpleType schemaType)
		{
			this.schemaType = schemaType ?? throw new ArgumentNullException(nameof(schemaType));

			typeName = new Lazy<string>(() => FindTypeNames(schemaType).FirstOrDefault() ?? String.Empty);
			type = new Lazy<Type>(() => GetTypeFromXmlType(TypeName, schemaType.TypeCode));
		} // ctor

		public bool IsList => GetListTypeVariation(schemaType);
		public string TypeName => typeName.Value;
		public Type Type => type.Value;
		public string DefaultValue => null;
	} // class DEConfigurationElementAttribute

	#endregion

	#region -- class DEMixedConfigurationValue ----------------------------------------

	internal class DEMixedConfigurationValue : IDEConfigurationValue
	{
		private DEMixedConfigurationValue() { }

		public string TypeName => "string";
		public Type Type => typeof(string);
		public string DefaultValue => null;
		public bool IsList => false;

		public static IDEConfigurationValue Default { get; } = new DEMixedConfigurationValue();
	} // class DEMixedConfigurationValue

	#endregion

	#region -- class DEConfigurationElement -------------------------------------------

	/// <summary></summary>
	internal class DEConfigurationElement : DEConfigurationBase<XmlSchemaElement>, IDEConfigurationElement
	{
		private readonly IServiceProvider sp;
		private readonly Lazy<string> typeName;
		private readonly Lazy<Type> getClassType;
		private readonly Lazy<bool> getBrowsable;
		private readonly Lazy<IDEConfigurationValue> getValue;

		public DEConfigurationElement(IServiceProvider sp, XmlSchemaElement element)
			: base(element)
		{
			this.sp = sp;

			typeName = new Lazy<string>(() => FindTypeNames(Item.ElementSchemaType).FirstOrDefault() ?? String.Empty);
			getClassType = new Lazy<Type>(() => FindClassType(Item, sp));
			getBrowsable = new Lazy<bool>(() =>
				{
					var appInfo = Item.Annotation?.Items.OfType<XmlSchemaAppInfo>().FirstOrDefault();
					if (appInfo != null)
					{
						var browsableElement = appInfo.Markup.OfType<XmlElement>().Where(x => x.LocalName == "browsable").FirstOrDefault();
						return browsableElement == null || browsableElement.InnerText == null || browsableElement.InnerText.ChangeType<bool>();
					}
					else
						return true;
				}
			);
			getValue = new Lazy<IDEConfigurationValue>(() =>
			{
				if (Item.ElementSchemaType.IsMixed)
					return DEMixedConfigurationValue.Default;
				else if (Item.ElementSchemaType.BaseXmlSchemaType is XmlSchemaSimpleType simpleType)
					return new DEConfigurationElementValue(simpleType);
				else
					return null;
			});
		} // ctor

		private IEnumerable<IDEConfigurationElement> GetElements(XmlSchemaObjectCollection items)
		{
			if (items == null)
				yield break;

			foreach (var x in items)
			{
				switch (x)
				{
					case XmlSchemaElement element:
						if (!(element.ElementSchemaType is XmlSchemaSimpleType)
							&& FindClassTypeString(element, out var appinfo) == null)
						{
							if (element.RefName != null && element.Name == null) // resolve reference
							{
								var el = sp.GetService<DEConfigurationService>(typeof(IDEConfigurationService), true)[GetXName(element.QualifiedName)];
								if (el.ClassType == null)
									yield return el;
							}
							else
								yield return new DEConfigurationElement(sp, element);
						}
						break;
					case XmlSchemaSequence seq:
						foreach (var c in GetElements(seq.Items))
							yield return c;
						break;
					case XmlSchemaChoice choice:
						foreach (var c in GetElements(choice.Items))
							yield return c;
						break;
					case XmlSchemaAll all:
						foreach (var c in GetElements(all.Items))
							yield return c;
						break;
				}
			}
		} // func GetElements

		public IEnumerable<IDEConfigurationElement> GetElements()
			=> Item.ElementSchemaType is XmlSchemaComplexType complexType
				? GetElements(GetSubSequences(complexType))
				: Array.Empty<IDEConfigurationElement>();

		public bool IsName(XName other)
			=> other == Name
				|| other == GetXName(Item.SubstitutionGroup);

		private bool IsSimpleTextContent(XmlSchemaType type)
		{
			if (type is XmlSchemaSimpleType)
				return true;
			else if (type is XmlSchemaComplexType ct && ct.AttributeUses.Count == 0)
				return ct.ContentType == XmlSchemaContentType.TextOnly;
			else
				return false;
		} // func IsSimpleTextContent

		public IEnumerable<IDEConfigurationAttribute> GetAttributes()
		{
			if (Item.ElementSchemaType is XmlSchemaComplexType complexType)
			{
				foreach (var attr in complexType.AttributeUses.Values.OfType<XmlSchemaAttribute>())
					yield return new DEConfigurationAttribute(attr);

				var items = GetSubSequences(complexType);
				if (items != null)
				{
					foreach (var x in items.OfType<XmlSchemaElement>().Where(c => IsSimpleTextContent(c.ElementSchemaType)))
						yield return new DEConfigurationElementAttribute(x);
				}
			}
		} // func GetAttributes

		public XName Name => GetXName(Item.QualifiedName);
		public bool IsBrowsable => getBrowsable.Value;

		public string TypeName => typeName.Value;
		public Type ClassType => getClassType.Value;

		public IDEConfigurationValue Value => getValue.Value;

		public int MinOccurs => Item.MinOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MinOccurs);
		public int MaxOccurs => Item.MaxOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MaxOccurs);
	} // class DEConfigurationElement

	#endregion
}