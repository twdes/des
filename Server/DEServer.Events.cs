using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TecWare.DE.Server.Http;

namespace TecWare.DE.Server
{
	internal partial class DEServer : IDEListService
	{
		#region -- class FiredEvent -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class FiredEvent
		{
			private int iLastFired;
			private int iRevision;

			private string sPath;
			private string sEvent;
			private string sIndex;
			private XElement values;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public FiredEvent(int iRevision, string sPath, string sEvent, string sIndex, XElement values)
			{
				if (String.IsNullOrEmpty(sPath))
					throw new ArgumentNullException("path");
				if (String.IsNullOrEmpty(sEvent))
					throw new ArgumentNullException("event");
				if (String.IsNullOrEmpty(sIndex))
					sIndex = String.Empty;

				this.sPath = sPath;
				this.sEvent = sEvent;
				this.sIndex = sIndex;

				Reset(iRevision, values);
			} // ctor

			public override bool Equals(object obj)
			{
				FiredEvent other = obj as FiredEvent;
				if (other != null)
					return IsEqual(other.Path, other.Event, other.Index);
				else
					return base.Equals(obj);
			} // func Equals

			public override int GetHashCode()
			{
				return sPath.GetHashCode() | sEvent.GetHashCode() | sIndex.GetHashCode();
			} // func GetHashCode

			#endregion

			public void Reset(int iRevision, XElement values)
			{
				this.iLastFired = Environment.TickCount;
				this.iRevision = iRevision;
				this.values = values;
			} // proc Reset

			/// <summary>Formuliert das Event.</summary>
			/// <returns>Xml-Fragment für dieses Event</returns>
			public XElement GetEvent()
			{
				return new XElement("event",
					new XAttribute("path", sPath),
					new XAttribute("event", sEvent),
					String.IsNullOrEmpty(sIndex) ? null : new XAttribute("index", sIndex),
					values);
			} // func GetEvent

			public bool IsEqual(string sTestPath, string sTestEvent, string sTestIndex)
			{
				return String.Compare(sPath, sTestPath, true) == 0 &&
						String.Compare(sEvent, sTestEvent, true) == 0 &&
						String.Compare(sIndex, sTestIndex, true) == 0;
			} // func IsEqual

			public bool IsActive { get { return Environment.TickCount - iLastFired > 300000; } } // 5min
			public string Path { get { return sPath; } }
			public string Event { get { return sEvent; } }
			public string Index { get { return sIndex; } }
			public int Revision { get { return iRevision; } }
		} // struct FiredEvent

		#endregion

		private int iSendedRevision = 0; // Zuletzt übertragene Revision
		private int iCurrentRevision = 1; // Aktuelle Revision
		private Dictionary<string, FiredEvent> propertyChanged = new Dictionary<string, FiredEvent>(); // Liste mit allen Events, mit ihrer Revision (sortiert nach rev)

		private int iLastEventClean = Environment.TickCount;

		#region -- Events -----------------------------------------------------------------

		public void AppendNewEvent(DEConfigItem item, string sEvent, string sIndex, XElement values = null)
		{
			lock (propertyChanged)
			{
				if (iCurrentRevision == iSendedRevision)
					iCurrentRevision++;

				CleanOutdatedEvents();

				string sConfigPath = item.ConfigPath;
				string sKey = GetEventKey(sConfigPath, sEvent, sIndex);
				FiredEvent ev;
				if (propertyChanged.TryGetValue(sKey, out ev))
					ev.Reset(iCurrentRevision, values);
				else
					propertyChanged[sKey] = new FiredEvent(iCurrentRevision, sConfigPath, sEvent, sIndex, values);
			}
		} // proc AppendNewEvent

		private string GetEventKey(string sPath, string sEvent, string sIndex)
		{
			if (String.IsNullOrEmpty(sPath))
				throw new ArgumentNullException("path");
			if (String.IsNullOrEmpty(sEvent))
				throw new ArgumentNullException("event");
			if (String.IsNullOrEmpty(sIndex))
				sIndex = String.Empty;

			return sPath + ":" + sEvent + "[" + sIndex + "]";
		} // func GetEventKey

		private void CleanOutdatedEvents()
		{
			lock (propertyChanged)
			{
				if (Environment.TickCount - iLastEventClean < 10000)
					return;

				string[] cleanKeys = new string[100];
				int iKeyCount = 0;

				// Suche die Ausgelaufenen Events
				foreach (var cur in propertyChanged)
					if (!cur.Value.IsActive)
					{
						cleanKeys[iKeyCount++] = cur.Key;
						if (iKeyCount >= cleanKeys.Length)
							break;
					}

				// Lösche die Schlüssel
				for (int i = 0; i < iKeyCount; i++)
					propertyChanged.Remove(cleanKeys[i]);

				iLastEventClean = Environment.TickCount;
			}
		} // proc CleanOutdatedEvents

		#endregion

		#region -- IDEListService members -------------------------------------------------

		#region -- enum ListEnumeratorType ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private enum ListEnumeratorType
		{
			Enumerable = 0,
			ReadOnlyList = 1,
			ListTyped = 2,
			RangeEnumerator = 3,
			ListUntyped = 4
		} // enum ListEnumeratorType

		#endregion

		#region -- WriteListCheckRange-----------------------------------------------------

		private static void WriteListCheckRange(XmlWriter xml, ref int iStart, ref int iCount, int iListCount)
		{
			// Prüfe den Start
			if (iStart < 0) // setze von hinten auf
			{
				iStart = iListCount + iStart;
				if (iStart < 0)
					iStart = 0;
			}

			if (iCount < 0) // Anzahl korrigieren
				iCount = 0;
			else if (iStart + iCount > iListCount)
			{
				iCount = iListCount - iStart;
				if (iCount < 0)
					iCount = 0;
			}

			xml.WriteAttributeString("tc", iListCount.ToString());
			xml.WriteAttributeString("s", iStart.ToString());
			xml.WriteAttributeString("c", iCount.ToString());
		} // proc WriteListCheckRange

		#endregion

		#region -- WriteListFetchEnum -----------------------------------------------------

		private void WriteListFetchEnum(XmlWriter xml, IDEListDescriptor descriptor, IEnumerator enumerator, int iStart, int iCount)
		{
			if (iStart < 0 || iCount < 0)
				throw new ArgumentException("start oder count dürfen nicht negativ sein.");

			// Überspringe die ersten
			while (iStart > 0)
			{
				if (!enumerator.MoveNext())
					break;
				iStart--;
			}

			// Gib die Element aus
			while (iCount > 0)
			{
				if (!enumerator.MoveNext())
					break;

				descriptor.WriteItem(new DEListItemWriter(xml), enumerator.Current);
				iCount--;
			}

		} // func WriteListFetchEnum

		#endregion

		#region -- WriteListFetchTyped ----------------------------------------------------

		#region -- class GenericWriter ----------------------------------------------------

		private class GenericWriter
		{
			private static void WriteList<T>(XmlWriter xml, IDEListDescriptor descriptor, IReadOnlyList<T> list, int iStart, int iCount)
			{
				WriteListCheckRange(xml, ref iStart, ref iCount, list.Count);

				if (iCount > 0)
				{
					var end = iStart + iCount;
					for (int i = iStart; i < end; i++)
						descriptor.WriteItem(new DEListItemWriter(xml), (T)list[i]);
				}
			} // proc WriteList

			private static void WriteList<T>(XmlWriter xml, IDEListDescriptor descriptor, IList<T> list, int iStart, int iCount)
			{
				WriteListCheckRange(xml, ref iStart, ref iCount, list.Count);

				if (iCount > 0)
				{
					var end = iStart + iCount;
					for (int i = iStart; i < end; i++)
						descriptor.WriteItem(new DEListItemWriter(xml), (T)list[i]);
				}
			} // proc WriteList

			private static void WriteList<T>(XmlWriter xml, IDEListDescriptor descriptor, IDERangeEnumerable<T> list, int iStart, int iCount)
			{
				WriteListCheckRange(xml, ref iStart, ref iCount, list.Count);

				if (iCount > 0)
				{
					using (var enumerator = list.GetEnumerator(iStart, iCount))
					{
						while (iCount > 0)
						{
							if (!enumerator.MoveNext())
								break;

							descriptor.WriteItem(new DEListItemWriter(xml), enumerator.Current);
							iCount--;
						}
					}
				}
			} // proc WriteList
		} // class GenericWriter

		#endregion

		private void WriteListFetchTyped(Type type, XmlWriter xml, IDEListDescriptor descriptor, object list, int iStart, int iCount)
		{
			Type typeGeneric = type.GetGenericTypeDefinition(); // Hole den generischen Typ ab

			// Suche die passende procedure
			var miWriteList = typeof(GenericWriter).GetTypeInfo().DeclaredMethods.Where(mi => mi.IsStatic && mi.Name == "WriteList" && mi.IsGenericMethodDefinition && mi.GetParameters()[2].ParameterType.Name == typeGeneric.Name).FirstOrDefault();
			if (miWriteList == null)
				throw new ArgumentNullException("writelist", String.Format("Keinen generische Implementierung gefunden ({0}).", typeGeneric.FullName));

			// Aufruf des Writers
			Type typeDelegate = typeof(Action<,,,,>).MakeGenericType(typeof(XmlWriter), typeof(IDEListDescriptor), type, typeof(int), typeof(int));
			MethodInfo miWriteListTyped = miWriteList.MakeGenericMethod(type.GetTypeInfo().GenericTypeArguments[0]);
			Delegate dlg = Delegate.CreateDelegate(typeDelegate, miWriteListTyped);
			dlg.DynamicInvoke(xml, descriptor, list, iStart, iCount);
		} // proc WriteListFetchListTyped

		#endregion

		#region -- WriteListFetchList -----------------------------------------------------

		private void WriteListFetchList(XmlWriter xml, IDEListDescriptor descriptor, IList list, int iStart, int iCount)
		{
			WriteListCheckRange(xml, ref iStart, ref iCount, list.Count);

			if (iCount > 0)
			{
				var end = iStart + iCount;
				for (int i = iStart; i < end; i++)
					descriptor.WriteItem(new DEListItemWriter(xml), list[i]);
			}
		} // proc WriteListFetchList

		#endregion

		void IDEListService.WriteList(IDEHttpContext r, IDEListController controller, int iStart, int iCount)
		{
			bool lSendTypeDefinition = String.Compare(r.GetProperty("desc", Boolean.FalseString), Boolean.TrueString, StringComparison.OrdinalIgnoreCase) == 0;

			// Suche den passenden Descriptor
			IDEListDescriptor descriptor = controller.Descriptor;
			if (descriptor == null)
				throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Liste '{0}' besitzt kein Format.", controller.Id));

			controller.OnBeforeList();

			// Rückgabe
			using (TextWriter tw = r.GetOutputTextWriter("text/xml"))
			using (XmlWriter xml = XmlWriter.Create(tw, GetSettings(tw)))
			{
				xml.WriteStartDocument();
				xml.WriteStartElement("list");

				// Sollen die Strukturinformationen übertragen werdem
				if (lSendTypeDefinition)
				{
					xml.WriteStartElement("typedef");
					descriptor.WriteType(new DEListTypeWriter(xml));
					xml.WriteEndElement();
				}

				// Gib die Daten aus
				using (controller.EnterReadLock())
				{
					var list = controller.List;

					// Prüfe auf Indexierte Listen
					ListEnumeratorType useInterface = ListEnumeratorType.Enumerable;
					Type useInterfaceType = null;
					foreach (var ii in list.GetType().GetTypeInfo().ImplementedInterfaces)
					{
						if (ii.IsGenericType)
						{
							Type genericType = ii.GetGenericTypeDefinition();
							if (genericType == typeof(IList<>))
							{
								if (useInterface < ListEnumeratorType.ListTyped)
								{
									useInterface = ListEnumeratorType.ListTyped;
									useInterfaceType = ii;
								}
							}
							else if (genericType == typeof(IReadOnlyList<>))
							{
								if (useInterface < ListEnumeratorType.ReadOnlyList)
								{
									useInterface = ListEnumeratorType.ReadOnlyList;
									useInterfaceType = ii;
								}
							}
							else if (genericType == typeof(IDERangeEnumerable<>))
							{
								if (useInterface < ListEnumeratorType.RangeEnumerator)
								{
									useInterface = ListEnumeratorType.RangeEnumerator;
									useInterfaceType = ii;
								}
							}
						}
						else if (ii == typeof(System.Collections.IList))
						{
							if (useInterface < ListEnumeratorType.ListUntyped)
								useInterface = ListEnumeratorType.ListUntyped;
						}
					}

					// Gib die entsprechende Liste aus
					xml.WriteStartElement("items");
					switch (useInterface)
					{
						case ListEnumeratorType.Enumerable:
							var enumerator = list.GetEnumerator();
							try
							{
								WriteListFetchEnum(xml, descriptor, enumerator, iStart, iCount);
							}
							finally
							{
								var tmp = enumerator as IDisposable;
								if (tmp != null)
									tmp.Dispose();
							}
							break;

						case ListEnumeratorType.ReadOnlyList:
						case ListEnumeratorType.ListTyped:
						case ListEnumeratorType.RangeEnumerator:
							WriteListFetchTyped(useInterfaceType, xml, descriptor, list, iStart, iCount);
							break;


						case ListEnumeratorType.ListUntyped:
							WriteListFetchList(xml, descriptor, (IList)list, iStart, iCount);
							break;

						default:
							throw new HttpResponseException(System.Net.HttpStatusCode.InternalServerError, String.Format("Liste '{0}' nicht aufzählbar.", controller.Id));
					}
					xml.WriteEndElement();
				}

				xml.WriteEndElement();
				xml.WriteEndDocument();
			}
		} // proc IDEListService.WriteList

		#endregion

		[DEConfigHttpAction("events")]
		private XElement HttpEventsAction(int rev = 0)
		{
			lock (propertyChanged)
			{
				CleanOutdatedEvents();

				iSendedRevision = iCurrentRevision;
				return new XElement("events",
					new XAttribute("rev", iCurrentRevision),
					(
					from c in propertyChanged.Values
					where c.Revision > rev
					select c.GetEvent()
					));
			}
		} // func HttpEventAction
	} // class DEServer
}
