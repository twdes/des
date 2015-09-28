using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Neo.IronLua;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server.Configuration
{
	#region -- class DEConfigurationService ---------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DEConfigurationService : IDEConfigurationService
	{
		#region -- struct SchemaAssemblyDefinition ----------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private struct SchemaAssemblyDefinition
		{
			public Assembly Assembly;
			public XmlSchema Schema;

			public string TargetNamespace => Schema.TargetNamespace;
			public string Name => Schema.Id + ".xsd";

			public string DisplayName => Schema.SourceUri;
		} // struct SchemaAssemblyDefinition

		#endregion

		private IServiceProvider sp;
		private IDEServerResolver resolver;

		private string configurationFile;
		private DateTime configurationStamp;
		private List<string> knownConfigurationFiles = new List<string>();

		private XmlNameTable nameTable; // name table
		private XmlSchemaSet schema; // complete schema
		private List<SchemaAssemblyDefinition> assemblySchemas = new List<SchemaAssemblyDefinition>(); // mapping schema to assembly

		private Dictionary<XName, IDEConfigurationElement> elementResolveCache = new Dictionary<XName, IDEConfigurationElement>();

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEConfigurationService(IServiceProvider sp, string configurationFile)
		{
			this.sp = sp;
			this.resolver = sp.GetService<IDEServerResolver>(false);

			this.configurationFile = configurationFile;
			this.configurationStamp = DateTime.MinValue;

			// create a empty schema
			nameTable = new NameTable();
			schema = new XmlSchemaSet(nameTable);
		} // ctor

		#endregion

		#region -- Parse Configuration ----------------------------------------------------

		#region -- class DEConfigurationStackException ------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class DEConfigurationStackException : DEConfigurationException
		{
			private string stackFrames;

			public DEConfigurationStackException(ParseFrame currentFrame, XObject x, string message, Exception innerException = null)
				: base(x, message, innerException)
			{
				var sbStack = new StringBuilder();
				var c = currentFrame;
				while (c != null)
				{
					c.AppendStackFrame(sbStack);
					c = c.Parent;
				}
				stackFrames = sbStack.ToString();
			} // ctor

			public string StackFrame => stackFrames;
		} // class DEConfigurationStackException

		#endregion

		#region -- class ParseFrame -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class ParseFrame : LuaTable
		{
			private LuaTable parentFrame;
			private XObject source;

			private bool deleteNodes;

			public ParseFrame(LuaTable parentFrame, XObject source)
			{
				this.parentFrame = parentFrame;
				this.source = source;
			} // ctor

			public void AppendStackFrame(StringBuilder sbStack)
			{
				sbStack.Append(source.BaseUri);
				var lineInfo = source as IXmlLineInfo;
				if (lineInfo != null && lineInfo.HasLineInfo())
				{
					sbStack.Append(" (");
					sbStack.Append(lineInfo.LineNumber.ToString());
					sbStack.Append(',');
					sbStack.Append(lineInfo.LinePosition.ToString());
					sbStack.Append(')');
				}
				sbStack.AppendLine();
			} // proc GetStackFrame

			protected override object OnIndex(object key) => base.OnIndex(key) ?? parentFrame?.GetValue(key);

			public ParseFrame Parent => parentFrame as ParseFrame;
			public bool IsDeleteNodes { get { return deleteNodes; } set { deleteNodes = value; } }
		} // class ParseFrame

		#endregion

		#region -- class ParseContext -----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class ParseContext : LuaPropertiesTable
		{
			private string basePath;
			private List<string> collectedFiles = new List<string>();

			private ParseFrame currentFrame = null;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public ParseContext(PropertyDictionary arguments, string basePath)
				: base(arguments)
			{
				this.basePath = basePath;
			} // ctor

			#endregion

			#region -- Frames ---------------------------------------------------------------

			public ParseFrame PushFrame(XNode source)
			{
				return currentFrame = new ParseFrame(currentFrame == null ? this : (LuaTable)currentFrame, source);
			} // proc ParseFrame

			public void PopFrame(ParseFrame frame)
			{
				if (currentFrame == null || currentFrame != frame)
					throw new InvalidOperationException("Invalid stack.");
				currentFrame = frame.Parent;
			} // proc PopFrame

			#endregion

			#region -- LoadFile -------------------------------------------------------------

			/// <summary></summary>
			/// <param name="source"></param>
			/// <param name="fileName"></param>
			/// <returns></returns>
			public XDocument LoadFile(XObject source, string fileName)
			{
				try
				{
					return LoadFile(ProcsDE.GetFileName(source, fileName));
				}
				catch (Exception e)
				{
					throw CreateConfigException(source, String.Format("Could not load reference '{0}'.", fileName), e);
				}
			} // func LoadFile

			/// <summary></summary>
			/// <param name="fileName"></param>
			/// <returns></returns>
			public XDocument LoadFile(string fileName)
			{
				if (!Path.IsPathRooted(fileName))
					fileName = Path.GetFullPath(Path.Combine(basePath, fileName));

				// collect all loaded files
				if (!collectedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
					collectedFiles.Add(fileName);

				return XDocument.Load(fileName, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
			} // proc LoadFile

			#endregion

			public Exception CreateConfigException(XObject x, string message, Exception innerException = null)
				=> new DEConfigurationStackException(currentFrame, x, message, innerException);

			public bool IsDefined(string expr)
			{
				return currentFrame.GetMemberValue(expr) != null;
			} // func IsDefined

			public ParseFrame CurrentFrame => currentFrame;
		} // class ParseContext

		#endregion

		/// <summary>Reads the configuration file and validates it agains the schema</summary>
		/// <returns></returns>
		public XElement ParseConfiguration(PropertyDictionary arguments)
		{
			// prepare arguments for the configuration
			var fileName = Path.GetFullPath(configurationFile);
			var context = new ParseContext(arguments, Path.GetDirectoryName(fileName));


			// read main file
			var doc = context.LoadFile(fileName);
			var frame = context.PushFrame(doc);

			try
			{
				if (doc.Root.Name != xnDes)
					throw new InvalidDataException(String.Format("Configuration root node is invalid (expected: {0}).", xnDes));
				if (doc.Root.GetAttribute("version", String.Empty) != "330")
					throw new InvalidDataException("Configuration version is invalid (expected: 330).");

				// parse the tree
				ParseConfiguration(context, doc);
				context.PopFrame(frame);

				return doc.Root;
			}
			catch (Exception e)
			{
				if (e is DEConfigurationStackException)
					throw;
				throw context.CreateConfigException(doc, "Could not parse configuration file.", e);
			}
		} // func ParseConfiguration

		private void ParseConfiguration(ParseContext context, XContainer x)
		{
			var c = x.FirstNode;
			while (c != null)
			{
				var deleteMe = (XNode)null;
				var value = (string)null;

				if (c is XComment)
					deleteMe = c;
				else if (c is XProcessingInstruction)
				{
					ParseConfigurationPI(context, (XProcessingInstruction)c);
					deleteMe = c;
				}
				else
				{
					if (context.CurrentFrame.IsDeleteNodes)
						deleteMe = c;
					else if (c is XElement)
					{
						var xCur = (XElement)c;

						// Replace values in attributes
						foreach (var attr in xCur.Attributes())
						{
							if (ChangeConfigurationValue(context, attr, attr.Value, out value))
								attr.Value = value;
						}

						// Parse the current element
						var newFrame = context.PushFrame(xCur);
						ParseConfiguration(context, xCur);
						context.PopFrame(newFrame);

						// Load assemblies -> they preprocessor needs them
						if (xCur.Name == xnServer)
						{
							foreach (var cur in xCur.Elements())
							{
								if (cur.Name == xnServerResolve) // resolve paths
									resolver?.AddPath(cur.Value);
								else if (cur.Name == xnServerLoad)
								{
									var assembly = resolver?.Load(cur.Value);
									if (assembly != null)
										UpdateSchema(assembly);
								}
							}
						}
					}
					else if (c is XText)
					{
						XText xText = (XText)c;
						if (ChangeConfigurationValue(context, xText, xText.Value, out value))
							xText.Value = value;
					}
				}

				// Nächster Knoten
				c = c.NextNode;

				// Lösche den Knoten, sonst würde Next nicht funktionieren
				if (deleteMe != null)
					deleteMe.Remove();
			}
		} // proc ParseConfiguration

		private void ParseConfigurationPI(ParseContext context, XProcessingInstruction xPI)
		{
			if (xPI.Target == "des-begin") // start a block
				context.CurrentFrame.IsDeleteNodes = !context.IsDefined(xPI.Data);
			else if (xPI.Target == "des-end")
				context.CurrentFrame.IsDeleteNodes = false;
			else if (!context.CurrentFrame.IsDeleteNodes)
			{
				if (xPI.Target.StartsWith("des-var-"))
				{
					context.CurrentFrame.SetMemberValue(xPI.Target.Substring(8), xPI.Data.Trim());
				}
				else if (xPI.Target == "des-include")
				{
					IncludeConfigTree(context, xPI);
				}
				else if (xPI.Target == "des-merge")
				{
					MergeConfigTree(context, xPI);
				}
			}
		} // proc ParseConfiguration

		private void IncludeConfigTree(ParseContext context, XProcessingInstruction xPI)
		{
			if (xPI.Parent == null)
				throw context.CreateConfigException(xPI, "It is not allowed to include to a root element.");

			var xInc = context.LoadFile(xPI, xPI.Data).Root;
			if (xInc.Name == DEConfigurationConstants.xnInclude)
			{
				XNode xLast = xPI;

				// Copy the baseuri annotation
				var copy = new List<XElement>();
				foreach (var xSrc in xInc.Elements())
				{
					Procs.XCopyAnnotations(xSrc, xSrc);
					copy.Add(xSrc);
				}

				// Remove all elements from the source, that not get internal copied.
				xInc.RemoveAll();
				xPI.AddAfterSelf(copy);
			}
			else
			{
				Procs.XCopyAnnotations(xInc, xInc);
				xInc.Remove();
				xPI.AddAfterSelf(xInc);
			}
		} // proc IncludeConfigTree

		private void MergeConfigTree(ParseContext context, XProcessingInstruction xPI)
		{
			var xDoc = context.LoadFile(xPI, xPI.Data);

			// parse the loaded document
			var newFrame = context.PushFrame(xPI);
			if (xDoc.Root.Name != DEConfigurationConstants.xnFragment)
				throw context.CreateConfigException(xDoc.Root, "<fragment> expected.");

			ParseConfiguration(context, xDoc.Root);
			context.PopFrame(newFrame);

			// merge the parsed nodes
			MergeConfigTree(xPI.Document.Root, xDoc.Root);
		} // proc MergeConfigTree

		private void MergeConfigTree(XElement xRoot, XElement xMerge)
		{
			// merge attributes
			var attributeMerge = xMerge.FirstAttribute;
			while (attributeMerge != null)
			{
				var attributeRoot = xRoot.Attribute(attributeMerge.Name);
				if (attributeRoot == null) // attribute does not exists --> insert
				{
					xRoot.SetAttributeValue(attributeMerge.Name, attributeMerge.Value);
				}
				else // attribute exists --> override or combine lists
				{
					var attributeDefinition = GetAttribute(attributeMerge);
					if (attributeDefinition != null)
					{
						if (attributeDefinition.IsList) // list detected
							attributeRoot.Value = attributeRoot.Value + " " + attributeMerge.Value;
						else
							attributeRoot.Value = attributeMerge.Value;
					}
				}

				attributeMerge = attributeMerge.NextAttribute;
			}

			// merge elements
			var xCurNodeMerge = xMerge.FirstNode;
			while (xCurNodeMerge != null)
			{
				var xCurMerge = xCurNodeMerge as XElement;
				var xNextNode = xCurNodeMerge.NextNode;

				if (xCurMerge != null)
				{
					var xCurRoot = FindConfigTreeElement(xRoot, xCurMerge);
					if (xCurRoot == null) // node is not present -> include
					{
						Procs.XCopyAnnotations(xCurMerge, xCurMerge);
						xCurMerge.Remove();
						xRoot.Add(xCurMerge);
					}
					else // merge node
						MergeConfigTree(xCurRoot, xCurMerge);
				}

				xCurNodeMerge = xNextNode;
			}
		} // proc MergeConfigTree

		private XElement FindConfigTreeElement(XElement xRootParent, XElement xSearch)
		{
			var elementDefinition = GetConfigurationElement(xSearch.Name);

			// find primary key columns
			var primaryKeys = (from c in elementDefinition.GetAttributes()
												 where c.IsPrimaryKey
												 select c).ToArray();

			foreach (var x in xRootParent.Elements(xSearch.Name))
			{
				var r = true;

				for (int i = 0; i < primaryKeys.Length; i++)
				{
					var attr1 = x.Attribute(primaryKeys[i].Name);
					var attr2 = xSearch.Attribute(primaryKeys[i].Name);

					if (attr1 != null ^ attr2 != null)
					{
						r = false;
						break;
					}
					else if (attr1.Value != attr2.Value)
					{
						r = false;
						break;
					}
				}

				if (r)
					return x;
			}

			return null;
		} // func FindConfigTreeElement

		private bool ChangeConfigurationValue(ParseContext context, XObject x, string currentValue, out string newValue)
		{
			var valueModified = false;

			// first check for macro substitionen
			newValue = currentValue.Trim(); // trim always the value
			if (newValue.StartsWith("$(", StringComparison.OrdinalIgnoreCase) && newValue.EndsWith(")"))
			{
				var propertyName = newValue.Substring(2, currentValue.Length - 3);
				newValue = Procs.ChangeType<string>(context.GetMemberValue(propertyName, true));

				valueModified |= true;
			}

			// first check for type converter
			var attributeDefinition = GetAttribute(x);
			if (attributeDefinition != null)
			{
				if (attributeDefinition.TypeName == "PathType")
				{
					newValue = ProcsDE.GetFileName(x, newValue);

					valueModified |= true;
				}
			}

			return valueModified;
		} // func ChangeConfigurationValue

		#endregion

		#region -- Update Schema ----------------------------------------------------------

		public void UpdateSchema(Assembly assembly)
		{
			var log = sp.LogProxy();
			foreach (var schemaAttribute in assembly.GetCustomAttributes<DEConfigurationSchemaAttribute>())
			{
				using (var src = assembly.GetManifestResourceStream(schemaAttribute.BaseType, schemaAttribute.ResourceId))
				{
					var schemaUri = assembly.FullName + ", " + (schemaAttribute.BaseType == null ? "" : schemaAttribute.BaseType.Name + ".") + schemaAttribute.ResourceId;
          try
					{
						{
							if (src == null)
								throw new Exception("Could not localize resource.");

							// Erzeuge die Schemadefinition
							var xmlSchema = XmlSchema.Read(src, (sender, e) => { throw e.Exception; });
							xmlSchema.SourceUri = schemaUri;

							// Aktualisiere die Schema-Assembly-Liste
							lock (schema)
							{
								var exists = assemblySchemas.FindIndex(c => c.Schema.Id == xmlSchema.Id);
								if (exists >= 0)
								{
									if (assemblySchemas[exists].Assembly == assembly)
										return;
									throw new ArgumentException(String.Format("Schema already loaded (existing: {0}).", assemblySchemas[exists].DisplayName));
								}

								// Add the schema
								assemblySchemas.Add(new SchemaAssemblyDefinition { Schema = xmlSchema, Assembly = assembly });
								schema.Add(xmlSchema);

								log.Info("Schema added ({0})", assemblySchemas[assemblySchemas.Count - 1].DisplayName);

								// recompile the schema
								schema.Compile();
							}
						}
					}
					catch (Exception e)
					{
						log.Except(String.Format("Schema not loaded ({0}).", schemaUri), e);
					}
				} // using
			} // foreach
		} // func UpdateSchema

		#endregion

		#region -- Schema description -----------------------------------------------------

		private IDEConfigurationElement GetConfigurationElement(XName name)
		{
			lock(schema)
			{
				// search cache
				IDEConfigurationElement r;
				if (elementResolveCache.TryGetValue(name, out r))
					return r;

				var xmlElement = FindConfigElement(new List<XmlSchemaElement>(), schema.GlobalElements.Values, name);
				if (xmlElement == null)
					xmlElement = FindConfigElement(new List<XmlSchemaElement>(), schema.GlobalTypes.Values, name);

				return elementResolveCache[name] = xmlElement == null ? null : new DEConfigurationElement(sp, xmlElement);
			}
		} // func FindConfigurationElement
		
		private XmlSchemaElement FindConfigElement(List<XmlSchemaElement> stack, System.Collections.ICollection items, XName name)
		{
			foreach (XmlSchemaObject c in items)
			{
				var t = (XmlSchemaComplexType)null;
				var e = c as XmlSchemaElement;

				// Ermittle den Typ, des Elementes
				if (e != null)
				{
					// Teste den Namen
					if (e.QualifiedName.Name == name.LocalName && e.QualifiedName.Namespace == name.NamespaceName)
						return e;
					if (stack.Contains(e))
						continue;
					else
						stack.Add(e);

					t = e.ElementSchemaType as XmlSchemaComplexType;
				}
				else
					t = c as XmlSchemaComplexType;

				// Prüfe die Complex-Typen
				if (t != null)
				{
					// Durchsuche die Sequencen, Alternativen
					var groupBase = t.ContentTypeParticle as XmlSchemaGroupBase;
					if (groupBase != null)
					{
						e = FindConfigElement(stack, groupBase.Items, name);
						if (e != null)
							return e;
					}
				}
			}
			return null;
		} // func FindConfigElement
				
		private IDEConfigurationAttribute GetConfigurationAttribute(XObject x)
		{
			var element = x.Parent;
			if (element == null)
				return null;

			// get the name
			var xName = (XName)null;
			if (x is XAttribute)
				xName = ((XAttribute)x).Name;
			else if (x is XElement)
				xName = ((XElement)x).Name;
			else
				return null;

			// find the element
			var elementDefinition =  GetConfigurationElement(element.Name);
			if (elementDefinition == null)
				return null;

			return elementDefinition.GetAttributes().FirstOrDefault(c => c.Name == xName);
		} // func GetConfigurationAttribute

		#endregion

		public IDEConfigurationAttribute GetAttribute(XObject attribute) => GetConfigurationAttribute(attribute);

		public IDEConfigurationElement this[XName name] => GetConfigurationElement(name);

		/// <summary>Main configuration file.</summary>
		public string ConfigurationFile => configurationFile;
		/// <summary>TimeStamp for the configuration.</summary>
		public DateTime ConfigurationStamp => ConfigurationStamp;
		/// <summary>List of configuration attached files.</summary>
		public IEnumerable<string> ConfigurationFiles => knownConfigurationFiles;
	} // class DEConfigurationService

	#endregion
}
