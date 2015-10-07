using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class HttpServer : DEConfigLogItem, IDEHttpServer
	{
		#region -- struct HttpCacheItem ---------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private struct HttpCacheItem
		{
			private int cacheHit;
			private string cacheId;
			private object data;

			public void Set(string _cacheId, object _data)
			{
				cacheHit = 0;
				cacheId = _cacheId;
				data = _data;
			} // proc Set

			public bool Hit(string _cacheId)
			{
				if (cacheHit >= 0 && String.Compare(cacheId, _cacheId, true) == 0)
				{
					cacheHit++;
					return true;
				}
				else
					return false;
			} // func Hit

			public void Clear()
			{
				cacheHit = -1;
				cacheId = null;
				data = null;
			} // proc Clear

			public bool IsEmpty => cacheHit < 0;

			public string CacheId => cacheId;
			public int HitCount => cacheHit;
			public object Data => data;
		} // struct HttpCacheItem

		#endregion

		#region -- class PrefixDefinition -------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class PrefixDefinition
		{
			private readonly string protocol;
			private readonly string hostname;
			private readonly int port;
			private readonly string relativeUriPath;

			public PrefixDefinition(XElement x)
			{
				var uri = x.Value;

				if (String.IsNullOrEmpty(uri))
				{
					this.protocol = null;
				}
				else
				{
					if (uri[uri.Length - 1] != '/')
						throw new ArgumentException("Ein Prefix muss auf / enden.");

					// Prüfe das Protokoll
					var pos = uri.IndexOf("://");
					if (pos == -1)
						throw new ArgumentException("Protokoll konnte nicht geparst werden.");

					this.protocol = uri.Substring(0, pos);
					if (protocol != "http" &&
						protocol != "https")
						throw new ArgumentException(String.Format("Unbekanntes Protokoll ({0}).", protocol));

					// Teste den Hostnamen
					var startAt = pos + 3;
					pos = uri.IndexOfAny(new char[] { ':', '/' }, startAt);

					this.hostname = uri.Substring(startAt, pos - startAt);

					// Optionaler Port
					if (uri[pos] == ':')
					{
						startAt = pos + 1;
						pos = uri.IndexOf('/', startAt);

						if (!int.TryParse(uri.Substring(startAt, pos - startAt), out this.port))
							throw new ArgumentException("Port konnte nicht geparst werden.");
					}
					else
						this.port = 80;

					// Pfad
					this.relativeUriPath = uri.Substring(pos);
				}
			} // ctor

			/// <summary>Fügt den Pfad an.</summary>
			/// <param name="prefixes"></param>
			public void AddHttpPrefix(List<string> prefixes)
			{
				var addPrefix = true;
				var uri = Prefix;
				if (uri == null)
					return;

				// Wurde eine URI schon hinzugefügt
				var uriLength = uri.Length;
				for (var i = prefixes.Count - 1; i >= 0; i--)
				{
					var prefixLength = prefixes[i].Length;
					if (String.Compare(prefixes[i], 0, uri, 0, Math.Min(prefixLength, uriLength), true) == 0)
					{
						if (prefixLength > uriLength)
							prefixes.RemoveAt(i);
						else
							addPrefix = false;
					}
				}

				// Füge das Element an
				if (addPrefix)
					prefixes.Add(uri);
			} // proc AddHttpPrefix

			public bool MatchPrefix(Uri url)
			{
				if (protocol == null)
					return true;
				else if (url.Scheme != protocol)
					return false;
				else if (hostname != "*" && hostname != "+" && hostname != url.Host)
					return false;
				else if (port != url.Port)
					return false;
				else
					return url.AbsolutePath.StartsWith(relativeUriPath);
			} // func MatchPrefix

			/// <summary>Pfad des Prefixes</summary>
			public string PrefixPath => relativeUriPath;
			/// <summary>Komplettes Prefix</summary>
			public string Prefix => protocol == null ? null : protocol + "://" + hostname + ":" + port.ToString() + relativeUriPath;
		} // class PrefixDefinition

		#endregion

		#region -- class PrefixPathTranslation --------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class PrefixPathTranslation : PrefixDefinition
		{
			private readonly string redirectPath;

			public PrefixPathTranslation(XElement x)
				: base(x)
			{
				this.redirectPath = x.GetAttribute("path", "/");
				if (String.IsNullOrEmpty(redirectPath) || redirectPath[0] != '/')
					throw new ArgumentException("Ungültiger interner Pfad.");
			} // ctor

			public string Path => redirectPath;
		} // class PrefixPathTranslation

		#endregion

		#region -- class PrefixAuthentificationScheme -------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class PrefixAuthentificationScheme : PrefixDefinition
		{
			private readonly AuthenticationSchemes scheme;

			public PrefixAuthentificationScheme(XElement x)
				: base(x)
			{
				switch (x.GetAttribute("scheme", "none"))
				{
					case "ntlm":
						scheme = AuthenticationSchemes.Ntlm | AuthenticationSchemes.Anonymous;
						break;
					case "basic":
						scheme = AuthenticationSchemes.Basic | AuthenticationSchemes.Anonymous;
						break;
					case "none":
						scheme = AuthenticationSchemes.Anonymous;
						break;
					default:
						throw new ArgumentException("Unknown authentificationscheme");
				}
			} // ctor

			public AuthenticationSchemes Scheme => scheme;
		} // class PrefixAuthentificationScheme

		#endregion

		private Encoding encoding = Encoding.UTF8;								// Encoding für die Textdaten von Datenströmen
		private HttpListener httpListener = new HttpListener();		// Zugriff auf den HttpListener
		private DEThreadList httpThreads = null;									// Threads, die die Request behandeln

		private Dictionary<string, string> mimeInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Hält die Mime-Informationen
		private List<PrefixAuthentificationScheme> prefixAuthentificationSchemes = new List<PrefixAuthentificationScheme>(); // Mapped verschiedene Authentification-Schemas auf die Urls
		private List<PrefixPathTranslation> prefixPathTranslations = new List<PrefixPathTranslation>(); // Mapped den externen Pfad (URI) auf einen internen Pfad (Path)

		private bool debugMode = false;														// Sollen detailiert die Request-Protokolliert werden

		private HttpCacheItem[] cacheItems = new HttpCacheItem[256];

		#region -- Ctor/Dtor --------------------------------------------------------------

		public HttpServer(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			httpThreads = new DEThreadList(this, "Http-Threads", "HTTP", ExecuteHttpRequest);

			ClearHttpCache();
			httpListener.AuthenticationSchemeSelectorDelegate = GetAuthenticationScheme;

			// Promote die Dienste
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDEHttpServer), this);

#if DEBUG
			Debug = true;
#endif
		} // ctor

		protected override void Dispose(bool lDisposing)
		{
			if (lDisposing)
			{
				// Entferne die Promotion
				var sc = this.GetService<IServiceContainer>(true);
				sc.RemoveService(typeof(IDEHttpServer));

				lock (httpListener)
					try
					{
						httpListener.Stop();
					}
					catch (Exception e)
					{
						Server.LogMsg(e);
					}
				Procs.FreeAndNil(ref httpThreads);
				try { Procs.FreeAndNil(ref httpListener); }
				catch { }
			}
			base.Dispose(lDisposing);
		} // proc Disposing

		#endregion

		#region -- Configuration ----------------------------------------------------------

		protected override void ValidateConfig(XElement config)
		{
			base.ValidateConfig(config);

			if ((from c in config.Elements(xnFiles)
					 where String.Compare(c.GetAttribute("name", String.Empty), "des", true) == 0
					 select c).FirstOrDefault() == null)
			{
				var debugDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(HttpServer).Assembly.Location), @"..\..\..\ServerWebUI"));
				if (Directory.Exists(debugDirectory))
				{
					config.Add(
						new XElement(xnFiles,
							new XAttribute("name", "des"),
							new XAttribute("displayname", "Data Exchange Server - Http (Dbg)"),
							new XAttribute("base", "/"),
							new XAttribute("directory", debugDirectory),
							new XAttribute("priority", 101)
						)
					);
				}
				else
				{
					config.Add(
						new XElement(xnResources,
							new XAttribute("name", "des"),
							new XAttribute("displayname", "Data Exchange Server - Http"),
							new XAttribute("base", "/"),
							new XAttribute("assembly", typeof(HttpServer).Assembly.GetName().FullName),
							new XAttribute("namespace", "TecWare.DE.Server.Resources.Http"),
							new XAttribute("priority", 100)
						)
					);
				}
			}
		} // proc ValidateConfig

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// Lade die Prefixe, Authentifizierungsschemas und mime's
			prefixPathTranslations.Clear();
			prefixAuthentificationSchemes.Clear();
			mimeInfo.Clear();

			// Importiere Standard mime
			mimeInfo[".js"] = "text/javascript";
			mimeInfo[".html"] = "text/html";
			mimeInfo[".css"] = "text/css";
			mimeInfo[".lua"] = "text/plain"; // wird im Standard nicht ausgeführt
			mimeInfo[".png"] = "image/png";
			mimeInfo[".jpg"] = "image/jpeg";
			mimeInfo[".ico"] = "image/x-icon";
			mimeInfo[".xaml"] = "application/xaml+xml";

			mimeInfo[".map"] = "text/json";
			mimeInfo[".ts"] = "text/plain";

			foreach (var x in config.ConfigNew.Elements())
			{
				try
				{
					if (x.Name == xnHttpPrefix)
						prefixPathTranslations.Add(new PrefixPathTranslation(x));
					else if (x.Name == xnHttpAccess)
						prefixAuthentificationSchemes.Add(new PrefixAuthentificationScheme(x));
					else if (x.Name == xnHttpMime) // Lade die Mime-Informationen
					{
						var name = x.GetAttribute("ext", String.Empty);
						var value = x.GetAttribute("mime", String.Empty);

						if (!String.IsNullOrEmpty(name) && !String.IsNullOrEmpty(value) && name[0] == '.')
							mimeInfo[name] = value;
						else
							Log.LogMsg(LogMsgType.Warning, "http/mime hat ein ungültiges Format (ext={0};mime={1}).", name, value);
					}
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "<" + x.Name.LocalName + "> nicht geladen.\n\n" + e.GetMessageString());
				}
			}

			// Erzeuge die Prefix-Liste
			var prefixes = new List<string>();
			foreach (var prefix in prefixPathTranslations)
				prefix.AddHttpPrefix(prefixes);

			// Prüfe, ob der Http-Listener aktualisiert werden muss
			var httpListenerPrefixesChanged = false;
			if (httpListener.IsListening && httpListener.Prefixes.Count == prefixes.Count)
			{
				string[] currentPrefixes = httpListener.Prefixes.ToArray();
				for (int i = 0; i < currentPrefixes.Length; i++)
				{
					if (currentPrefixes[i] != prefixes[i])
					{
						httpListenerPrefixesChanged = true;
						break;
					}
				}
			}
			else
				httpListenerPrefixesChanged = true;

			if (httpListenerPrefixesChanged)
			{
				lock (httpListener)
				{
					// Stop den Listener
					if (httpListener.IsListening)
						httpListener.Stop();
					httpListener.Prefixes.Clear();

					// Lade die Prefixe neu
					foreach (string c in prefixes)
						httpListener.Prefixes.Add(c);
				}
			}

			var configNode = new XConfigNode(Server.Configuration[xnHttp], config.ConfigNew);

			// Erzeuge die Worker
			httpThreads.Count = configNode.GetAttribute<int>("threads");
			// Setze den Realm
			httpListener.Realm = configNode.GetAttribute<string>("realm");
			// Lese die Default-Encoding aus
			encoding = configNode.GetAttribute<Encoding>("encoding");

			// Lade die Erweiterungen (Zerstört werden Sie automatisch)
			foreach (XElement cur in config.ConfigNew.Elements().ToArray())
			{
				try
				{
					Server.LoadConfigExtension(config, cur, MainNamespace.NamespaceName);
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "Fehler beim Initialisieren der Dienste.\n\n" + e.GetMessageString());
				}
			}
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			// Starte den Listener, falls er durch das Laden der Konfiguration beendet wurde
			lock (httpListener)
				try
				{
					// Default-Prefix setzen  
					if (httpListener.Prefixes.Count == 0)
						httpListener.Prefixes.Add("http://localhost:8080/");

					// Starte den Listener
					if (!httpListener.IsListening)
						httpListener.Start();
				}
				catch (Exception e)
				{
					Server.LogMsg(EventLogEntryType.Error, e.GetMessageString());
				}
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Http Schnittstelle -----------------------------------------------------

		[
		DEConfigHttpAction("debug", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Schaltet den DebugModus ein oder aus.")
		]
		private XElement HttpDebugAction(bool debug)
		{
			this.Debug = debug;
			return new XElement("debug", Debug);
		} // proc HttpDebugAction

		[
		DEConfigHttpAction("clearCache", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Löscht den aktuellen Cache")
		]
		private void HttpClearCacheAction()
		{
			ClearHttpCache();
		} // proc HttpClearCacheAction

		//[
		//DEConfigHttpAction("getCache"),
		//Description("Gibt den Cacheinhalt als Tabelle zurück.")
		//]
		//private void HttpGetCacheAction(HttpResponse r)
		//{
		//	using (var html = HtmlHelper.Create(r.GetOutputTextWriter("text/html")))
		//		lock (cacheItems)
		//		{
		//			html.WriteStartHtml("table", "#cache");

		//			int iCacheSize = 0;
		//			html.WriteStartHtml("tbody");
		//			for (int i = 0; i < cacheItems.Length; i++)
		//				if (!cacheItems[i].IsEmpty)
		//				{
		//					html.WriteStartHtml("tr");
		//					html.WriteHtml("td", null, cacheItems[i].CacheId);
		//					html.WriteHtml("td", null, cacheItems[i].HitCount.ToString("N0"), "x");
		//					object obj = cacheItems[i].Data;
		//					int iLen;
		//					string sType;
		//					if (obj is string)
		//					{
		//						iLen = ((string)obj).Length;
		//						sType = "text";
		//					}
		//					else if (obj is byte[])
		//					{
		//						iLen = ((byte[])obj).Length;
		//						sType = "bytes";
		//					}
		//					else if (obj is ILuaScript)
		//					{
		//						LuaChunk c = ((ILuaScript)obj).Chunk;
		//						iLen = c != null && c.Method.GetType().Name == "RuntimeMethodInfo" ? c.Size : -1;
		//						sType = "script";
		//					}
		//					else
		//					{
		//						iLen = -1;
		//						sType = "unknown";
		//					}
		//					html.WriteHtml("td", null, new XAttribute("style", "text-align: center"), sType);
		//					html.WriteHtml("td", null, new XAttribute("style", "text-align: right"), iLen == -1 ? "N.A." : Procs.FormatFileSize(iLen));
		//					if (iLen > 0)
		//						iCacheSize += iLen;

		//					html.WriteEndHtml();
		//				}
		//			html.WriteEndHtml();

		//			html.WriteStartHtml("tfoot");
		//			html.WriteStartHtml("tr");
		//			html.WriteHtml("th", null);
		//			html.WriteHtml("th", null);
		//			html.WriteHtml("th", null);
		//			html.WriteHtml("th", null, new XAttribute("style", "text-align: right"), Procs.FormatFileSize(iCacheSize));
		//			html.WriteEndHtml();
		//			html.WriteEndHtml();

		//			html.WriteEndHtml();
		//		}
		//} // proc HttpGetCacheAction

		//protected override void HttpInfoCollectSections(List<HttpInfoSection> sections)
		//{
		//	base.HttpInfoCollectSections(sections);

		//	sections.Add(new HttpInfoSection
		//	{
		//		Title = "Http-Server",
		//		Content = html => html.WriteHtml("table", "#cache"),
		//		Buttons = tw =>
		//		{
		//			tw.WriteActionButton("Debug an", "debug&debug=true", ButtonActionTask.Call);
		//			tw.WriteActionButton("Debug aus", "debug&debug=false", ButtonActionTask.Call);
		//			tw.WriteActionButton("Cache löschen", "clearCache", ButtonActionTask.Call);
		//			tw.WriteActionButton("getCache");
		//		}
		//	});
		//} // proc HttpInfoCollectSections

		#endregion

		#region -- MimeInfo ---------------------------------------------------------------

		public string GetContentType(string sExtension)
		{
			string sContentType;
			lock (mimeInfo)
				if (mimeInfo.TryGetValue(sExtension, out sContentType))
					return sContentType;
				else
					throw new ArgumentException(String.Format("Kein mime-type definiert für '{0}'.", sExtension), "extension");
		} // func GetContentType

		#endregion

		#region -- Web Cache --------------------------------------------------------------

		public object GetWebCache(string cacheId)
		{
			if (String.IsNullOrEmpty(cacheId))
				return null;

			lock (cacheItems)
			{
				for (var i = 0; i < cacheItems.Length; i++)
					if (cacheItems[i].Hit(cacheId))
						return cacheItems[i].Data;
			}
			return null;
		} // func GetWebCache

		public bool UpdateWebCache(string cacheId, object data)
		{
			if (String.IsNullOrEmpty(cacheId))
				return false;
			if (!(data is string) && !(data is byte[]) && !(data is ILuaScript))
				throw new ArgumentException("data muss string, byte[] oder ein script sein.", "data");

			lock (cacheItems)
			{
				var freeIndex = -1;
				var minHitCount = -1;
				var minHitIndex = -1;

				// Scan den Cache
				for (int i = 0; i < cacheItems.Length; i++)
				{
					if (cacheItems[i].IsEmpty)
					{
						freeIndex = i;
						break;
					}
					else if (minHitCount == -1 || minHitCount > cacheItems[i].HitCount)
					{
						minHitCount = cacheItems[i].HitCount;
						minHitIndex = i;
					}
				}

				// Setze den Cache
				if (freeIndex >= 0)
				{
					cacheItems[freeIndex].Set(cacheId, data);
				}
				else if (minHitIndex >= 0)
				{
					cacheItems[minHitIndex].Set(cacheId, data);
				}

				return true;
			}
		} // proc UpdateWebCache

		private void ClearHttpCache()
		{
			lock (cacheItems)
			{
				for (int i = 0; i < cacheItems.Length; i++)
					cacheItems[i].Clear();
			}
		} // proc ClearHttpCache

		#endregion

		#region -- ProcessRequest ---------------------------------------------------------

		private AuthenticationSchemes GetAuthenticationScheme(HttpListenerRequest r)
		{
			var prefixScheme = FindPrefix(prefixAuthentificationSchemes, r.Url);
			return prefixScheme == null ? AuthenticationSchemes.Anonymous : prefixScheme.Scheme;
		} // func GetAuthenticationScheme

		private void ExecuteHttpRequest()
		{
			if (IsHttpListenerRunning)
			{
				HttpListenerContext ctx = null;
				try
				{
					ctx = httpListener.GetContext();
				}
				catch (HttpListenerException e)
				{
					if (e.ErrorCode != 995)
						throw;
				}
				if (ctx != null)
					ProcessRequest(ctx);
			}
			else
				DEThread.CurrentThread.WaitFinish(500);
		} // proc ExecuteHttpRequest

		private void ProcessRequest(HttpListenerContext ctx)
		{
			// Erzeuge das Antwort-Objekt
			var url = ctx.Request.Url;

			// Suche das zugehörige Prefix und übersetze den Pfad
			var pathTranslation = FindPrefix(prefixPathTranslations, url);
			string absolutePath;
			if (pathTranslation == null || pathTranslation.Prefix == null)
				absolutePath = url.AbsolutePath;
			else
				absolutePath = pathTranslation.Path + url.AbsolutePath.Substring(pathTranslation.PrefixPath.Length);

			// Führe die Anfrage aus
			using (HttpResponse r = new HttpResponse(this, ctx, absolutePath))
				try
				{
					// Sollen der Request ins Log geschrieben werden
					if (debugMode)
						r.LogStart();

					// Setze die Kultur des Clients für den aktuellen Thread
					Thread.CurrentThread.CurrentCulture = r.CultureInfo;
					Thread.CurrentThread.CurrentUICulture = r.CultureInfo;

					// Starte das ablaufen des Pfades
					r.PushPath(Server, "/");

					// Versuche den Pfad auf einen Configurationsknoten zu mappen
					if (!ProcessRequestForConfigItem(r, this.GetService<DEServer>(true)))
					{
						// Suche innerhalb der Http-Worker
						using (EnterReadLock())
						{
							if (!UnsafeProcessRequest(r))
								throw new HttpResponseException(HttpStatusCode.BadRequest, "Nicht verarbeitet.");
						}
					}

					// Wurde eine Rückgabe geschrieben
					if (ctx.Request.HttpMethod != "OPTIONS" && ctx.Response.ContentType == null)
						throw new HttpResponseException(HttpStatusCode.NoContent, "Keine Rückgabe definiert.");
				}
				catch (Exception e)
				{
					// Entpacke die Target exceptions
					var ex = e;
					while (ex is TargetInvocationException)
						ex = ex.InnerException;

					// Lese die Meldung fürs Response aus
					var httpEx = ex as HttpResponseException;

					// Startet ggf. die Log-Aufzeichnung
					if (httpEx != null && httpEx.Code == HttpStatusCode.Unauthorized)
						r.LogStop();
					else
					{
						// Schreibe die Fehlermeldung
						r.LogStart();
						r.Log(l => l.WriteException(ex));
					}

					if (httpEx != null)
						ctx.Response.StatusCode = (int)httpEx.Code;
					else
						ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
					ctx.Response.StatusDescription = FilterChar(ex.Message);
				}
		} // proc ProcessRequest

		private bool ProcessRequestForConfigItem(HttpResponse r, DEConfigItem current)
		{
			using (current.EnterReadLock())
			{
				// Suche den zugehörigen Knoten
				var currentName = r.RelativePathName;
				var find = currentName != null ? current.UnsafeFind(r.RelativePathName) : null;
				if (find != null)
					try
					{
						r.PushPath(find, currentName);
						if (ProcessRequestForConfigItem(r, find))
							return true;
					}
					finally
					{
						r.PopPath();
					}

				// 1. Prüfe die Actions an diesem Knoten
				string actionName;
				if (r.RelativePath.Length == 0 && !String.IsNullOrEmpty(actionName = r.GetParameter("action", String.Empty)))
				{
					if (actionName == "lines" || actionName == "states" || actionName == "events")
						r.LogStop();

					// Führe die Action aus
					current.UnsafeInvokeHttpAction(actionName, r);
					return true;
				}
				// 2. Kann der Knoten den Eintrag verarbeiten
				else if (current.UnsafeProcessRequest(r))
					return true;
				else // 3. Gibt es einen überliegenden HttpWorker der dazu passt
					return false;
			}
		} // func ProcessRequestForConfigItem

		private static T FindPrefix<T>(List<T> prefixes, Uri url)
			where T : PrefixDefinition
		{
			lock (prefixes)
				return prefixes.Find(c => c.MatchPrefix(url));
		} // func FindPrefix

		private static string FilterChar(string sMessage)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < sMessage.Length; i++)
			{
				char c = sMessage[i];
				if (c == '\n')
					sb.Append("<br/>");
				else if (c > (char)0x1F || c == '\t')
					sb.Append(c);
			}
			return sb.ToString();
		} // func FilterChar

		private bool IsHttpListenerRunning { get { lock (httpListener) return httpListener.IsListening; } }

		#endregion

		public override string Icon { get { return "/images/http.png"; } }

		/// <summary>Encodierung für Textdateien</summary>
		public Encoding Encoding { get { return encoding; } }

		[
		PropertyName("tw_http_debugmode"),
		DisplayName("Protokollierung"),
		Category("Http"),
		Description("Ist die Protokollierung der Http-Request aktiv."),
		]
		public bool Debug { get { return debugMode; } private set { SetProperty(ref debugMode, value); } }
	} // class HttpServer
}
