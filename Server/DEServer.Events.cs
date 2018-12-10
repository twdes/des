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
		#region -- class FiredEvent ---------------------------------------------------

		private sealed class FiredEvent
		{
			private int lastFired;
			private int revision;

			private readonly string path;
			private readonly string eventId;
			private readonly string index;
			private XElement values;

			#region -- Ctor/Dtor ------------------------------------------------------

			public FiredEvent(int revision, string path, string eventId, string index, XElement values)
			{
				if (String.IsNullOrEmpty(path))
					throw new ArgumentNullException(nameof(path));
				if (String.IsNullOrEmpty(eventId))
					throw new ArgumentNullException(nameof(eventId));

				this.path = path;
				this.eventId = eventId;
				this.index = index ?? String.Empty;

				Reset(revision, values);
			} // ctor

			public override bool Equals(object obj)
				=> obj is FiredEvent other
					? IsEqual(other.Path, other.Event, other.Index)
					: base.Equals(obj);

			public override int GetHashCode()
				=> path.GetHashCode() | eventId.GetHashCode() | index.GetHashCode();

			#endregion

			public void Reset(int revision, XElement values)
			{
				lastFired = Environment.TickCount;

				this.revision = revision;
				this.values = values;
			} // proc Reset

			/// <summary>Formuliert das Event.</summary>
			/// <returns>Xml-Fragment für dieses Event</returns>
			public XElement GetEvent()
			=> FormatEvent(path, eventId, index, values);

			public bool IsEqual(string testPath, string testEvent, string testIndex)
				=> String.Compare(path, testPath, StringComparison.OrdinalIgnoreCase) == 0
					&& String.Compare(eventId, testEvent, StringComparison.OrdinalIgnoreCase) == 0
					&& String.Compare(index, testIndex, StringComparison.OrdinalIgnoreCase) == 0;

			public bool IsActive => Environment.TickCount - lastFired > 300000; // 5min
			public string Path => path;
			public string Event => eventId;
			public string Index => index;
			public int Revision => revision;
		} // class FiredEvent

		#endregion

		#region -- class EventSession -------------------------------------------------

		private sealed class EventSession : IDisposable
		{
			private readonly DEServer server;
			private readonly IDEWebSocketScope context;
			private readonly SynchronizationContext synchronizationContext;

			private string[] eventFilter = Array.Empty<string>();

			#region -- Ctor/Dtor ------------------------------------------------------

			public EventSession(DEServer server, IDEWebSocketScope context)
			{
				this.server = server ?? throw new ArgumentNullException(nameof(server));
				this.context = context ?? throw new ArgumentNullException(nameof(context));

				this.synchronizationContext = SynchronizationContext.Current ?? throw new ArgumentNullException("SynchronizationContext.Current");

				server.AddEventSession(this);
			} // ctor

			public void Dispose()
			{
				// remove session
				server.RemoveEventSession(this);
			} // proc Dispose

			public Task CloseAsync(CancellationToken cancellationToken)
			{
				if (Socket.State == WebSocketState.Open)
					return context.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close.", cancellationToken);
				else
					return Task.CompletedTask;
			} // proc CloseAsync

			#endregion

			#region -- PostNotify -----------------------------------------------------

			private void ExecuteNotify(object state)
			{
				var eventLine = (string)state;

				SendEventLineAsync(eventLine)
					.ContinueWith(
						t => server.Log.Warn("Event Socket notify failed.", t.Exception),
						TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
					);
			} // proc ExecuteNotify

			private Task SendEventLineAsync(string eventLine)
			{
				var segment = new ArraySegment<byte>(context.Http.DefaultEncoding.GetBytes(eventLine));
				return Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
			} // proc SendEventLineAsync

			public void TryPostNotify(string path, string securityToken, string eventId, string eventLine, CancellationToken cancellationToken)
			{
				// check if websocket still alive
				if (Socket.State != WebSocketState.Open)
					return;

				// check if is eventId filtered
				if (eventFilter.Length > 0 && !eventFilter.Contains(eventId))
					return;

				// check if the path is requested
				if ((context.AbsolutePath.Length > 0 && !path.StartsWith(context.AbsolutePath, StringComparison.OrdinalIgnoreCase))
					|| !context.TryDemandToken(securityToken))
					return;

				synchronizationContext.Post(ExecuteNotify, eventLine);
			} // proc TryNotifyAsync

			#endregion

			public async Task ExecuteCommandAsync(string commandLine)
			{
				var pos = commandLine.IndexOf(' ');
				var firstPart = pos >= 0 ? commandLine.Substring(0, pos) : commandLine;
				// parse command line
				switch (firstPart)
				{
					case "/setFilter":
						if (pos == -1)
							eventFilter = Array.Empty<string>(); // clear filter
						else
							SetEventFilter(commandLine.Substring(pos + 1));
						goto case "/getFilter";
					case "/getFilter":
						await SendEventLineAsync(FormatEventLine(FormatEvent(null, "eventFilter", null, new XElement("f", String.Join(";", eventFilter)))));
						break;
					case "/ping": // ping event
						await SendEventLineAsync(FormatEventLine(FormatEvent(null, "pong", null, null))); // write empty event
						break;
					default:
						throw new ArgumentOutOfRangeException(nameof(commandLine), firstPart, "Unknown command.");
				}
			} // proc ExecuteCommandAsync

			public void SetEventFilter(string newEventFilter)
			{
				eventFilter = newEventFilter == null
					? Array.Empty<string>()
					: (from c in newEventFilter.Split(new char[] { ';', ',' })
					   let c1 = c.Trim()
					   where c1.Length > 0
					   select c1).ToArray();
			} // proc SetEventFilter

			public WebSocket Socket => context.WebSocket;
		} // class EventSession

		#endregion

		private int sendedRevision = 0;		// Last revision sent to the client (any)
		private int currentRevision = 1;	// Current revision
		private readonly Dictionary<string, FiredEvent> propertyChanged = new Dictionary<string, FiredEvent>();	// List of all fired events, sorted by rev.
		private readonly DEList<EventSession> eventSessions;                                                    // List with webSockets, they request events
		private bool eventSessionsClosing = false;

		private int lastEventClean = Environment.TickCount;

		private void AddEventSession(EventSession eventSession)
			=> eventSessions.Add(eventSession);

		private void RemoveEventSession(EventSession eventSession)
			=> eventSessions.Remove(eventSession);

		private void CloseEventSessions()
		{
			eventSessionsClosing = true;

			// close all connections
			var closeTasks = new List<Task>();
			while (eventSessions.Count > 0)
			{
				var es = eventSessions[eventSessions.Count - 1];
				var t = es.CloseAsync(CancellationToken.None);
				if (!t.IsCompleted)
					closeTasks.Add(t);
				eventSessions.Remove(es);
			}
			if (closeTasks.Count > 0)
				Task.WaitAll(closeTasks.ToArray(), 5000);
		} // proc CloseEventSessions

		#region -- Events -----------------------------------------------------------------

		public void AppendNewEvent(DEConfigItem item, string securityToken, string eventId, string index, XElement values = null)
		{
			lock (propertyChanged)
			{
				if (currentRevision == sendedRevision)
					currentRevision++;

				CleanOutdatedEvents();

				var configPath = item.ConfigPath;
				var key = GetEventKey(configPath, eventId, index);
				if (propertyChanged.TryGetValue(key, out var ev))
					ev.Reset(currentRevision, values);
				else
					propertyChanged[key] = ev = new FiredEvent(currentRevision, configPath, eventId, index, values);
				
				// web socket event handling
				FireEventOnSocket(configPath, securityToken ?? item.SecurityToken, eventId, ev.GetEvent());
			}
		} // proc AppendNewEvent

		private static string GetEventKey(string path, string eventId, string index)
		{
			if (String.IsNullOrEmpty(path))
				throw new ArgumentNullException("path");
			if (String.IsNullOrEmpty(eventId))
				throw new ArgumentNullException("event");

			return String.IsNullOrEmpty(index)
				? path + ":" + eventId
				: path + ":" + eventId + "[" + index + "]";
		} // func GetEventKey

		private static XElement FormatEvent(string path, string eventId, string index, XElement values)
		{
			return new XElement("event",
				path == null ? null : new XAttribute("path", path),
				new XAttribute("event", eventId),
				String.IsNullOrEmpty(index) ? null : new XAttribute("index", index),
				values
			);
		} // func 

		private static string FormatEventLine(XElement xEvent)
			=> xEvent.ToString(SaveOptions.DisableFormatting);

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
				for (var i = 0; i < keyCount; i++)
					propertyChanged.Remove(cleanKeys[i]);

				lastEventClean = Environment.TickCount;
			}
		} // proc CleanOutdatedEvents

		#endregion

		#region -- WebSocket Events -------------------------------------------------------

		public async Task ExecuteWebSocketAsync(IDEWebSocketScope webSocket)
		{
			if (eventSessionsClosing)
				return;

			// create event session
			var eventSession = new EventSession(this, webSocket);
			try
			{
				var ws = webSocket.WebSocket;
				while (ws.State == WebSocketState.Open)
				{
					var segment = WebSocket.CreateServerBuffer(1024);
					var r = await ws.ReceiveAsync(segment, CancellationToken.None);
					if (r.MessageType == WebSocketMessageType.Text) // message as relative uri
					{
						try
						{
							await eventSession.ExecuteCommandAsync(Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count));
						}
						catch (Exception e)
						{
							Log.Except("Command failed: ", e);
						}
					}
				}
			}
			catch (Exception e)
			{
				Log.Warn("Event Socket failed.", e);
			}
		} // func AcceptWebSocket

		private void FireEventOnSocket(string path, string securityToken, string eventId, XElement xEvent)
		{
			// prepare line
			var eventLine = FormatEventLine(xEvent);
			
			using (eventSessions.EnterReadLock())
			{
				// a call to notify can cause a remove of this session, this will be done in a different thread
				foreach (var es in eventSessions.List.Cast<EventSession>())
					es.TryPostNotify(path, securityToken, eventId, eventLine, CancellationToken.None);
			}
		} // proc FireEventOnSocket

		string IDEWebSocketProtocol.Protocol => "des_event";
		string IDEWebSocketProtocol.BasePath => String.Empty;
		string IDEWebSocketProtocol.SecurityToken => SecurityUser;

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
			else if (count == Int32.MaxValue || startAt + count > listCount)
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

		void IDEListService.WriteList(IDEWebRequestScope r, IDEListController controller, int startAt, int count)
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
								if (enumerator is IDisposable tmp)
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
					)
				);
			}
		} // func HttpEventAction
	} // class DEServer
}
