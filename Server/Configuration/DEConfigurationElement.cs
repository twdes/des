using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationHelper;

namespace TecWare.DE.Server.Configuration
{
	#region -- class DEConfigurationHelper ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal static class DEConfigurationHelper
	{
		#region -- GetTypeFromXmlTypeCode -------------------------------------------------

		public static Type GetTypeFromXmlTypeCode(XmlTypeCode typeCode)
		{
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
		} // func GetTypeFromXmlTypecode

		#endregion

		public static bool GetListTypeVariation(XmlSchemaType xmlType)
		{
			var simpleType = xmlType as XmlSchemaSimpleType;
			if (simpleType == null)
				return false;

			return simpleType.Content is XmlSchemaSimpleTypeList;
		} // func GetTypeHierarchy

		public static string GetXmlText(XmlNode[] elements)
		{
			var sb = new StringBuilder();
			foreach (var n in elements)
			{
				if (n is XmlText)
					sb.Append(((XmlText)n).Value);
				else if (n is XmlCDataSection)
					sb.Append(((XmlCDataSection)n).Value);
				else
					sb.Append(n.InnerText);
			}
			return sb.ToString();
		} // func GetXmlText

		public static XmlSchemaObjectCollection GetSubSequences(XmlSchemaComplexType complexType)
		{
			var items = (XmlSchemaObjectCollection)null;
			if (complexType != null)
			{
				var particle = complexType.Particle ?? complexType.ContentTypeParticle;
				if (particle != null)
				{
					var seq = particle as XmlSchemaSequence;
					if (seq != null)
						items = seq.Items;
					else
					{
						var choice = particle as XmlSchemaChoice;
						if (choice != null)
							items = choice.Items;
						else
						{
							var all = particle as XmlSchemaAll;
							if (all != null)
								items = all.Items;
						}
					}
				}
			}
			return items;
		} // func GetSubSequences

		public static XName GetXName(XmlQualifiedName xmlName) => XName.Get(xmlName.Name, xmlName.Namespace);
  } // class DEConfigurationHelper

	#endregion	

	#region -- class DEConfigurationBase<T> ---------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEConfigurationBase<T> : IDEConfigurationAnnotated
		where T : XmlSchemaAnnotated
	{
		private T item;
		private Lazy<string> getDocumentation;

		public DEConfigurationBase(T item)
		{
			if (item == null)
				throw new ArgumentNullException("item");

			this.item = item;
			this.getDocumentation = new Lazy<string>(() =>
				{
					var doc = item.Annotation?.Items.OfType<XmlSchemaDocumentation>().FirstOrDefault();
					if (doc == null)
					{
						var element = item as XmlSchemaElement;
						if (element != null && element.ElementSchemaType != null)
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

	#region -- class DEConfigurationAttribute -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEConfigurationAttribute : DEConfigurationBase<XmlSchemaAttribute>, IDEConfigurationAttribute
	{
		internal DEConfigurationAttribute(XmlSchemaAttribute attribute)
			: base(attribute)
		{
    } // ctor

		public XName Name => GetXName(Item.QualifiedName);
		public string TypeName => Item.SchemaTypeName.Name;
		public Type Type => GetTypeFromXmlTypeCode(Item.AttributeSchemaType.TypeCode);

		public string DefaultValue => Item.DefaultValue;

		public bool IsElement => false;
		public bool IsList => GetListTypeVariation(Item.AttributeSchemaType);
		public bool IsPrimaryKey => TypeName == "KeyType";

		public int MinOccurs => Item.Use == XmlSchemaUse.Required ? 1 : 0;
		public int MaxOccurs => 1;
	} // class DEConfigurationAttribute

	#endregion

	#region -- class DEConfigurationElementAttribute ------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEConfigurationElementAttribute : DEConfigurationBase<XmlSchemaElement>, IDEConfigurationAttribute
	{
		internal DEConfigurationElementAttribute(XmlSchemaElement element)
			: base(element)
		{
		} // ctor

		public XName Name => GetXName(Item.QualifiedName);
		public string TypeName => Item.SchemaTypeName.Name;
		public Type Type => GetTypeFromXmlTypeCode(Item.ElementSchemaType.TypeCode);

		public string DefaultValue => Item.DefaultValue;

		public bool IsElement => true;
		public bool IsList => GetListTypeVariation(Item.ElementSchemaType);
		public bool IsPrimaryKey => TypeName == "KeyType";

		public int MinOccurs => Item.MinOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MinOccurs);
		public int MaxOccurs => Item.MaxOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MaxOccurs);
	} // class DEConfigurationElementAttribute

	#endregion

	#region -- class DEConfigurationElement ---------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEConfigurationElement : DEConfigurationBase<XmlSchemaElement>, IDEConfigurationElement
	{
		private IServiceProvider sp;
		private Lazy<Type> getClassType;

		public DEConfigurationElement(IServiceProvider sp, XmlSchemaElement element)
			: base(element)
		{
			this.sp = sp;
			
			getClassType = new Lazy<Type>(() =>
				{
					var classType = (Type)null;
					var appInfo = element.Annotation?.Items.OfType<XmlSchemaAppInfo>().FirstOrDefault();
					if (appInfo != null)
					{
						var classTypeElement = appInfo.Markup.OfType<XmlElement>().Where(x => x.LocalName == "class").FirstOrDefault();
						if (classTypeElement != null)
						{
							var typeString = classTypeElement.InnerText;
							try
							{
								if (typeString.IndexOf(',') == -1) // this is relative type
								{
									var sourceUri = DEConfigItem.GetSourceUri(appInfo);
									var posType = sourceUri.LastIndexOf(',');
									if (posType != -1)
										typeString = typeString + ", " + sourceUri.Substring(0, posType);
								}
							
								classType = Type.GetType(typeString, true, false);
							}
							catch (Exception e)
							{
								sp.LogProxy().Warn(new DEConfigurationException(appInfo, "Could not resolve type.", e)); // todo: exception mit position im schema
							}
						}
					}
					return classType;
				}
			);
		} // ctor

		public IEnumerable<IDEConfigurationElement> GetElements()
		{
			var complexType = Item.ElementSchemaType as XmlSchemaComplexType;
			if (complexType != null)
			{
				var items = GetSubSequences(complexType);
				if (items != null)
				{
					foreach (var x in items.OfType<XmlSchemaElement>().Where(c => !(c.ElementSchemaType is XmlSchemaSimpleType)))
					{
						if (x.RefName != null && x.Name == null) // resolve reference
							yield return sp.GetService<DEConfigurationService>(typeof(IDEConfigurationService), true)[GetXName(x.QualifiedName)];
						else
							yield return new DEConfigurationElement(sp, x);
					}
				}
			}
		} // func GetElements

		public IEnumerable<IDEConfigurationAttribute> GetAttributes()
		{
			var complexType = Item.ElementSchemaType as XmlSchemaComplexType;
			if (complexType != null)
			{
				foreach (XmlSchemaAttribute attr in complexType.AttributeUses.Values)
					yield return new DEConfigurationAttribute(attr);

				var items = GetSubSequences(complexType);
				if (items != null)
				{
					foreach (var x in items.OfType<XmlSchemaElement>().Where(c => c.ElementSchemaType is XmlSchemaSimpleType))
						yield return new DEConfigurationElementAttribute(x);
				}
			}
		} // func GetAttributes

		public XName Name => GetXName(Item.QualifiedName);
		public Type ClassType => getClassType.Value;

		public int MinOccurs => Item.MinOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MinOccurs);
		public int MaxOccurs => Item.MaxOccurs == Decimal.MaxValue ? Int32.MaxValue : Decimal.ToInt32(Item.MaxOccurs);
	} // class DEConfigurationElement

	#endregion
}