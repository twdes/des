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
using Neo.IronLua;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server.Configuration
{
	#region -- class DEConfigurationService -------------------------------------------

	internal sealed class DEConfigurationService : IDEConfigurationService
	{
		private readonly static XName xnDeleteMeAttribute = XName.Get("removeme", "__configurationservice__");

		#region -- struct SchemaAssemblyDefinition ------------------------------------

		private struct SchemaAssemblyDefinition
		{
			public SchemaAssemblyDefinition(XmlSchema schema, Assembly assembly)
			{
				this.Schema = schema;
				this.Assembly = assembly;
			} // ctor

			public Assembly Assembly { get; }
			public XmlSchema Schema { get; }

			public string TargetNamespace => Schema.TargetNamespace;
			public string Name => Schema.Id + ".xsd";

			public string DisplayName => Schema.SourceUri;
		} // struct SchemaAssemblyDefinition

		#endregion

		private readonly IServiceProvider sp;
		private readonly IDEServerResolver resolver;

		private readonly string configurationFile;
		private readonly PropertyDictionary configurationProperties;

		private DateTime configurationStamp; // max timestamp of the known configuration files
		private Dictionary<string, DateTime> knownConfigurationFiles = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		private readonly XmlNameTable nameTable; // name table
		private readonly XmlSchemaSet schema; // complete schema
		private readonly List<SchemaAssemblyDefinition> assemblySchemas = new List<SchemaAssemblyDefinition>(); // mapping schema to assembly

		private readonly Dictionary<XName, IDEConfigurationElement> elementResolveCache = new Dictionary<XName, IDEConfigurationElement>();

		#region -- Ctor/Dtor ----------------------------------------------------------

		public DEConfigurationService(IServiceProvider sp, string configurationFile, PropertyDictionary configurationProperties)
		{
			this.sp = sp;
			this.resolver = sp.GetService<IDEServerResolver>(false);

			this.configurationFile = configurationFile;
			this.configurationProperties = configurationProperties;
			this.configurationStamp = DateTime.MinValue;

			// create a empty schema
			nameTable = new NameTable();
			schema = new XmlSchemaSet(nameTable);

			// init schema
			UpdateSchema(Assembly.GetCallingAssembly());
		} // ctor

		#endregion

		#region -- Parse Configuration ------------------------------------------------

		#region -- class DEConfigurationStackException --------------------------------

		private class DEConfigurationStackException : DEConfigurationException
		{
			private readonly string stackFrames;

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

		#region -- class ParseFrame ---------------------------------------------------

		private class ParseFrame : LuaTable
		{
			private readonly LuaTable parentFrame;
			private readonly XObject source;

			private bool deleteNodes;

			public ParseFrame(LuaTable parentFrame, XObject source)
			{
				this.parentFrame = parentFrame;
				this.source = source;
			} // ctor

			public void AppendStackFrame(StringBuilder sbStack)
			{
				sbStack.Append(source.BaseUri);
				if (source is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
				{
					sbStack.Append(" (");
					sbStack.Append(lineInfo.LineNumber.ToString());
					sbStack.Append(',');
					sbStack.Append(lineInfo.LinePosition.ToString());
					sbStack.Append(')');
				}
				sbStack.AppendLine();
			} // proc GetStackFrame

			protected override object OnIndex(object key)
				=> base.OnIndex(key) ?? parentFrame?.GetValue(key);

			public ParseFrame Parent => parentFrame as ParseFrame;
			public bool IsDeleteNodes { get => deleteNodes; set => deleteNodes = value; }
		} // class ParseFrame

		#endregion

		#region -- class ParseContext -------------------------------------------------

		private class ParseContext : LuaPropertiesTable
		{
			private readonly Lua lua = new Lua();
			private readonly string basePath;
			private readonly Dictionary<string, DateTime> collectedFiles = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

			private ParseFrame currentFrame = null;

			#region -- Ctor/Dtor ------------------------------------------------------

			public ParseContext(PropertyDictionary arguments, string basePath)
				: base(arguments)
			{
				this.basePath = basePath;
			} // ctor

			#endregion

			#region -- Frames ---------------------------------------------------------

			public ParseFrame PushFrame(XNode source)
				=> currentFrame = new ParseFrame(currentFrame == null ? this : (LuaTable)currentFrame, source);
			
			public void PopFrame(ParseFrame frame)
			{
				if (currentFrame == null || currentFrame != frame)
					throw new InvalidOperationException("Invalid stack.");
				currentFrame = frame.Parent;
			} // proc PopFrame

			#endregion

			#region -- LoadFile -------------------------------------------------------

			/// <summary></summary>
			/// <param name="source"></param>
			/// <param name="fileName"></param>
			/// <returns></returns>
			public XDocument LoadFile(XObject source, string fileName)
			{
				try
				{
					// resolve macros
					ChangeConfigurationStringValue(this, fileName, out fileName);
					// load the file name
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
				if (!collectedFiles.ContainsKey(fileName))
					collectedFiles.Add(fileName, File.GetLastWriteTimeUtc(fileName));

				// test for assembly resource
				var sep = fileName.LastIndexOf(',');
				if (sep > 0)
				{
					var assemblyName = fileName.Substring(0, sep).Trim(); // first part is a assembly name
					var resourceName = fileName.Substring(sep + 1).Trim(); // secound the resource name

					var asm = Assembly.Load(assemblyName);
					if (asm == null)
						throw new ArgumentNullException("Assembly not loaded.");

					using (var src = asm.GetManifestResourceStream(resourceName))
					{
						if (src == null)
							throw new ArgumentNullException("Resource not found.");

						using (var xml = XmlReader.Create(src, Procs.XmlReaderSettings, fileName))
							return XDocument.Load(xml, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
					}
				}
				else
					return XDocument.Load(fileName, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
			} // proc LoadFile

			#endregion

			public Exception CreateConfigException(XObject x, string message, Exception innerException = null)
				=> new DEConfigurationStackException(currentFrame, x, message, innerException);

			public bool IsDefined(string expr)
			{
				var chunk = lua.CompileChunk("return " + expr + ";", "expr", null);
				return new LuaResult(chunk.Run(currentFrame)).ToBoolean();
			} // func IsDefined

			public ParseFrame CurrentFrame => currentFrame;
			public Dictionary<string, DateTime> CollectedFiles => collectedFiles;
		} // class ParseContext

		#endregion

		#region -- class XFileAnnotation ----------------------------------------------

		/// <summary></summary>
		/// <remarks>This annotation marks process elements.</remarks>
		public class XFileAnnotation { }

		#endregion

		/// <summary>Reads the configuration file and validates it agains the schema</summary>
		/// <returns></returns>
		public XElement ParseConfiguration()
		{
			// prepare arguments for the configuration
			var fileName = Path.GetFullPath(configurationFile);
			var context = new ParseContext(configurationProperties, Path.GetDirectoryName(fileName));

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
				ParseConfiguration(context, doc, new XFileAnnotation());
				context.PopFrame(frame);

				// remove all elements, that marked for deletion
				// and remove all parse frames
				RemoveFileNodesAndAnnotations(doc.Root);

				// update configuration info
				knownConfigurationFiles = context.CollectedFiles;
				configurationStamp = knownConfigurationFiles.Max(c => c.Value);
			}
			catch (Exception e)
			{
				if (e is DEConfigurationStackException)
					throw;
				throw context.CreateConfigException(doc, "Could not parse configuration file.", e);
			}

			// check the schema
			try
			{
				doc.Validate(schema, ValidationEvent, false);
			}
			catch
			{
				// write configuration
				try { doc.Save(Path.Combine(Path.GetTempPath(), "des.xml")); }
				catch { }

				throw;
			}

			// debug configuration
			if (configurationProperties.GetProperty("OutputTemp", false))
				doc.Save(Path.Combine(Path.GetTempPath(), "des.xml"));

			return doc.Root;
		} // func ParseConfiguration

		private void RemoveFileNodesAndAnnotations(XElement cur)
		{
			cur.RemoveAnnotations<XFileAnnotation>();
			foreach (var a in cur.Attributes())
				a.RemoveAnnotations<XFileAnnotation>();

			var x = cur.FirstNode;
			while (x != null)
			{
				XNode deleteMe = null;

				if (x is XElement xe)
				{
					if (xe.GetAttribute(xnDeleteMeAttribute, false))
						deleteMe = x;
					else
						RemoveFileNodesAndAnnotations(xe);
				}
				x = x.NextNode;

				if (deleteMe != null)
					deleteMe.Remove();
			}
		} // func RemoveFileNodesAndAnnotations

		private void ValidationEvent(object sender, ValidationEventArgs e)
		{
			if (e.Severity == XmlSeverityType.Warning)
				sp.LogProxy().LogMsg(LogMsgType.Warning, "Validation: {0}\n{1} ({2:N0},{3:N0})", e.Message, e.Exception.SourceUri, e.Exception.LineNumber, e.Exception.LinePosition);
			else // rethrow exception
				throw e.Exception;
		} // proc ValidationEvent

		private void ParseConfiguration(ParseContext context, XContainer x, XFileAnnotation fileToken)
		{
			var c = x.FirstNode;
			while (c != null)
			{
				var deleteMe = (XNode)null;
				if (c is XComment)
					deleteMe = c;
				else if (c is XProcessingInstruction)
				{
					ParseConfigurationPI(context, (XProcessingInstruction)c, fileToken);
					deleteMe = c;
				}
				else
				{
					string value;
					if (context.CurrentFrame.IsDeleteNodes)
						deleteMe = c;
					else if (c is XElement xCur)
					{
						// annotate
						if (xCur.Annotation<XFileAnnotation>() == null)
							xCur.AddAnnotation(fileToken);

						// Replace values in attributes
						foreach (var attr in xCur.Attributes())
						{
							if (ChangeConfigurationValue(context, attr, attr.Value, out value))
								attr.Value = value;

							// mark the attribute with the current frame
							if (attr.Annotation<XFileAnnotation>() == null)
								attr.AddAnnotation(fileToken);
						}

						// Parse the current element
						var newFrame = context.PushFrame(xCur);
						ParseConfiguration(context, xCur, fileToken);
						context.PopFrame(newFrame);

						#region -- Load assemblies -> they preprocessor needs them --
						if (xCur.Name == xnServer)
						{
							foreach (var cur in xCur.Elements())
							{
								if (cur.Name == xnServerResolve) // resolve paths
								{
									if (ChangeConfigurationValue(context, cur, cur.Value, out value))
										cur.Value = value;

									switch (cur.GetAttribute("type", "net"))
									{
										case "net":
											resolver?.AddPath(cur.Value);
											break;
										case "platform":
											resolver?.AddPath(cur.Value);
											if (IntPtr.Size == 4) // 32bit
												DEServer.AddToProcessEnvironment(Path.Combine(cur.Value, "x86"));
											else
												DEServer.AddToProcessEnvironment(Path.Combine(cur.Value, "x64"));
											break;
										case "envonly":
											DEServer.AddToProcessEnvironment(cur.Value);
											break;
										default:
											throw context.CreateConfigException(cur, "resolve @type has an invalid attribute value.");
									}
								}
								else if (cur.Name == xnServerLoad)
								{
									if (ChangeConfigurationValue(context, cur, cur.Value, out value))
										cur.Value = value;
									try
									{
										UpdateSchema(Assembly.Load(cur.Value));
									}
									catch (Exception e)
									{
										throw context.CreateConfigException(cur, String.Format("Failed to load assembly ({0}).", cur.Value), e);
									}
								}
							}
						}
						#endregion
					}
					else if (c is XText xText) // replace values in text elements
					{
						if (ChangeConfigurationValue(context, xText, xText.Value, out value))
							xText.Value = value;
					}
				}

				// next node
				c = c.NextNode;

				// remove the node after next, in the other case next has null
				if (deleteMe != null)
					deleteMe.Remove();
			}
		} // proc ParseConfiguration

		private void ParseConfigurationPI(ParseContext context, XProcessingInstruction xPI, XFileAnnotation currentFileToken)
		{
			if (xPI.Target == "des-begin") // start a block
			{
				try
				{
					context.CurrentFrame.IsDeleteNodes = !context.IsDefined(xPI.Data);
				}
				catch (Exception e)
				{
					throw context.CreateConfigException(xPI, $"Could execute expression: {xPI.Data}.", e);
				}
			}
			else if (xPI.Target == "des-end")
			{
				context.CurrentFrame.IsDeleteNodes = false;
			}
			else if (!context.CurrentFrame.IsDeleteNodes)
			{
				if (xPI.Target.StartsWith("des-var-"))
				{
					var varName = xPI.Target.Substring(8);
					context.CurrentFrame.SetMemberValue(varName, Lua.RtReadValue(xPI.Data));
				}
				else if (xPI.Target == "des-include")
				{
					IncludeConfigTree(context, xPI, currentFileToken);
				}
				else if (xPI.Target == "des-merge")
				{
					MergeConfigTree(context, xPI, currentFileToken);
				}
				else if (xPI.Target == "des-remove-me")
				{
					RemoveConfigElement(context, xPI);
				}
			}
		} // proc ParseConfiguration

		private void RemoveConfigElement(ParseContext context, XProcessingInstruction xPI)
		{
			if (String.Compare(xPI.Data, Boolean.FalseString, StringComparison.OrdinalIgnoreCase) == 0)
				xPI.Parent.SetAttributeValue(xnDeleteMeAttribute, null);
			else
				xPI.Parent.SetAttributeValue(xnDeleteMeAttribute, true);
		} // proc RemoveConfigElement

		private void IncludeConfigTree(ParseContext context, XProcessingInstruction xPI, XFileAnnotation currentFileToken)
		{
			if (xPI.Parent == null)
				throw context.CreateConfigException(xPI, "It is not allowed to include to a root element.");

			var xInc = context.LoadFile(xPI, xPI.Data).Root;
			if (xInc.Name == xnInclude)
			{
				// Copy the baseuri annotation
				var copy = new List<XElement>();
				foreach (var xSrc in xInc.Elements())
				{
					Procs.XCopyAnnotations(xSrc, xSrc);
					copy.Add(xSrc);

					// mark node
					if (xSrc.Annotation<XFileAnnotation>() == null)
						xSrc.AddAnnotation(currentFileToken);
					// mark attributes
					foreach (var xAttr in xSrc.Attributes())
					{
						if (xAttr.Annotation<XFileAnnotation>() == null)
							xAttr.AddAnnotation(currentFileToken);
					}
				}

				// Remove all elements from the source, that not get internal copied.
				xInc.RemoveAll();
				xPI.AddAfterSelf(copy);
			}
			else
			{
				Procs.XCopyAnnotations(xInc, xInc);
				xInc.Remove();
				xInc.AddAnnotation(currentFileToken);
				// todo: mark attributes
				xPI.AddAfterSelf(xInc);
			}
		} // proc IncludeConfigTree

		private void MergeConfigTree(ParseContext context, XProcessingInstruction xPI, XFileAnnotation currentFileToken)
		{
			var xDoc = context.LoadFile(xPI, xPI.Data);

			// parse the loaded document
			var fileToken = new XFileAnnotation();
			var newFrame = context.PushFrame(xPI);
			if (xDoc.Root.Name != xnFragment)
				throw context.CreateConfigException(xDoc.Root, "<fragment> expected.");

			ParseConfiguration(context, xDoc, fileToken);
			context.PopFrame(newFrame);

			// merge the parsed nodes
			MergeConfigTree(xPI.Document.Root, xDoc.Root, currentFileToken);
		} // proc MergeConfigTree

		private static bool IsSameConfigurationElement(XElement x, IDEConfigurationElement element, XName compareName)
			=> element?.IsName(compareName) ?? x.Name == compareName;

		private int FindInChildElements(XName[] childElements, int offset, XElement xInsert)
		{
			var element = this[xInsert.Name];
			var substitionIndex = -1;

			for (var i = offset; i < childElements.Length; i++)
			{
				if (element != null)
				{
					if (element.IsName(childElements[i]))
						substitionIndex = i;
				}
				else if (childElements[i] == xInsert.Name)
					return i;
			}

			return substitionIndex;
		} // func FindInChildElements

		private XElement MergeConfigTreeFindInsertBefore(XElement xRoot, XElement xAdd)
		{
			var rootElement = this[xRoot.Name];
			var addElement = rootElement != null ? this[xAdd.Name] : null;
			var childElements = rootElement is DEConfigurationElement ce
				? DEConfigurationHelper.GetAllSchemaElements(ce.Item).Select(c => DEConfigurationHelper.GetXName(c.QualifiedName)).ToArray()
				: Array.Empty<XName>();
			var childInsertIndex = childElements != null
				? Array.FindIndex(childElements, c => IsSameConfigurationElement(xAdd, addElement, c))
				: -1;

			var xLastName = (XName)null;
			var lastChildIndex = 0;
			var startInsertAfter = false;
			foreach (var xInsert in xRoot.Elements())
			{
				if (xInsert.Name != xLastName) // check for a new xname
				{
					if (startInsertAfter) // startInsertAfter - mode is set, return current element, to insert before
						return xInsert;
					if (IsSameConfigurationElement(xAdd, addElement, xInsert.Name)) // check if this is the same name or substition group
						startInsertAfter = true;
					else if (childInsertIndex >= 0)
					{
						var tmp = FindInChildElements(childElements, lastChildIndex, xInsert);
						if (tmp > lastChildIndex)
						{
							lastChildIndex = tmp;
							if (lastChildIndex > childInsertIndex)
								return xInsert;
						}
					}
				}
			}

			return null;
		} // func MergeConfigTreeFindInsertBefore

		private void MergeConfigTree(XElement xTarget, XElement xMerge, XFileAnnotation currentFileToken)
		{
			static bool IsOverrideAble(XObject x)
			{
				// check for annotation, that marks the base file content, we do not want to override base definitions.
				// - all processed definitions have the currentFileToken annotation => override
				// - null values should not override, because they are not processed yet => no override
				// - imported values => override
				return x.Annotation<XFileAnnotation>() != null;
				//return a != null && a != currentFileToken;
			} // func IsOverrideAble

			// merge value
			var elementValueDefinition = GetValue(xMerge);
			if (elementValueDefinition != null)
			{
				if (elementValueDefinition.IsList) // merge list values
					xTarget.Value = xTarget.Value + " " + xMerge.Value;
				else if (IsOverrideAble(xTarget))
					xTarget.Value = xMerge.Value;
			}

			// merge attributes
			var attributeMerge = xMerge.FirstAttribute;
			while (attributeMerge != null)
			{
				var attributeRoot = xTarget.Attribute(attributeMerge.Name);
				if (attributeRoot == null) // attribute does not exists --> insert
				{
					xTarget.SetAttributeValue(attributeMerge.Name, attributeMerge.Value);
				}
				else // attribute exists --> override or combine lists
				{
					var valueDefinition = GetValue(attributeMerge);
					if (valueDefinition != null)
					{
						if (valueDefinition.IsList) // list detected
							attributeRoot.Value = attributeRoot.Value + " " + attributeMerge.Value;
						else if (IsOverrideAble(attributeRoot))
							attributeRoot.Value = attributeMerge.Value;
					}
				}

				attributeMerge = attributeMerge.NextAttribute;
			}

			// merge elements
			var xCurNodeMerge = xMerge.FirstNode;
			while (xCurNodeMerge != null)
			{
				var xNextNode = xCurNodeMerge.NextNode;

				if (xCurNodeMerge is XElement xCurMerge)
				{
					var xCurRoot = FindConfigTreeElement(xTarget, xCurMerge);
					if (xCurRoot == null) // node is not present -> include
					{
						Procs.XCopyAnnotations(xCurMerge, xCurMerge);
						xCurMerge.Remove();

						// find the xsd insert position
						var xInsertBefore = xTarget.HasElements ? MergeConfigTreeFindInsertBefore(xTarget, xCurMerge) : null;
						if (xInsertBefore == null)
							xTarget.Add(xCurMerge);
						else
							xInsertBefore.AddBeforeSelf(xCurMerge);
					}
					else // merge node
						MergeConfigTree(xCurRoot, xCurMerge, currentFileToken);
				}

				xCurNodeMerge = xNextNode;
			}
		} // proc MergeConfigTree

		private XElement FindConfigTreeElement(XElement xRootParent, XElement xSearch)
		{
			var elementDefinition = GetConfigurationElement(xSearch.Name);
			if (elementDefinition == null)
				throw new DEConfigurationException(xSearch, $"Definition for configuration element '{xSearch.Name}' is missing.");

			if (elementDefinition.TypeName == "KeyType") // compare by values
			{
				foreach (var x in xRootParent.Elements(xSearch.Name))
				{
					if (Equals(x.Value, xSearch.Value))
						return x;
				}
				return null;
			}
			else // compare by attribute (primary key mode)
			{
				// find primary key columns
				var primaryKeys = (from c in elementDefinition.GetAttributes()
								   where c.IsPrimaryKey
								   select c).ToArray();

				if (primaryKeys.Length == 0)
					return xRootParent.Elements(xSearch.Name).FirstOrDefault();
				else
				{
					foreach (var x in xRootParent.Elements(xSearch.Name))
					{
						var r = true;

						for (var i = 0; i < primaryKeys.Length; i++)
						{
							var attr1 = x.Attribute(primaryKeys[i].Name);
							var attr2 = xSearch.Attribute(primaryKeys[i].Name);

							if (!Equals(attr1?.Value, attr2?.Value))
							{
								r = false;
								break;
							}
						}

						if (r)
							return x;
					}

					return null;
				}
			}
		} // func FindConfigTreeElement

		private static Regex macroReplacement = new Regex("\\$\\(([\\w\\d\\-_]+)\\)", RegexOptions.Singleline | RegexOptions.Compiled);

		private bool ChangeConfigurationValue(ParseContext context, XObject x, string currentValue, out string newValue)
		{
			var valueModified = ChangeConfigurationStringValue(context, currentValue, out newValue);

			// first check for type converter
			var valueDefinition = GetValue(x);
			if (valueDefinition != null)
			{
				if (valueDefinition.TypeName == "PathType")
				{
					newValue = ProcsDE.GetFileName(x, newValue);

					valueModified |= true;
				}
				else if (valueDefinition.TypeName == "PathArray")
				{
					newValue = Procs.JoinPaths(Procs.SplitPaths(newValue).Select(c => ProcsDE.GetFileName(x, c)));

					valueModified |= true;
				}
				else if (valueDefinition.TypeName == "CertificateType")
				{
					if (String.IsNullOrEmpty(newValue) || !newValue.StartsWith("store://"))
					{
						newValue = ProcsDE.GetFileName(x, newValue);
						valueModified |= true;
					}
				}
			}

			return valueModified;
		} // func ChangeConfigurationValue

		private static bool ChangeConfigurationStringValue(ParseContext context, string currentValue, out string newValue)
		{
			var valueModified = false;

			// trim always the value
			newValue = currentValue.Trim();

			// first check for macro substitionen
			newValue = macroReplacement.Replace(newValue,
				m =>
				{
					// mark value as modified
					valueModified |= true;
					return context.CurrentFrame.GetOptionalValue(m.Groups[1].Value, String.Empty, true);
				}
			);

			return valueModified;
		} // func ChangeConfigurationStringValue

		#endregion

		#region -- Update Schema ------------------------------------------------------

		public void UpdateSchema(Assembly assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException("assembly");

			var log = sp.LogProxy();
			foreach (var schemaAttribute in assembly.GetCustomAttributes<DEConfigurationSchemaAttribute>())
			{
				using (var src = assembly.GetManifestResourceStream(schemaAttribute.BaseType, schemaAttribute.ResourceId))
				{
					var schemaUri = assembly.FullName + ", " + (schemaAttribute.BaseType == null ? "" : schemaAttribute.BaseType.FullName + ".") + schemaAttribute.ResourceId;
					try
					{
						{
							if (src == null)
								throw new Exception("Could not locate resource.");

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

								// clear includes
								for (var i = xmlSchema.Includes.Count - 1; i >= 0; i--)
								{
									if (xmlSchema.Includes[i] is XmlSchemaInclude cur
										&& assemblySchemas.Exists(c => String.Compare(c.Schema.Id, cur.Id, StringComparison.OrdinalIgnoreCase) == 0))
										xmlSchema.Includes.RemoveAt(i);
								}


								// Add the schema
								assemblySchemas.Add(new SchemaAssemblyDefinition(xmlSchema, assembly));
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

		#region -- Schema description -------------------------------------------------

		private IDEConfigurationElement GetConfigurationElement(XName name)
		{
			lock (schema)
			{
				// search cache
				if (elementResolveCache.TryGetValue(name, out var r))
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

				// Ermittle den Typ, des Elementes
				if (c is XmlSchemaElement e)
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

				// check complex types
				if (t != null)
				{
					// Durchsuche die Sequencen, Alternativen
					if (t.ContentTypeParticle is XmlSchemaGroupBase groupBase)
					{
						e = FindConfigElement(stack, groupBase.Items, name);
						if (e != null)
							return e;
					}
				}

				// search with in the sequences
				if (c is XmlSchemaSequence seq)
				{
					e = FindConfigElement(stack, seq.Items, name);
					if (e != null)
						return e;
				}
			}
			return null;
		} // func FindConfigElement
		
		#endregion

		public IDEConfigurationValue GetValue(XObject x)
		{
			if (x is XElement xElement) // value of this element
			{
				var elementDefinition = GetConfigurationElement(xElement.Name);
				if (elementDefinition == null)
					return null;

				return elementDefinition.Value;
			}
			else if (x is XAttribute xAttribute)
			{
				var elementDefinition = GetConfigurationElement(xAttribute.Parent.Name);
				if (elementDefinition == null)
					return null;

				return elementDefinition.GetAttributes().FirstOrDefault(c => c.Name == xAttribute.Name);
			}
			else
				return null;
		} // func GetValue

		public IDEConfigurationElement this[XName name] => GetConfigurationElement(name);

		/// <summary>Main configuration file.</summary>
		public string ConfigurationFile => configurationFile;
		/// <summary>TimeStamp for the configuration.</summary>
		public DateTime ConfigurationStamp => configurationStamp;
		/// <summary>List of configuration attached files.</summary>
		public IReadOnlyDictionary<string, DateTime> ConfigurationFiles => knownConfigurationFiles;
	} // class DEConfigurationService

	#endregion
}
