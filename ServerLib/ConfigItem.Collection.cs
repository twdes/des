using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEListService -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>List-Service für die Darstellung und export der Listen.</summary>
	public interface IDEListService
	{
		/// <summary>Serialisiert die Liste auf den Http-Stream.</summary>
		/// <param name="r">Http response</param>
		/// <param name="controller">Liste</param>
		/// <param name="startAt">Übergebener Startwert.</param>
		/// <param name="count">Anzahl der Elemente die zurückgeliefert werden sollen</param>
		void WriteList(IDEContext r, IDEListController controller, int startAt, int count);
	} // interface IDEListService

	#endregion

	#region -- interface IDEListDescriptor ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Beschreibt eine Liste für den Export zum Client.</summary>
	public interface IDEListDescriptor
	{
		/// <summary>Schreibt die Struktur, der Liste.</summary>
		/// <param name="xml"></param>
		void WriteType(DEListTypeWriter xml);
		/// <summary>Schreibt ein einzelnes Element.</summary>
		/// <param name="xml"></param>
		/// <param name="item"></param>
		void WriteItem(DEListItemWriter xml, object item);
	} // interface IDEListDescriptor<T>

	#endregion
	
	#region -- class DEListTypeWriter ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Schreibt die definition eines Listentypes</summary>
	public sealed class DEListTypeWriter
	{
		private enum PropertyType
		{
			Element,
			Attribute,
			Value
		} // enum PropertyType

		private bool inType = false;

		private XmlWriter xml;

		/// <summary></summary>
		/// <param name="xml"></param>
		public DEListTypeWriter(XmlWriter xml)
		{
			this.xml = xml;
		} // ctor

		public void WriteStartType(string sTypeName)
		{
			if (inType)
				throw new InvalidOperationException("Nested types are not allowed.");

			xml.WriteStartElement(sTypeName);
			inType = true;
		} // proc StartType

		public void WriteEndType()
		{
			inType = false;
			xml.WriteEndElement();
		} // proc WriteEndType

		/// <summary>Beschreibt eine Eigenschaft.</summary>
		/// <param name="propertyName">Erzeugt ein Element, welches einen Type enthalten kann. Attribute sind nicht erlaubt</param>
		public void WriteProperty(string propertyName, string typeName)
		{
			if (propertyName == ".")
				WriteProperty(PropertyType.Value, String.Empty, typeName);
			else
				WriteProperty(PropertyType.Element, propertyName, typeName);
		} // func WriteProperty

		/// <summary>Beschreibt eine Eigenschaft.</summary>
		/// <param name="propertyName">Wird ein @ vor die Eigenschaft gesetzt, so wird das Element als Property angelegt.</param>
		/// <param name="type"></param>
		public void WriteProperty(string propertyName, Type type)
		{
			if (String.IsNullOrEmpty(propertyName))
				throw new ArgumentNullException();

			string sTypeName = LuaType.GetType(type).AliasOrFullName;

			if (propertyName == ".")
				WriteProperty(PropertyType.Value, String.Empty, sTypeName);
			else if (propertyName.StartsWith("@"))
				WriteProperty(PropertyType.Attribute, propertyName.Substring(1), sTypeName);
			else
				WriteProperty(PropertyType.Element, propertyName, sTypeName);
		} // proc WriteProperty

		private void WriteProperty(PropertyType type, string propertyName, string typeString)
		{
			if (!inType)
				throw new InvalidOperationException("No type defined.");
			
			CheckXmlName(propertyName);

			switch (type)
			{
				case PropertyType.Attribute:
					xml.WriteStartElement("attribute");
					break;
				case PropertyType.Element:
				case PropertyType.Value:
					xml.WriteStartElement("element");
					break;
				default:
					throw new InvalidOperationException();
			}

			if (!String.IsNullOrEmpty(propertyName))
				xml.WriteAttributeString("name", propertyName);
			xml.WriteAttributeString("type", typeString);

			xml.WriteEndElement();
		} // proc WriteProprerty

		private static void CheckXmlName(string propertyName)
		{
			for (int i = 0; i < propertyName.Length; i++)
			{
				if (!Char.IsLetterOrDigit(propertyName[i]) && propertyName[i] != '.')
					throw new ArgumentException("Invalid xml name.");
			}
		} // func CheckXmlName
	} // class DEListTypeWriter

	#endregion

	#region -- class DEListItemWriter ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEListItemWriter
	{
		private XmlWriter xml;

		public DEListItemWriter(XmlWriter xml)
		{
			this.xml = xml;
		} // ctor

		private string ConvertValue(object value)
		{
			if (value == null)
				return null;
			else if (value is Type)
				return LuaType.GetType((Type)value).AliasOrFullName;
			else
				return Procs.ChangeType<string>(value);
		} // func ConvertValue

		public void WriteStartProperty(string sPropertyName)
		{
			xml.WriteStartElement(sPropertyName);
		} // proc WriteStartProperty

		public void WriteValue(object value)
		{
			string sValue = ConvertValue(value);
			if (sValue == null)
				return;

			if (sValue.IndexOfAny(new char[] { '<', '>', '\n', '\t' }) >= 0)
				xml.WriteCData(sValue);
			else
				xml.WriteValue(sValue);
		} // proc WriteValue

		public void WriteEndProperty()
		{
			xml.WriteEndElement();
		} // proc WriteEndProperty

		public void WriteElementProperty(string sPropertyName, object value)
		{
			WriteStartProperty(sPropertyName);
			WriteValue(value);
			WriteEndProperty();
		} // proc WriteElementProperty

		public void WriteAttributeProperty(string sPropertyName, object value)
		{
			string sValue = ConvertValue(value);
			if (sValue != null)
				xml.WriteAttributeString(sPropertyName, sValue);
		} // proc WriteAttributeProperty
	} // class DEListItemWriter

	#endregion

	#region -- interface IDEListController ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Verwaltet eine Liste, die Exportiert werden kann.</summary>
	public interface IDEListController : IDisposable
	{
		/// <summary>Wird aufgerufen, um den Zugriff auf die Liste zu sperren.</summary>
		/// <returns></returns>
		IDisposable EnterReadLock();
		/// <summary>Wird aufgerufen, um den Zugriff auf die Liste zu sperren.</summary>
		/// <returns></returns>
		IDisposable EnterWriteLock();

		/// <summary>Wird ausgeführt, bevor die Liste zum Client gesendet wird.</summary>
		/// <param name="arguments"></param>
		void OnBeforeList();

		/// <summary>Bezeichner der Liste</summary>
		string Id { get; }
		/// <summary>Anzeigename der Liste</summary>
		string DisplayName { get; }

		/// <summary>Daten der Liste</summary>
		IEnumerable List { get; }

		/// <summary></summary>
		string SecurityToken { get; }

		/// <summary>Wie soll die Liste aufgegeben werden.</summary>
		IDEListDescriptor Descriptor { get; }
	} // interface IDEListController

	#endregion

	#region -- class DEListControllerBase -----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Implementiert einen Controller für eine Liste.</summary>
	public abstract class DEListControllerBase : IDEListController
	{
		private ReaderWriterLockSlim listLock;
		private readonly DEConfigItem configItem;
		private readonly string id;
		private readonly string displayName;
		private readonly IDEListDescriptor descriptor;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEListControllerBase(DEConfigItem configItem, IDEListDescriptor descriptor, string id, string displayName)
		{
			this.configItem = configItem;
			this.id = id;
			this.displayName = displayName;
			this.descriptor = descriptor;
			this.listLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

			configItem.RegisterList(id, this);
		} // ctor

		~DEListControllerBase()
		{
			Dispose(false);
		} // ctor

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				// Liste wieder austragen
				configItem.UnregisterList(this);

				// Sperren zerstören
				Procs.FreeAndNil(ref listLock);
			}
		} // proc Dispose

		#endregion

		#region -- EnterReadLock, EnterWriteLock ------------------------------------------

		public IDisposable EnterReadLock()
		{
			if (listLock.IsWriteLockHeld)
				return null;
			else
			{
				listLock.EnterReadLock();
				return new DisposableScope(listLock.ExitReadLock);
			}
		} // func EnterReadLock

		public IDisposable EnterWriteLock()
		{
			listLock.EnterWriteLock();
			return new DisposableScope(listLock.ExitWriteLock);
		} // func EnterWriteLock

		#endregion

		public virtual void OnBeforeList()
		{
		} // proc OnBeforeList

		public string Id => id;
		public virtual string DisplayName => displayName;
		public virtual string SecurityToken => DEConfigItem.SecuritySys;
		public abstract IEnumerable List { get; }
		public IDEListDescriptor Descriptor => descriptor;

		public DEConfigItem ConfigItem => configItem;
	} // class DEListControllerBase

	#endregion

	#region -- interface IDERangeEnumerable ---------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Enumerable, welches einen Bereich der Liste abfragen kann.</summary>
	public interface IDERangeEnumerable<T> : IEnumerable
	{
		/// <summary>Erweiterter Enumerator.</summary>
		/// <param name="start">Erstes Element welches abgefragt werden soll.</param>
		/// <param name="count">Anzahl der Elemente</param>
		/// <returns>Enumerator</returns>
		IEnumerator<T> GetEnumerator(int start, int count);

		/// <summary>Total number of rows, if the number is unknown, than return -1.</summary>
		int Count { get; }
	} // interface IDERangeEnumerator<T>

	#endregion

	#region -- interface IDERangeEnumerable2 --------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDERangeEnumerable2<T> : IDERangeEnumerable<T>
	{
		IEnumerator<T> GetEnumerator(int start, int count, IPropertyReadOnlyDictionary selector);
	} // interface IDERangeEnumerable2

	#endregion

	#region -- class DEEnumerator<T> ----------------------------------------------------

	internal sealed class DEEnumerator<T> : IEnumerator<T>, IEnumerator, IDisposable
	{
		private IEnumerator<T> enumBase;
		private IDisposable lockScope;

		public DEEnumerator(IDEListController controller, IEnumerable<T> enumerable)
		{
			this.lockScope = controller.EnterReadLock();
			this.enumBase = enumerable.GetEnumerator();
		} // ctor

		public void Dispose()
		{
			Procs.FreeAndNil(ref enumBase);
			Procs.FreeAndNil(ref lockScope);
		} // proc Dispose

		void IEnumerator.Reset() { enumBase.Reset(); }
		bool IEnumerator.MoveNext() { return enumBase.MoveNext(); }

		T IEnumerator<T>.Current { get { return enumBase.Current; } }
		object IEnumerator.Current { get { return enumBase.Current; } }
	} // class DEEnumerator<T>

	#endregion

	#region -- class DEList<T> ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Liste mit speziellen Eigenschaften:
	/// - Schreibzugriff sperrt liste, multiples Lesen ist möglich.
	/// - Erzeugt einen Zugriff im Log</summary>
	public sealed class DEList<T> : DEListControllerBase, IList<T>, IDisposable
	{
		private List<T> innerList = new List<T>();

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEList(DEConfigItem configItem, string id, string displayName)
			: base(configItem, DEConfigItem.CreateListDescriptorFromType(typeof(T)), id, displayName)
		{
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					using (EnterWriteLock())
					{
						innerList.Clear();
						innerList = null;
					}
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		#region -- Liste ------------------------------------------------------------------

		public void Add(T item)
		{
			using (EnterWriteLock())
				innerList.Add(item);
		} // proc Add

		public void AddRange(IEnumerable<T> items)
		{
			using (EnterWriteLock())
				innerList.AddRange(items);
		} // proc AddRange

		public int FindIndex(Predicate<T> predicate)
		{
			using (EnterReadLock())
				return innerList.FindIndex(predicate);
		} // func IDEList<T>.FindIndex

		public bool Remove(T item)
		{
			using (EnterWriteLock())
				return innerList.Remove(item);
		} // func Remove

		public void Clear()
		{
			using (EnterWriteLock())
				innerList.Clear();
		} // proc Clear

		public bool Contains(T item)
		{
			using (EnterReadLock())
				return innerList.Contains(item);
		} // func Contains

		public void CopyTo(T[] array, int arrayIndex)
		{
			using (EnterReadLock())
				innerList.CopyTo(array, arrayIndex);
		} // proc CopyTo

		public void Insert(int index, T item)
		{
			using (EnterWriteLock())
				innerList.Insert(index, item);
		} // proc Insert

		public void RemoveAt(int index)
		{
			using (EnterWriteLock())
				innerList.RemoveAt(index);
		} // proc RemoveAt

		public int IndexOf(T item)
		{
			using (EnterReadLock())
				return innerList.IndexOf(item);
		} // func IndexOf

		public IEnumerator<T> GetEnumerator() => new DEEnumerator<T>(this, innerList);
		IEnumerator IEnumerable.GetEnumerator() => new DEEnumerator<T>(this, innerList);

		public T this[int index]
		{
			get
			{
				using (EnterReadLock())
					return innerList[index];
			}
			set
			{
				using (EnterWriteLock())
					innerList[index] = value;
			}
		} // prop this

		public int Count
		{
			get
			{
				using (EnterReadLock())
					return innerList.Count;
			}
		} // prop Count

		public bool IsReadOnly => false;

		public override IEnumerable List => innerList;

		#endregion
	} // class ConfigItemCollection

	#endregion

	#region -- class DEDictionary<TKey, TItem> ------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEDictionary<TKey, TItem> : DEListControllerBase, IDictionary<TKey, TItem>, IDisposable
	{
		private IDictionary<TKey, TItem> innerDictionary;

		public DEDictionary(DEConfigItem configItem, string id, string displayName, IEqualityComparer<TKey> comparer = null)
			:base(configItem, null, id, displayName)
		{
			this.innerDictionary = new Dictionary<TKey, TItem>(comparer == null ? EqualityComparer<TKey>.Default : comparer);
		} // ctor

		public DEDictionary(DEConfigItem configItem, string id, string displayName, IComparer<TKey> comparer)
			: base(configItem, null, id, displayName)
		{
			this.innerDictionary = new SortedDictionary<TKey, TItem>(comparer == null ? Comparer<TKey>.Default : comparer);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					using (EnterWriteLock())
					{
						innerDictionary.Clear();
						innerDictionary = null;
					}
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		public void Add(TKey key, TItem value)
		{
			using (EnterWriteLock())
				innerDictionary.Add(key, value);
		} // Add

		void ICollection<KeyValuePair<TKey, TItem>>.Add(KeyValuePair<TKey, TItem> item)
		{
			using (EnterWriteLock())
				innerDictionary.Add(item.Key, item.Value);
		} // proc ICollection<KeyValuePair<TKey, TItem>>.Add

		public bool ContainsKey(TKey key)
		{
			using (EnterReadLock())
				return innerDictionary.ContainsKey(key);
		} // func ContainsKey

		public bool TryGetValue(TKey key, out TItem value)
		{
			using (EnterReadLock())
				return innerDictionary.TryGetValue(key, out value);
		} // func TryGetValue

		public bool Remove(TKey key)
		{
			using (EnterWriteLock())
				return innerDictionary.Remove(key);
		} // func Remove

		public void Clear()
		{
			using (EnterWriteLock())
				innerDictionary.Clear();
		} // proc Clear

		void ICollection<KeyValuePair<TKey, TItem>>.CopyTo(KeyValuePair<TKey, TItem>[] array, int arrayIndex)
		{
			using (EnterReadLock())
				((ICollection<KeyValuePair<TKey, TItem>>)innerDictionary).CopyTo(array, arrayIndex);
		} // func ICollection<KeyValuePair<TKey, TItem>>.CopyTo

		bool ICollection<KeyValuePair<TKey, TItem>>.Remove(KeyValuePair<TKey, TItem> item)
		{
			using (EnterWriteLock())
				return ((ICollection<KeyValuePair<TKey, TItem>>)innerDictionary).Remove(item);
		} // func ICollection<KeyValuePair<TKey, TItem>>.Remove

		bool ICollection<KeyValuePair<TKey, TItem>>.Contains(KeyValuePair<TKey, TItem> item)
		{
			using (EnterReadLock())
				return ((ICollection<KeyValuePair<TKey, TItem>>)innerDictionary).Contains(item);
		} // ICollection<KeyValuePair<TKey, TItem>>.Contains

		ICollection<TKey> IDictionary<TKey, TItem>.Keys { get { throw new NotSupportedException(); } }
		ICollection<TItem> IDictionary<TKey, TItem>.Values { get { throw new NotSupportedException(); } }

		IEnumerator IEnumerable.GetEnumerator() { return new DEEnumerator<KeyValuePair<TKey, TItem>>(this, innerDictionary); }
		IEnumerator<KeyValuePair<TKey, TItem>> IEnumerable<KeyValuePair<TKey, TItem>>.GetEnumerator() { return new DEEnumerator<KeyValuePair<TKey, TItem>>(this, innerDictionary); }

		bool ICollection<KeyValuePair<TKey, TItem>>.IsReadOnly { get { return false; } }

		public TItem this[TKey key]
		{
			get
			{
				using (EnterReadLock())
					return innerDictionary[key];
			}
			set
			{
				using (EnterWriteLock())
					innerDictionary[key] = value;
			}
		} // prop this

		public int Count
		{
			get
			{
				using (EnterReadLock())
					return innerDictionary.Count;
			}
		} // prop Count

		public override IEnumerable List { get { return innerDictionary; } }
	} // class DEDictionary

	#endregion

	#region -- class DEListTypePropertyAttribute ----------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Markiert eine Eigenschaft für den automatischen Export.</summary>
	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Interface)]
	public class DEListTypePropertyAttribute : Attribute
	{
		private string name;

		public DEListTypePropertyAttribute(string name)
		{
			this.name = name;
		} // ctor

		public string Name => name;
	} // class DEListTypePropertyAttribute

	#endregion

	#region -- class DEConfigItemPublicAction -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEConfigItemPublicAction
	{
		public DEConfigItemPublicAction(string actionId)
		{
			this.ActionId = actionId;
		} // ctor

		public string ActionId { get; }
		public string DisplayName { get; set; }
	} // class DEConfigItemPublicAction

	#endregion

	#region -- class DEConfigItemPublicPanel --------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEConfigItemPublicPanel
	{
		public DEConfigItemPublicPanel(string id, string uri)
		{
			this.Id = id;
			this.Uri = uri;
		} // ctor

		public string Id { get; }
		public string DisplayName { get; set; }
		public string Uri { get; }
	} // class DEConfigItemPublicPanel

	#endregion

	#region -- class DEConfigItem -------------------------------------------------------

	public partial class DEConfigItem
	{
		private List<IDEListController> controllerList = new List<IDEListController>();
		private Dictionary<string, object> publishedItems = new Dictionary<string, object>();

		#region -- RegisterCollectionController, UnregisterCollectionController -----------

		private IDEListController FindController(string sId)
		{
			lock (controllerList)
				return controllerList.Find(c => String.Compare(c.Id, sId, StringComparison.OrdinalIgnoreCase) == 0);
		} // func FindController

		/// <summary>Registriert eine Liste an diesem Knoten</summary>
		/// <param name="controller">Liste die dem Controller zugeordnet werden soll.</param>
		/// <param name="publish">Soll die Liste für den Client sichtbar gestaltet werden.</param>
		/// <returns></returns>
		/// <remarks>Diese Methode erstellt eine Liste mit einem Standard-List-Controller, sofern er nicht implementiert wurde.</remarks>
		[LuaMember("RegisterList")]
		public IDEListController RegisterList(string id, IDEListController controller, bool publish = false)
		{
			lock (controllerList)
			{
				if (controller == null)
						throw new ArgumentNullException("list", "Keine Liste gefunden.");
				if (FindController(id) != null)
					throw new ArgumentException(String.Format("Collection '{0}' ist schon registriert.", id));

				// Füge den Controller ein
				controllerList.Add(controller);

				// Veröffentlichen
				if (publish)
					PublishItem(controller);

				return controller;
			}
		} // func RegisterList

		public void PublishItem(object item)
		{
			IDEListController controller;
			DEConfigItemPublicAction action;
			DEConfigItemPublicPanel panel;
			string sId;

			if ((controller = item as IDEListController) != null)
				sId = controller.Id;
			else if ((action = item as DEConfigItemPublicAction) != null)
				sId = action.ActionId;
			else if ((panel = item as DEConfigItemPublicPanel) != null)
				sId = panel.Id;
			else
				throw new ArgumentException("item");

			lock (publishedItems)
				publishedItems[sId] = item;
		} // proc PublishItem

		[LuaMember("PublishPanel")]
		private void LuaPublishPanel(string sId, string sUri, LuaTable t)
		{
			var configPanel = new DEConfigItemPublicPanel(sId, sUri);
			if (t != null)
				t.SetObjectMember(configPanel);

			PublishItem(configPanel);
		} // proc LuaPublishPanel

		[LuaMember("PublishAction")]
		private void LuaPublishAction(string sAction, LuaTable t)
		{
			var configAction = new DEConfigItemPublicAction(sAction);
			if (t != null)
				t.SetObjectMember(configAction);

			PublishItem(configAction);
		} // proc LuaPublishAction

		/// <summary>Wird durch den List-Controller aufgerufen, wenn er zerstört wird.</summary>
		/// <param name="controller"></param>
		public void UnregisterList(IDEListController controller)
		{
			lock (controllerList)
			{
				controllerList.Remove(controller);
				lock (publishedItems)
				{
					object item;
					if(publishedItems.TryGetValue(controller.Id, out item) && item == controller)
						publishedItems.Remove(controller.Id);
				}
			}
		} // proc UnregisterList

		#endregion

		#region -- Controller Http --------------------------------------------------------

		private XElement GetControllerXmlNode(string sId, object item)
		{
			IDEListController configController = item as IDEListController;
			DEConfigItemPublicAction configAction;
			DEConfigItemPublicPanel configPanel;
			if (configController != null)
			{
				var indexAccess = configController.List as IList;
				XElement x = new XElement("list",
					new XAttribute("id", configController.Id),
					Procs.XAttributeCreate("displayname", configController.DisplayName)
				);

				if (indexAccess != null)
					x.Add(new XAttribute("count", indexAccess.Count));

				return x;
			}
			else if ((configAction = item as DEConfigItemPublicAction) != null)
			{
				XElement x = new XElement("action",
					new XAttribute("id", configAction.ActionId),
					Procs.XAttributeCreate("displayname", configAction.DisplayName)
				);
				return x;
			}
			else if ((configPanel = item as DEConfigItemPublicPanel) != null)
			{
				XElement x = new XElement("panel",
					new XAttribute("id", configPanel.Id),
					Procs.XAttributeCreate("displayname", configPanel.DisplayName),
					new XText(configPanel.Uri)
				);
				return x;
			}
			else
				return null;
		} // func GetControllerXmlNode

		[
		DEConfigHttpAction("list", SecurityToken = SecuritySys),
		Description("Gibt die Konfigurationsstruktur des Servers zurück.")
		]
		private XElement HttpListAction(bool recursive = true)
		{
			// Aktuelle Element anlegen
			XElement x = new XElement("item",
				new XAttribute("name", Name),
				new XAttribute("displayname", DisplayName),
				new XAttribute("icon", Icon ?? Config.GetAttribute("icon", "/images/config.png"))
			);

			// Füge die entsprechenden Collections
			lock (publishedItems)
			{
				foreach (var cur in publishedItems)
					x.Add(GetControllerXmlNode(cur.Key, cur.Value));
			}

			// Füge die untergeordneten Knoten an
			if (recursive)
			{
				foreach (var c in subItems)
					x.Add(c.HttpListAction(recursive));
			}

			return x;
		} // func HttpListAction

		[
		DEConfigHttpAction("listget"),
		Description("Gibt den Inhalt der angegebenen Liste zurück. (optional: desc, template)")
		]
		private void HttpListGetAction(IDEContext r, string id, int start = 0, int count = Int32.MaxValue)
		{
			// Suche den passenden Controller
			var controller = FindController(id);
			if (controller == null)
				throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Liste '{0}' nicht gefunden.", id));

			// check security token
			r.DemandToken(controller.SecurityToken);

			// write list
			((IDEListService)Server).WriteList(r, controller, start, count);
		} // func HttpListGetAction

		#endregion

		// -- Static --------------------------------------------------------------

		#region -- class DEListDescriptorReflectorImpl ------------------------------------

		private sealed class DEListDescriptorReflectorImpl : IDEListDescriptor
		{
			private string sTypeName;
			private List<KeyValuePair<DEListTypePropertyAttribute, PropertyDescriptor>> properties = new List<KeyValuePair<DEListTypePropertyAttribute, PropertyDescriptor>>();

			public DEListDescriptorReflectorImpl(Type typeDescripe)
			{
				var typeProperty = typeDescripe.GetCustomAttribute<DEListTypePropertyAttribute>(true);
				this.sTypeName = typeProperty == null ? typeDescripe.Name : typeProperty.Name;

				// Gibt es eine Methode zur Ausgabe des Items
				int iAttributeBorder = 0;
				int iElementBorder = 0;
				foreach (PropertyDescriptor pi in TypeDescriptor.GetProperties(typeDescripe))
				{
					var attr = (DEListTypePropertyAttribute)pi.Attributes[typeof(DEListTypePropertyAttribute)];
					if (attr != null)
					{
						var cur = new KeyValuePair<DEListTypePropertyAttribute, PropertyDescriptor>(attr, pi);
						if (attr.Name == ".")
							properties.Add(cur);
						else if (attr.Name == "@")
						{
							properties.Insert(iAttributeBorder++, cur);
							iElementBorder++;
						}
						else
							properties.Insert(iElementBorder++, cur);
					}
				}
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType(sTypeName);

				for (int i = 0; i < properties.Count; i++)
					xml.WriteProperty(properties[i].Key.Name, properties[i].Value.PropertyType);

				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				xml.WriteStartProperty(sTypeName);
				for (int i = 0; i < properties.Count; i++)
				{
					string sName = properties[i].Key.Name;
					object value = properties[i].Value.GetValue(item);

					if (sName == ".")
						xml.WriteValue(value);
					else if (sName.StartsWith("@"))
						xml.WriteAttributeProperty(sName.Substring(1), value);
					else
						xml.WriteElementProperty(sName, value);
				}
				xml.WriteEndProperty();
			} // proc WriteItem
		} // class DEListDescriptorReflectorImpl

		#endregion

		/// <summary></summary>
		/// <param name="tw"></param>
		/// <returns></returns>
		public static XmlWriterSettings GetSettings(TextWriter tw)
		{
			var settings = new XmlWriterSettings();
			settings.CloseOutput = true;
			settings.CheckCharacters = true;
			settings.Encoding = tw.Encoding;
			settings.Indent = true;
			settings.IndentChars = "  ";
			settings.NewLineChars = Environment.NewLine;
			settings.NewLineHandling = NewLineHandling.Entitize;
			settings.NewLineOnAttributes = false;
			return settings;
		} // func GetSettings

		public static IDEListDescriptor CreateListDescriptorFromType(Type itemType)
		{
			return new DEListDescriptorReflectorImpl(itemType);
		} // func CreateListDescriptorFromType
	} // class DEConfigItem

	#endregion
}
