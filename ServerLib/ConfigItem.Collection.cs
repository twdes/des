﻿#region -- copyright --
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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEListService -----------------------------------------------

	/// <summary>List-Service für die Darstellung und export der Listen.</summary>
	public interface IDEListService
	{
		/// <summary>Serialisiert die Liste auf den Http-Stream.</summary>
		/// <param name="r">Http response</param>
		/// <param name="controller">Liste</param>
		/// <param name="startAt">Übergebener Startwert.</param>
		/// <param name="count">Anzahl der Elemente die zurückgeliefert werden sollen</param>
		void WriteList(IDEWebRequestScope r, IDEListController controller, int startAt, int count);
	} // interface IDEListService

	#endregion

	#region -- interface IDEListDescriptor --------------------------------------------

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
	} // interface IDEListDescriptor

	#endregion

	#region -- class DEListTypeWriter -------------------------------------------------

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

		private readonly XmlWriter xml;

		/// <summary></summary>
		/// <param name="xml"></param>
		public DEListTypeWriter(XmlWriter xml)
		{
			this.xml = xml;
		} // ctor

		/// <summary>Start a new type.</summary>
		/// <param name="typeName">Name of the type.</param>
		public void WriteStartType(string typeName)
		{
			if (inType)
				throw new InvalidOperationException("Nested types are not allowed.");

			xml.WriteStartElement(typeName);
			inType = true;
		} // proc StartType

		/// <summary>End the current type.</summary>
		public void WriteEndType()
		{
			inType = false;
			xml.WriteEndElement();
		} // proc WriteEndType

		/// <summary>Write the property.</summary>
		/// <param name="propertyName">Erzeugt ein Element, welches einen Type enthalten kann. Attribute sind nicht erlaubt</param>
		/// <param name="typeName"></param>
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

			var typeName = LuaType.GetType(type).AliasOrFullName;

			if (propertyName == ".")
				WriteProperty(PropertyType.Value, String.Empty, typeName);
			else if (propertyName.StartsWith("@"))
				WriteProperty(PropertyType.Attribute, propertyName.Substring(1), typeName);
			else
				WriteProperty(PropertyType.Element, propertyName, typeName);
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
			for (var i = 0; i < propertyName.Length; i++)
			{
				if (!Char.IsLetterOrDigit(propertyName[i]) && propertyName[i] != '.')
					throw new ArgumentException("Invalid xml name.");
			}
		} // func CheckXmlName
	} // class DEListTypeWriter

	#endregion

	#region -- class DEListItemWriter -------------------------------------------------

	/// <summary>List writer implementation.</summary>
	public sealed class DEListItemWriter
	{
		private readonly static char[] isCDateEmitChar = new char[] { '<', '>', '\n', '\t', '/' };
		private readonly XmlWriter xml;

		/// <summary></summary>
		/// <param name="xml"></param>
		public DEListItemWriter(XmlWriter xml)
		{
			this.xml = xml;
		} // ctor

		/// <summary>Write property.</summary>
		/// <param name="propertyName"></param>
		public void WriteStartProperty(string propertyName)
			=> xml.WriteStartElement(propertyName);

		/// <summary>Write value.</summary>
		/// <param name="_value"></param>
		public void WriteValue(object _value)
		{
			var value = _value.ChangeType<string>();
			if (value == null)
				return;

			if (value.IndexOfAny(isCDateEmitChar) >= 0) // check for specials
				xml.WriteCData(Procs.RemoveInvalidXmlChars(value, '?'));
			else
				xml.WriteValue(Procs.RemoveInvalidXmlChars(value, '?'));
		} // proc WriteValue

		/// <summary>Write the end of an property.</summary>
		public void WriteEndProperty()
			=> xml.WriteEndElement();

		/// <summary>Write a complete property.</summary>
		/// <param name="propertyName"></param>
		/// <param name="_value"></param>
		public void WriteElementProperty(string propertyName, object _value)
		{
			WriteStartProperty(propertyName);
			WriteValue(_value);
			WriteEndProperty();
		} // proc WriteElementProperty

		/// <summary>Write a complete attribute.</summary>
		/// <param name="propertyName"></param>
		/// <param name="_value"></param>
		public void WriteAttributeProperty(string propertyName, object _value)
		{
			var value = _value.ChangeType<string>();
			if (value != null)
				xml.WriteAttributeString(propertyName, Procs.RemoveInvalidXmlChars(value, '?'));
		} // proc WriteAttributeProperty

		/// <summary>Write a complete property.</summary>
		/// <param name="propertyName"></param>
		/// <param name="value"></param>
		public void WriteProperty(string propertyName, object value)
		{
			if (propertyName == ".")
				WriteValue(value);
			else if (propertyName.StartsWith("@"))
				WriteAttributeProperty(propertyName.Substring(1), value);
			else
				WriteElementProperty(propertyName, value);
		} // proc WriteProperty
	} // class DEListItemWriter

	#endregion

	#region -- interface IDEListController --------------------------------------------

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
		void OnBeforeList();

		/// <summary>Bezeichner der Liste</summary>
		string Id { get; }
		/// <summary>Anzeigename der Liste</summary>
		string DisplayName { get; }

		/// <summary>Daten der Liste</summary>
		IEnumerable List { get; }

		/// <summary>Access token to this list.</summary>
		string SecurityToken { get; }

		/// <summary>Wie soll die Liste aufgegeben werden.</summary>
		IDEListDescriptor Descriptor { get; }
	} // interface IDEListController

	#endregion

	#region -- class DEListControllerBase ---------------------------------------------

	/// <summary>Implementiert einen Controller für eine Liste.</summary>
	public abstract class DEListControllerBase : IDEListController
	{
		private ReaderWriterLockSlim listLock;
		private readonly DEConfigItem configItem;
		private readonly string id;
		private readonly string displayName;
		private readonly IDEListDescriptor descriptor;

		#region -- Ctor/Dtor ------------------------------------------------------------

		/// <summary></summary>
		/// <param name="configItem"></param>
		/// <param name="descriptor"></param>
		/// <param name="id"></param>
		/// <param name="displayName"></param>
		public DEListControllerBase(DEConfigItem configItem, IDEListDescriptor descriptor, string id, string displayName)
		{
			this.configItem = configItem;
			this.id = id;
			this.displayName = displayName;
			this.descriptor = descriptor;
			this.listLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

			configItem.RegisterList(id, this);
		} // ctor

		/// <summary></summary>
		~DEListControllerBase()
		{
			Dispose(false);
		} // ctor

		/// <summary></summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		} // proc Dispose

		/// <summary></summary>
		/// <param name="disposing"></param>
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

		#region -- EnterReadLock, EnterWriteLock ----------------------------------------

		/// <summary>Enter read access to this list.</summary>
		/// <returns></returns>
		public IDisposable EnterReadLock()
		{
			if (listLock == null)
				return null;

			if (listLock.IsWriteLockHeld)
				return null;
			else
			{
				listLock.EnterReadLock();
				return new DisposableScope(listLock.ExitReadLock);
			}
		} // func EnterReadLock

		/// <summary>Enter write access to this list.</summary>
		/// <returns></returns>
		public IDisposable EnterWriteLock()
		{
			if (listLock == null)
				return null;

			listLock.EnterWriteLock();
			return new DisposableScope(listLock.ExitWriteLock);
		} // func EnterWriteLock

		#endregion

		/// <summary></summary>
		public virtual void OnBeforeList()
		{
		} // proc OnBeforeList

		/// <summary>Id of the list.</summary>
		public string Id => id;
		/// <summary>Display name of the list.</summary>
		public virtual string DisplayName => displayName;
		/// <summary>Security access to this list.</summary>
		public virtual string SecurityToken => DEConfigItem.SecuritySys;
		/// <summary>List access.</summary>
		public abstract IEnumerable List { get; }
		/// <summary>List descriptor.</summary>
		public IDEListDescriptor Descriptor => descriptor;

		/// <summary>List config item assignment.</summary>
		public DEConfigItem ConfigItem => configItem;
	} // class DEListControllerBase

	#endregion

	#region -- interface IDERangeEnumerable -------------------------------------------

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

	#region -- interface IDERangeEnumerable2 ------------------------------------------

	/// <summary>Enumerator for pagination.</summary>
	public interface IDERangeEnumerable2<T> : IDERangeEnumerable<T>
	{
		/// <summary></summary>
		/// <param name="start"></param>
		/// <param name="count"></param>
		/// <param name="selector"></param>
		/// <returns></returns>
		IEnumerator<T> GetEnumerator(int start, int count, IPropertyReadOnlyDictionary selector);
	} // interface IDERangeEnumerable2

	#endregion

	#region -- class DEEnumerator<T> --------------------------------------------------

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

	#region -- class DEList<T> --------------------------------------------------------

	/// <summary>Liste mit speziellen Eigenschaften:
	/// - Schreibzugriff sperrt liste, multiples Lesen ist möglich.
	/// - Erzeugt einen Zugriff im Log</summary>
	public sealed class DEList<T> : DEListControllerBase, IList<T>
	{
		private readonly List<T> innerList = new List<T>();

		#region -- Ctor/Dtor ----------------------------------------------------------

		/// <summary></summary>
		/// <param name="configItem"></param>
		/// <param name="id"></param>
		/// <param name="displayName"></param>
		public DEList(DEConfigItem configItem, string id, string displayName)
			: base(configItem, DEConfigItem.CreateListDescriptorFromType(typeof(T)), id, displayName)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					using (EnterWriteLock())
						innerList.Clear();
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		#region -- Liste --------------------------------------------------------------

		/// <summary></summary>
		/// <param name="item"></param>
		public void Add(T item)
		{
			using (EnterWriteLock())
				innerList.Add(item);
		} // proc Add

		/// <summary></summary>
		/// <param name="items"></param>
		public void AddRange(IEnumerable<T> items)
		{
			using (EnterWriteLock())
				innerList.AddRange(items);
		} // proc AddRange

		/// <summary></summary>
		/// <param name="predicate"></param>
		/// <returns></returns>
		public int FindIndex(Predicate<T> predicate)
		{
			using (EnterReadLock())
				return innerList.FindIndex(predicate);
		} // func IDEList<T>.FindIndex

		/// <summary></summary>
		/// <param name="item"></param>
		/// <returns></returns>
		public bool Remove(T item)
		{
			using (EnterWriteLock())
				return innerList.Remove(item);
		} // func Remove

		/// <summary></summary>
		public void Clear()
		{
			using (EnterWriteLock())
				innerList.Clear();
		} // proc Clear

		/// <summary></summary>
		/// <param name="item"></param>
		/// <returns></returns>
		public bool Contains(T item)
		{
			using (EnterReadLock())
				return innerList.Contains(item);
		} // func Contains

		/// <summary></summary>
		/// <param name="array"></param>
		/// <param name="arrayIndex"></param>
		public void CopyTo(T[] array, int arrayIndex)
		{
			using (EnterReadLock())
				innerList.CopyTo(array, arrayIndex);
		} // proc CopyTo

		/// <summary></summary>
		/// <param name="index"></param>
		/// <param name="item"></param>
		public void Insert(int index, T item)
		{
			using (EnterWriteLock())
				innerList.Insert(index, item);
		} // proc Insert

		/// <summary></summary>
		/// <param name="index"></param>
		public void RemoveAt(int index)
		{
			using (EnterWriteLock())
				innerList.RemoveAt(index);
		} // proc RemoveAt

		/// <summary></summary>
		/// <param name="item"></param>
		/// <returns></returns>
		public int IndexOf(T item)
		{
			using (EnterReadLock())
				return innerList.IndexOf(item);
		} // func IndexOf

		/// <summary></summary>
		/// <returns></returns>
		public IEnumerator<T> GetEnumerator() => new DEEnumerator<T>(this, innerList);
		IEnumerator IEnumerable.GetEnumerator() => new DEEnumerator<T>(this, innerList);

		/// <summary></summary>
		/// <param name="index"></param>
		/// <returns></returns>
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

		/// <summary></summary>
		public int Count
		{
			get
			{
				using (EnterReadLock())
					return innerList.Count;
			}
		} // prop Count

		/// <summary></summary>
		public bool IsReadOnly => false;

		/// <summary></summary>
		public override IEnumerable List => innerList;

		#endregion
	} // class ConfigItemCollection

	#endregion

	#region -- class DEDictionary<TKey, TItem> ----------------------------------------

	/// <summary>Dictionary implementation for safe access.</summary>
	public sealed class DEDictionary<TKey, TItem> : DEListControllerBase, IDictionary<TKey, TItem>
	{
		private readonly IDictionary<TKey, TItem> innerDictionary;

		private DEDictionary(DEConfigItem configItem, string id, string displayName, IDEListDescriptor listDescriptor, IDictionary<TKey, TItem> innerDictionary)
			: base(configItem, listDescriptor, id, displayName)
		{
			this.innerDictionary = innerDictionary;
		} // ctor

		/// <summary></summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					using (EnterWriteLock())
						innerDictionary.Clear();
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		/// <summary></summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		public void Add(TKey key, TItem value)
		{
			using (EnterWriteLock())
				innerDictionary.Add(key, value);
		} // proc Add

		void ICollection<KeyValuePair<TKey, TItem>>.Add(KeyValuePair<TKey, TItem> item)
		{
			using (EnterWriteLock())
				innerDictionary.Add(item.Key, item.Value);
		} // proc ICollection<KeyValuePair<TKey, TItem>>.Add

		/// <summary></summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool ContainsKey(TKey key)
		{
			using (EnterReadLock())
				return innerDictionary.ContainsKey(key);
		} // func ContainsKey

		/// <summary></summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool TryGetValue(TKey key, out TItem value)
		{
			using (EnterReadLock())
				return innerDictionary.TryGetValue(key, out value);
		} // func TryGetValue

		/// <summary></summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool Remove(TKey key)
		{
			using (EnterWriteLock())
				return innerDictionary.Remove(key);
		} // func Remove

		/// <summary></summary>
		public void Clear()
		{
			using (EnterWriteLock())
				innerDictionary.Clear();
		} // proc Clear

		void ICollection<KeyValuePair<TKey, TItem>>.CopyTo(KeyValuePair<TKey, TItem>[] array, int arrayIndex)
		{
			using (EnterReadLock())
				innerDictionary.CopyTo(array, arrayIndex);
		} // func ICollection<KeyValuePair<TKey, TItem>>.CopyTo

		bool ICollection<KeyValuePair<TKey, TItem>>.Remove(KeyValuePair<TKey, TItem> item)
		{
			using (EnterWriteLock())
				return innerDictionary.Remove(item);
		} // func ICollection<KeyValuePair<TKey, TItem>>.Remove

		bool ICollection<KeyValuePair<TKey, TItem>>.Contains(KeyValuePair<TKey, TItem> item)
		{
			using (EnterReadLock())
				return innerDictionary.Contains(item);
		} // ICollection<KeyValuePair<TKey, TItem>>.Contains

		ICollection<TKey> IDictionary<TKey, TItem>.Keys => throw new NotSupportedException();
		ICollection<TItem> IDictionary<TKey, TItem>.Values => throw new NotSupportedException();

		IEnumerator IEnumerable.GetEnumerator()
			=> new DEEnumerator<KeyValuePair<TKey, TItem>>(this, innerDictionary);

		IEnumerator<KeyValuePair<TKey, TItem>> IEnumerable<KeyValuePair<TKey, TItem>>.GetEnumerator()
			=> new DEEnumerator<KeyValuePair<TKey, TItem>>(this, innerDictionary);

		bool ICollection<KeyValuePair<TKey, TItem>>.IsReadOnly => false;

		/// <summary></summary>
		/// <param name="key"></param>
		/// <returns></returns>
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

		/// <summary></summary>
		public int Count
		{
			get
			{
				using (EnterReadLock())
					return innerDictionary.Count;
			}
		} // prop Count

		/// <summary></summary>
		public override IEnumerable List => innerDictionary;

		/// <summary></summary>
		/// <param name="configItem"></param>
		/// <param name="id"></param>
		/// <param name="displayName"></param>
		/// <param name="listDescriptor"></param>
		/// <param name="comparer"></param>
		/// <returns></returns>
		public static DEDictionary<TKey, TItem> CreateDictionary(DEConfigItem configItem, string id, string displayName, IDEListDescriptor listDescriptor = null, IEqualityComparer<TKey> comparer = null)
		{
			return new DEDictionary<TKey, TItem>(configItem, id, displayName,
				listDescriptor ?? DEConfigItem.CreateListDescriptorFromType(typeof(KeyValuePair<TKey, TItem>)),
				new Dictionary<TKey, TItem>(comparer ?? EqualityComparer<TKey>.Default)
			);
		} // func CreateDictionary

		/// <summary></summary>
		/// <param name="configItem"></param>
		/// <param name="id"></param>
		/// <param name="displayName"></param>
		/// <param name="listDescriptor"></param>
		/// <param name="comparer"></param>
		/// <returns></returns>
		public static DEDictionary<TKey, TItem> CreateSortedList(DEConfigItem configItem, string id, string displayName, IDEListDescriptor listDescriptor = null, IComparer<TKey> comparer = null)
		{
			return new DEDictionary<TKey, TItem>(configItem, id, displayName,
				listDescriptor ?? DEConfigItem.CreateListDescriptorFromType(typeof(KeyValuePair<TKey, TItem>)),
				new SortedDictionary<TKey, TItem>(comparer ?? Comparer<TKey>.Default)
			);
		} // func CreateSortedList
	} // class DEDictionary

	#endregion

	#region -- class DEListTypePropertyAttribute --------------------------------------

	/// <summary>Markiert eine Eigenschaft für den automatischen Export.</summary>
	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Interface)]
	public sealed class DEListTypePropertyAttribute : Attribute
	{
		/// <summary></summary>
		/// <param name="name"></param>
		public DEListTypePropertyAttribute(string name)
		{
			if (String.IsNullOrWhiteSpace(name))
				throw new ArgumentNullException(nameof(name));

			Name = name.Trim();
		} // ctor

		/// <summary>Name of the property.</summary>
		public string Name { get; }
	} // class DEListTypePropertyAttribute

	#endregion

	#region -- class DEConfigItemPublicAction -----------------------------------------

	/// <summary>Marks a public action of an configuration note.</summary>
	public sealed class DEConfigItemPublicAction
	{
		/// <summary></summary>
		/// <param name="actionId"></param>
		public DEConfigItemPublicAction(string actionId)
		{
			if (String.IsNullOrWhiteSpace(actionId))
				throw new ArgumentNullException(nameof(actionId));

			ActionId = actionId;
		} // ctor

		/// <summary>Id of the action.</summary>
		public string ActionId { get; }
		/// <summary>Display name of the action</summary>
		public string DisplayName { get; set; }
	} // class DEConfigItemPublicAction

	#endregion

	#region -- class DEConfigItem -----------------------------------------------------

	public partial class DEConfigItem
	{
		private readonly List<IDEListController> controllerList = new List<IDEListController>();
		private readonly Dictionary<string, object> publishedItems = new Dictionary<string, object>();

		#region -- RegisterCollectionController, UnregisterCollectionController -------

		private IDEListController FindController(string id, bool recursive)
		{
			IDEListController controller;
			lock (controllerList)
				controller = controllerList.Find(c => String.Compare(c.Id, id, StringComparison.OrdinalIgnoreCase) == 0);

			if (controller == null && recursive && Owner is DEConfigItem configItem)
				controller = configItem.FindController(id, true);

			return controller;
		} // func FindController

		/// <summary>Registriert eine Liste an diesem Knoten</summary>
		/// <param name="id"></param>
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
				if (FindController(id, false) != null)
					throw new ArgumentException(String.Format("Collection '{0}' ist schon registriert.", id));

				// Füge den Controller ein
				controllerList.Add(controller);

				// Veröffentlichen
				if (publish)
					PublishItem(controller);

				return controller;
			}
		} // func RegisterList

		/// <summary></summary>
		/// <param name="item"></param>
		public void PublishItem(object item)
		{
			string id;

			switch (item)
			{
				case IDEListController controller:
					id = controller.Id;
					break;
				case DEConfigItemPublicAction action:
					id = action.ActionId;
					break;
				default:
					throw new ArgumentException(nameof(item));
			}

			lock (publishedItems)
				publishedItems[id] = item;
		} // proc PublishItem

		/// <summary></summary>
		/// <param name="action"></param>
		/// <param name="t"></param>
		[LuaMember("PublishAction")]
		public void LuaPublishAction(string action, LuaTable t)
		{
			var configAction = new DEConfigItemPublicAction(action);
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
					if (publishedItems.TryGetValue(controller.Id, out var item) && item == controller)
						publishedItems.Remove(controller.Id);
				}
			}
		} // proc UnregisterList

		#endregion

		#region -- Controller Http ----------------------------------------------------

		private XElement GetControllerXmlNode(string sId, object item)
		{
			if (item is IDEListController configController)
			{
				var x = new XElement("list",
					new XAttribute("id", configController.Id),
					Procs.XAttributeCreate("displayname", configController.DisplayName)
				);

				if (configController.List is IList indexAccess)
					x.Add(new XAttribute("count", indexAccess.Count));

				return x;
			}
			else if (item is DEConfigItemPublicAction configAction)
			{
				var x = new XElement("action",
					new XAttribute("id", configAction.ActionId),
					Procs.XAttributeCreate("displayname", configAction.DisplayName)
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
		private XElement HttpListAction(bool recursive = true, bool published = true, int rlevel = Int32.MaxValue)
		{
			recursive = recursive || (rlevel != Int32.MaxValue);

			// Aktuelle Element anlegen
			var x = new XElement("item",
				new XAttribute("name", Name),
				new XAttribute("displayname", DisplayName),
				new XAttribute("icon", Config.GetAttribute("icon", Icon)),
				this is DEConfigLogItem l && l.HasLog ? new XAttribute("hasLog", true) : null
			);

			// Füge die entsprechenden Collections
			if (published)
			{
				lock (publishedItems)
				{
					foreach (var cur in publishedItems)
						x.Add(GetControllerXmlNode(cur.Key, cur.Value));
				}
			}

			// Füge die untergeordneten Knoten an
			if (recursive && rlevel > 0)
			{
				foreach (var c in subItems)
					x.Add(c.HttpListAction(recursive, published, rlevel - 1));
			}

			return x;
		} // func HttpListAction

		[
		DEConfigHttpAction("listget"),
		Description("Gibt den Inhalt der angegebenen Liste zurück. (optional: desc, template)")
		]
		private void HttpListGetAction(IDEWebRequestScope r, string id, int start = 0, int count = Int32.MaxValue)
		{
			// Suche den passenden Controller
			var controller = FindController(id, true);
			if (controller == null)
				throw new HttpResponseException(HttpStatusCode.BadRequest, $"List'{id}' not found.");

			// check security token
			r.DemandToken(controller.SecurityToken);

			// write list
			((IDEListService)Server).WriteList(r, controller, start, count);
		} // func HttpListGetAction

		#endregion

		// -- Static ----------------------------------------------------------

		#region -- class DEListDescriptorReflectedProperty ----------------------------

		[DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "(),nq}")]
		private sealed class DEListDescriptorReflectedProperty
		{
			private readonly DEListTypePropertyAttribute attribute;
			private readonly PropertyDescriptor propertyDescriptor;

			public DEListDescriptorReflectedProperty(DEListTypePropertyAttribute attribute, PropertyDescriptor propertyDescriptor)
			{
				this.attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
				this.propertyDescriptor = propertyDescriptor ?? throw new ArgumentNullException(nameof(propertyDescriptor));
			} // ctor

			private string GetDebuggerDisplay()
				=> $"{attribute.Name} : {propertyDescriptor.PropertyType} = {propertyDescriptor.Name}";

			private object GetValueSafe(object item)
			{
				try
				{
					return propertyDescriptor.GetValue(item);
				}
				catch
				{
					return null;
				}
			} // func GetValueSafge

			public void WriteType(DEListTypeWriter xml)
				=> xml.WriteProperty(attribute.Name, propertyDescriptor.PropertyType);

			public void WriteProperty(DEListItemWriter xml, object item)
				=> xml.WriteProperty(attribute.Name, GetValueSafe(item));
		} // class DEListDescriptorReflectedProperty

		#endregion

		#region -- class DEListDescriptorReflectorImpl --------------------------------

		private sealed class DEListDescriptorReflectorImpl : IDEListDescriptor
		{
			private readonly string typeName;
			private readonly DEListDescriptorReflectedProperty[] properties;

			public DEListDescriptorReflectorImpl(Type itemType)
			{
				typeName = GetItemTypeName(itemType);
				properties = GetItemTypeProperties(itemType);
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType(typeName);

				for (var i = 0; i < properties.Length; i++)
					properties[i].WriteType(xml);

				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				xml.WriteStartProperty(typeName);
				for (var i = 0; i < properties.Length; i++)
					properties[i].WriteProperty(xml, item);
				xml.WriteEndProperty();
			} // proc WriteItem
		} // class DEListDescriptorReflectorImpl

		#endregion

		#region -- class DEListDescriptorReflectorImpl --------------------------------

		private sealed class DEDictionaryDescriptorReflectorImpl : IDEListDescriptor
		{
			private readonly string typeName;
			private readonly PropertyInfo keyProperty;
			private readonly PropertyInfo itemProperty;
			private readonly DEListDescriptorReflectedProperty[] properties;

			public DEDictionaryDescriptorReflectorImpl(Type dictionaryType)
			{
				keyProperty = dictionaryType.GetProperty(nameof(KeyValuePair<object, object>.Key)) ?? throw new ArgumentNullException(nameof(keyProperty));
				itemProperty = dictionaryType.GetProperty(nameof(KeyValuePair<object, object>.Value)) ?? throw new ArgumentNullException(nameof(itemProperty));

				typeName = GetItemTypeName(itemProperty.PropertyType);
				properties = GetItemTypeProperties(itemProperty.PropertyType);
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType(typeName);

				xml.WriteProperty("@key", keyProperty.PropertyType);

				for (var i = 0; i < properties.Length; i++)
					properties[i].WriteType(xml);

				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				xml.WriteStartProperty(typeName);
				xml.WriteProperty("@key", keyProperty.GetValue(item));
				var itemValue = itemProperty.GetValue(item);
				for (var i = 0; i < properties.Length; i++)
					properties[i].WriteProperty(xml, itemValue);
				xml.WriteEndProperty();
			} // proc WriteItem
		} // class DEDictionaryDescriptorReflectorImpl

		#endregion

		/// <summary></summary>
		/// <param name="tw"></param>
		/// <returns></returns>
		public static XmlWriterSettings GetSettings(TextWriter tw)
		{
			return new XmlWriterSettings
			{
				CloseOutput = true,
				CheckCharacters = true,
				Encoding = tw.Encoding,
				Indent = true,
				IndentChars = "  ",
				NewLineChars = Environment.NewLine,
				NewLineHandling = NewLineHandling.Entitize,
				NewLineOnAttributes = false
			};
		} // func GetSettings

		private static string GetItemTypeName(Type itemType)
		{
			var typeProperty = itemType.GetCustomAttribute<DEListTypePropertyAttribute>(true);
			return typeProperty == null ? itemType.Name : typeProperty.Name;
		} // func GetItemTypeName

		private static DEListDescriptorReflectedProperty[] GetItemTypeProperties(Type itemType)
		{
			var properties = new List<DEListDescriptorReflectedProperty>();
			
			var attributeBorder = 0;
			var elementBorder = 0;
			foreach (var pi in TypeDescriptor.GetProperties(itemType).Cast<PropertyDescriptor>())
			{
				var attr = (DEListTypePropertyAttribute)pi.Attributes[typeof(DEListTypePropertyAttribute)];
				if (attr != null)
				{
					var cur = new DEListDescriptorReflectedProperty(attr, pi);
					if (attr.Name == ".")
						properties.Add(cur);
					else if (attr.Name[0] == '@')
					{
						properties.Insert(attributeBorder++, cur);
						elementBorder++;
					}
					else
						properties.Insert(elementBorder++, cur);
				}
			}

			return properties.ToArray();
		} // func GetItemTypeProperties

		/// <summary>Create a list descriptor for the givven type.</summary>
		/// <param name="itemType"></param>
		/// <returns></returns>
		public static IDEListDescriptor CreateListDescriptorFromType(Type itemType)
		{
			if (itemType.IsGenericType && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) // dictionary type
				return new DEDictionaryDescriptorReflectorImpl(itemType);
			else
				return new DEListDescriptorReflectorImpl(itemType);
		}
	} // class DEConfigItem

	#endregion
}
