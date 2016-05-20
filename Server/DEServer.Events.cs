using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using TecWare.DE.Networking;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	internal partial class DEServer : IDEListService, IDEWebSocketProtocol
	{
		#region -- class FiredEvent -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class FiredEvent
		{
			private int lastFired;
			private int revision;

			private readonly string path;
			private readonly string eventId;
			private readonly string index;
			private XElement values;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public FiredEvent(int revision, string path, string eventId, string index, XElement values)
			{
				if (String.IsNullOrEmpty(path))
					throw new ArgumentNullException("path");
				if (String.IsNullOrEmpty(eventId))
					throw new ArgumentNullException("event");
				if (String.IsNullOrEmpty(index))
					index = String.Empty;

				this.path = path;
				this.eventId = eventId;
				this.index = index;

				Reset(revision, values);
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
				return path.GetHashCode() | eventId.GetHashCode() | index.GetHashCode();
			} // func GetHashCode

			#endregion

			public void Reset(int revision, XElement values)
			{
				this.lastFired = Environment.TickCount;
				this.revision = revision;
				this.values = values;
			} // proc Reset

			/// <summary>Formuliert das Event.</summary>
			/// <returns>Xml-Fragment für dieses Event</returns>
			public XElement GetEvent()
			{
				return new XElement("event",
					new XAttribute("path", path),
					new XAttribute("event", eventId),
					String.IsNullOrEmpty(index) ? null : new XAttribute("index", index),
					values);
			} // func GetEvent

			public bool IsEqual(string testPath, string testEvent, string testIndex)
			{
				return String.Compare(path, testPath, true) == 0 &&
						String.Compare(eventId, testEvent, true) == 0 &&
						String.Compare(index, testIndex, true) == 0;
			} // func IsEqual

			public bool IsActive { get { return Environment.TickCount - lastFired > 300000; } } // 5min
			public string Path { get { return path; } }
			public string Event { get { return eventId; } }
			public string Index { get { return index; } }
			public int Revision { get { return revision; } }
		} // class FiredEvent

		#endregion

		private int sendedRevision = 0; // Zuletzt übertragene Revision
		private int currentRevision = 1; // Aktuelle Revision
		private Dictionary<string, FiredEvent> propertyChanged = new Dictionary<string, FiredEvent>(); // Liste mit allen Events, mit ihrer Revision (sortiert nach rev)
		private List<IDEWebSocketContext> activeContexts = new List<IDEWebSocketContext>();

		private int lastEventClean = Environment.TickCount;

		#region -- Events -----------------------------------------------------------------

		public void AppendNewEvent(DEConfigItem item, string eventId, string index, XElement values = null)
		{
			lock (propertyChanged)
			{
				if (currentRevision == sendedRevision)
					currentRevision++;

				CleanOutdatedEvents();

				var configPath = item.ConfigPath;
				var key = GetEventKey(configPath, eventId, index);
				FiredEvent ev;
				if (propertyChanged.TryGetValue(key, out ev))
					ev.Reset(currentRevision, values);
				else
					propertyChanged[key] = ev = new FiredEvent(currentRevision, configPath, eventId, index, values);

				// web socket event handling
				FireEventOnSocket(configPath, ev.GetEvent());
			}
		} // proc AppendNewEvent

		private string GetEventKey(string path, string eventId, string index)
		{
			if (String.IsNullOrEmpty(path))
				throw new ArgumentNullException("path");
			if (String.IsNullOrEmpty(eventId))
				throw new ArgumentNullException("event");
			if (String.IsNullOrEmpty(index))
				index = String.Empty;

			return path + ":" + eventId + "[" + index + "]";
		} // func GetEventKey

		private void CleanOutdatedEvents()
		{
			lock (propertyChanged)
			{
				if (Environment.TickCount - lastEventClean < 10000)
					return;

				var cleanKeys = new string[100];
				var keyCount = 0;

				// Suche die Ausgelaufenen Events
				foreach (var cur in propertyChanged)
				{
					if (!cur.Value.IsActive)
					{
						cleanKeys[keyCount++] = cur.Key;
						if (keyCount >= cleanKeys.Length)
							break;
					}
				}

				// Lösche die Schlüssel
				for (int i = 0; i < keyCount; i++)
					propertyChanged.Remove(cleanKeys[i]);

				lastEventClean = Environment.TickCount;
			}
		} // proc CleanOutdatedEvents

		#endregion

		#region -- WebSocket Events -------------------------------------------------------

		public bool AcceptWebSocket(IDEWebSocketContext webSocket)
		{
			lock(activeContexts)
				activeContexts.Add(webSocket);
			return true;
		} // func AcceptWebSocket

		private void FireEventOnSocket(string path, XElement xEvent)
		{
			// prepare line
			var eventLine = xEvent.ToString(SaveOptions.DisableFormatting);

			lock (activeContexts)
			{
				// remove dead sockets
				for (int i = activeContexts.Count - 1; i >= 0; i--)
				{
					var c = activeContexts[i];
					if (c.WebSocket.State != WebSocketState.Open) // check if the socket is available
						activeContexts.RemoveAt(i);
					else if (c.AbsolutePath.Length == 0 || path.StartsWith(c.AbsolutePath, StringComparison.OrdinalIgnoreCase)) // spawn thread
						Task.Factory.StartNew(SendEventOnSocketAsync(c, eventLine).Wait);
				}
			}
		} // proc FireEventOnSocket

		private async Task SendEventOnSocketAsync(IDEWebSocketContext context, string eventLine)
		{
			var ws = context.WebSocket;
			
			if (ws.State == System.Net.WebSockets.WebSocketState.Open)
			{
				var segment = new ArraySegment<byte>(context.Server.Encoding.GetBytes(eventLine));
				await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
			}
		} // proc SendEventOnSocketAsync

		string IDEWebSocketProtocol.Protocol => "des_event";
		string IDEWebSocketProtocol.BasePath => String.Empty;
		string IDEWebSocketProtocol.SecurityToken => SecuritySys;

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

		private static void WriteListCheckRange(XmlWriter xml, ref int startAt, ref int count, int listCount, bool allowNegativeCount)
		{
			if (listCount < 0)
			{
				if (allowNegativeCount)
					return;
				else
					listCount = 0;
			}

			// Prüfe den Start
			if (startAt < 0) // setze von hinten auf
			{
				startAt = listCount + startAt;
				if (startAt < 0)
					startAt = 0;
			}

			if (count < 0) // Anzahl korrigieren
				count = 0;
			else if (startAt + count > listCount)
			{
				count = listCount - startAt;
				if (count < 0)
					count = 0;
			}

			xml.WriteAttributeString("tc", listCount.ToString());
			xml.WriteAttributeString("s", startAt.ToString());
			xml.WriteAttributeString("c", count.ToString());
		} // proc WriteListCheckRange

		#endregion

		#region -- WriteListFetchEnum -----------------------------------------------------

		private void WriteListFetchEnum(XmlWriter xml, IDEListDescriptor descriptor, IEnumerator enumerator, int startAt, int count)
		{
			if (startAt < 0 || count < 0)
				throw new ArgumentException("start oder count dürfen nicht negativ sein.");

			// Überspringe die ersten
			while (startAt > 0)
			{
				if (!enumerator.MoveNext())
					break;
				startAt--;
			}

			// Gib die Element aus
			while (count > 0)
			{
				if (!enumerator.MoveNext())
					break;

				descriptor.WriteItem(new DEListItemWriter(xml), enumerator.Current);
				count--;
			}

		} // func WriteListFetchEnum

		#endregion

		#region -- WriteListFetchTyped ----------------------------------------------------

		#region -- class GenericWriter ----------------------------------------------------

		private class GenericWriter
		{
			private static void WriteList<T>(IPropertyReadOnlyDictionary r, XmlWriter xml, IDEListDescriptor descriptor, IReadOnlyList<T> list, int startAt, int count)
			{
				WriteListCheckRange(xml, ref startAt, ref count, list.Count, false);

				if (count > 0)
				{
					var end = startAt + count;
					for (int i = startAt; i < end; i++)
						descriptor.WriteItem(new DEListItemWriter(xml), (T)list[i]);
				}
			} // proc WriteList

			private static void WriteList<T>(IPropertyReadOnlyDictionary r, XmlWriter xml, IDEListDescriptor descriptor, IList<T> list, int startAt, int count)
			{
				WriteListCheckRange(xml, ref startAt, ref count, list.Count, false);

				if (count > 0)
				{
					var end = startAt + count;
					for (int i = startAt; i < end; i++)
						descriptor.WriteItem(new DEListItemWriter(xml), (T)list[i]);
				}
			} // proc WriteList

			private static void WriteList<T>(IPropertyReadOnlyDictionary r, XmlWriter xml, IDEListDescriptor descriptor, IDERangeEnumerable2<T> list, int startAt, int count)
			{
				WriteListCheckRange(xml, ref startAt, ref count, list.Count, true);

				if (count > 0)
				{
					using (var enumerator = list.GetEnumerator(startAt, count, r))
					{
						while (count > 0)
						{
							if (!enumerator.MoveNext())
								break;

							descriptor.WriteItem(new DEListItemWriter(xml), enumerator.Current);
							count--;
						}
					}
				}
			} // proc WriteList
		} // class GenericWriter

		#endregion

		private void WriteListFetchTyped(Type type, IPropertyReadOnlyDictionary r, XmlWriter xml, IDEListDescriptor descriptor, object list, int startAt, int count)
		{
			Type typeGeneric = type.GetGenericTypeDefinition(); // Hole den generischen Typ ab

			// Suche die passende procedure
			var miWriteList = typeof(GenericWriter).GetTypeInfo().DeclaredMethods.Where(mi => mi.IsStatic && mi.Name == "WriteList" && mi.IsGenericMethodDefinition && mi.GetParameters()[3].ParameterType.Name == typeGeneric.Name).FirstOrDefault();
			if (miWriteList == null)
				throw new ArgumentNullException("writelist", String.Format("Keinen generische Implementierung gefunden ({0}).", typeGeneric.FullName));

			// Aufruf des Writers
			var typeDelegate = typeof(Action<,,,,,>).MakeGenericType(typeof(IPropertyReadOnlyDictionary), typeof(XmlWriter), typeof(IDEListDescriptor), type, typeof(int), typeof(int));
			var miWriteListTyped = miWriteList.MakeGenericMethod(type.GetTypeInfo().GenericTypeArguments[0]);
			var dlg = Delegate.CreateDelegate(typeDelegate, miWriteListTyped);
			dlg.DynamicInvoke(r, xml, descriptor, list, startAt, count);
		} // proc WriteListFetchListTyped

		#endregion

		#region -- WriteListFetchList -----------------------------------------------------

		private void WriteListFetchList(XmlWriter xml, IDEListDescriptor descriptor, IList list, int startAt, int count)
		{
			WriteListCheckRange(xml, ref startAt, ref count, list.Count, false);

			if (count > 0)
			{
				var end = startAt + count;
				for (int i = startAt; i < end; i++)
					descriptor.WriteItem(new DEListItemWriter(xml), list[i]);
			}
		} // proc WriteListFetchList

		#endregion

		void IDEListService.WriteList(IDEContext r, IDEListController controller, int startAt, int count)
		{
			var sendTypeDefinition = String.Compare(r.GetProperty("desc", Boolean.FalseString), Boolean.TrueString, StringComparison.OrdinalIgnoreCase) == 0;

			// Suche den passenden Descriptor
			var descriptor = controller.Descriptor;
			if (descriptor == null)
				throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Liste '{0}' besitzt kein Format.", controller.Id));

			controller.OnBeforeList();

			// Rückgabe
			using (var tw = r.GetOutputTextWriter(MimeTypes.Text.Xml))
			using (var xml = XmlWriter.Create(tw, GetSettings(tw)))
			{
				xml.WriteStartDocument();
				xml.WriteStartElement("list");

				// Sollen die Strukturinformationen übertragen werdem
				if (sendTypeDefinition)
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
					var useInterface = ListEnumeratorType.Enumerable;
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
							else if (genericType == typeof(IDERangeEnumerable2<>))
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
								WriteListFetchEnum(xml, descriptor, enumerator, startAt, count);
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
							WriteListFetchTyped(useInterfaceType, r, xml, descriptor, list, startAt, count);
							break;


						case ListEnumeratorType.ListUntyped:
							WriteListFetchList(xml, descriptor, (IList)list, startAt, count);
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

				sendedRevision = currentRevision;
				return new XElement("events",
					new XAttribute("rev", currentRevision),
					(
					from c in propertyChanged.Values
					where c.Revision > rev
					select c.GetEvent()
					));
			}
		} // func HttpEventAction
	} // class DEServer
}
