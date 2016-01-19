using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- class DECommonContext ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal abstract class DECommonContext : IDECommonContext, IDisposable
	{
		private readonly DEHttpServer http;
		private readonly Lazy<NameValueCollection> queryString;
		private readonly HttpListenerRequest request;
		private readonly string absolutePath;

		private IDEAuthentificatedUser user = null;
		private readonly Lazy<CultureInfo> clientCultureInfo;

		#region -- Ctor/Dtor --------------------------------------------------------------

		protected DECommonContext(DEHttpServer http, HttpListenerRequest request, string absolutePath)
		{
			this.http = http;
			this.queryString = new Lazy<NameValueCollection>(() => request.QueryString);
			this.request = request;
			this.absolutePath = absolutePath;

      this.clientCultureInfo = new Lazy<CultureInfo>(() =>
			{
				try
				{
					var userLanguages = request.UserLanguages;
					if (userLanguages == null || userLanguages.Length == 0)
						return http.DefaultCultureInfo;
					else
						return CultureInfo.GetCultureInfo(userLanguages[0]);
				}
				catch
				{
					return http.DefaultCultureInfo;
				}
			}
			);
		} // ctor

		public void Dispose()
		{
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				Procs.FreeAndNil(ref user);
		} // proc Dispose

		#endregion

		#region -- User -------------------------------------------------------------------

		/// <summary>Change the current user on the context, to a server user. Is the given user null, the result is also null.</summary>
		internal void AuthentificateUser(IPrincipal authentificateUser)
		{
			if (authentificateUser != null)
			{
				user = http.Server.AuthentificateUser(authentificateUser.Identity);
				if (user == null)
					throw new HttpResponseException(HttpStatusCode.Unauthorized, String.Format("Authentification against the DES-Users failed: {0}.", authentificateUser.Identity.Name));
			}
		} // proc AuthentificateUser

		public T GetUser<T>()
			where T : class
		{
			if (user == null)
				throw new HttpResponseException(HttpStatusCode.Unauthorized, "Authorization expected.");

			T r = user as T;
			if (r == null)
				throw new NotImplementedException(String.Format("User class does not implement '{0}.", typeof(T).FullName));

			return r;
		} // func GetUser

		#endregion

		#region -- Parameter --------------------------------------------------------------

		private static string GetNameValueKeyIgnoreCase(NameValueCollection list, string name)
		{
			var value = list[name];
			if (value == null)
			{
				name = list.AllKeys.FirstOrDefault(c => String.Compare(name, c, StringComparison.OrdinalIgnoreCase) == 0);
				if (name != null)
					value = list[name];
			}
			return value;
		} // func GetNameValueKeyIgnoreCase

		public bool TryGetProperty(string name, out object value)
		{
			// check for query parameter
			value = GetNameValueKeyIgnoreCase(queryString.Value, name);

			// check for header field
			if (value == null)
				value = GetNameValueKeyIgnoreCase(request.Headers, name);

			return value != null;
		} // func TryGetProperty

		/// <summary></summary>
		/// <param name="name"></param>
		/// <param name="default"></param>
		/// <returns></returns>
		public object GetProperty(string name, object @default)
			=> PropertyDictionaryExtensions.GetProperty(this, name, @default);

		public string[] ParameterNames => queryString.Value.AllKeys;
		public string[] HeaderNames => request.Headers.AllKeys;

		#endregion

		/// <summary>Client culture</summary>
		public CultureInfo CultureInfo => clientCultureInfo.Value;

		public string AbsolutePath => absolutePath;
		public DEHttpServer Http => http;
		IDEContextServer IDECommonContext.Server => http;
    public IDEAuthentificatedUser User => user;
  } // class DECommonContext

	#endregion

	#region -- class DEWebSocketContext -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DEWebSocketContext : DECommonContext, IDEWebSocketContext
	{
		private readonly HttpListenerWebSocketContext webSocketContext;

		public DEWebSocketContext(DEHttpServer http, HttpListenerContext context, HttpListenerWebSocketContext webSocketContext, string absolutePath)
			: base(http, context.Request, absolutePath)
		{
			this.webSocketContext = webSocketContext;

			AuthentificateUser(context.User);
		} // ctor
		
		public WebSocket WebSocket => webSocketContext.WebSocket;
	} // class DEWebSocketContext

	#endregion

	#region -- class DEHttpContext ------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DEHttpContext : DECommonContext, IDEContext
	{
		#region -- struct RelativeFrame ---------------------------------------------------

		private sealed class RelativeFrame
		{
			public RelativeFrame(int absolutePosition, IServiceProvider item)
			{
				this.AbsolutePosition = absolutePosition;
				this.Item = item;
			} // ctor

			public int AbsolutePosition { get; }
			public IServiceProvider Item { get; }
		} // struct RelativeFrame

		#endregion

		private readonly HttpListenerContext context;
		private LogMessageScopeProxy log = null;
		private bool httpAuthentification;

		private Stack<RelativeFrame> relativeStack = new Stack<RelativeFrame>();
		private string currentRelativeSubPath = null;
		private IServiceProvider currentRelativeSubNode = null;

		private bool isOutputSended = false;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEHttpContext(DEHttpServer http, HttpListenerContext context, string absolutePath, bool httpAuthentification)
			: base(http, context.Request, absolutePath)
		{
			this.context = context;
			this.httpAuthentification = httpAuthentification;

			// prepare response header
			context.Response.Headers["Server"] = "DES";

			// prepare log
			if (http.IsDebug)
				LogStart();
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				// close objects
				Procs.FreeAndNil(ref log);

				// close the context
				try { context.Response.Close(); }
				catch { }
			}
		} // proc Dispose

		#endregion

		#region -- Log --------------------------------------------------------------------

		public void LogStart()
		{
			if (log != null)
				return; // Log schon gestartet

			log = Http.LogProxy().GetScope(LogMsgType.Information, true, true);
			log.WriteLine("{0}: {1}", InputMethod, context.Request.Url);
			log.WriteLine();
			log.WriteLine("UrlReferrer: {0}", context.Request.UrlReferrer);
			log.WriteLine("UserAgent: {0}", context.Request.UserAgent);
			log.WriteLine("UserHostAddress: {0}", context.Request.UserHostAddress);
			log.WriteLine("Content-Type: {0}", InputContentType);
			log.WriteLine();
			log.WriteLine("Header:");
			var headers = context.Request.Headers;
			foreach (var k in headers.AllKeys)
				log.WriteLine("  {0}: {1}", k, headers[k]);
			log.WriteLine();
		} // proc LogStart

		public void LogStop()
		{
			Procs.FreeAndNil(ref log);
		} // proc LogStop

		public void Log(Action<LogMessageScopeProxy> action)
		{
			if (log != null)
				action(log);
		} // proc Log

		#endregion

		#region -- Security ---------------------------------------------------------------

		/// <summary>Darf der Nutzer, den entsprechenden Token verwenden.</summary>
		/// <param name="securityToken">Zu prüfender Token.</param>
		public void DemandToken(string securityToken)
		{
			if (!httpAuthentification || String.IsNullOrEmpty(securityToken))
				return;

			if (!TryDemandToken(securityToken))
				throw CreateAuthorizationException(securityToken);
		} // proc DemandToken

		/// <summary>Darf der Nutzer, den entsprechenden Token verwenden.</summary>
		/// <param name="securityToken">Zu prüfender Token.</param>
		/// <returns><c>true</c>, wenn der Token erlaubt ist.</returns>
		public bool TryDemandToken(string securityToken)
		{
			if (!httpAuthentification)
				return true;
			if (String.IsNullOrEmpty(securityToken))
				return true;

			return User != null && User.IsInRole(securityToken);
		} // proc TryDemandToken

		public HttpResponseException CreateAuthorizationException(string securityText) // force user, if no user is given
		 => new HttpResponseException(User == null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, String.Format("User {0} is not authorized to access token '{1}'.", User == null ? "Anonymous" : User.Identity.Name, securityText));

		#endregion

		#region -- Input ------------------------------------------------------------------

		public Stream GetInputStream()
		{
			var encoding = context.Request.Headers["Content-Encoding"];
			if (encoding != null && encoding.IndexOf("gzip") >= 0)
				return new GZipStream(context.Request.InputStream, CompressionMode.Decompress);
			else
				return context.Request.InputStream;
		} // func GetInputStream

		public TextReader GetInputTextReader()
			=> new StreamReader(GetInputStream(), InputEncoding);

		public string[] AcceptedTypes => context.Request.AcceptTypes;

		public string InputMethod => context.Request.HttpMethod;
		public string InputContentType => context.Request.ContentType;
		public Encoding InputEncoding => context.Request.ContentEncoding;
		public long InputLength => context.Request.ContentLength64;
		public CookieCollection InputCookies => context.Request.Cookies;

		public bool HasInputData => context.Request.HasEntityBody;

		#endregion

		#region -- Output -----------------------------------------------------------------

		private void CheckOutputSended()
		{
			if (isOutputSended)
				throw new InvalidOperationException("Output stream is already started.");
		} // proc CheckOutputSended

		public Stream GetOutputStream(string contentType, long contentLength = -1, bool? compress = null)
		{
			CheckOutputSended();

			if (String.IsNullOrEmpty(contentType))
				throw new ArgumentNullException("contentType");

			// is the content tpe marked
			var p = contentType.IndexOf(";gzip", StringComparison.OrdinalIgnoreCase);
			if (p >= 0)
			{
				contentType = contentType.Remove(p, 5);
				compress = true;
			}

			// set default value
			if (!compress.HasValue)
				compress = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

			// set the content type 
			context.Response.ContentType = contentType;

			// compressed streams allowed
			if (compress ?? false)
			{
				var acceptEncoding = context.Request.Headers["Accept-Encoding"];
				compress = acceptEncoding != null && acceptEncoding.IndexOf("gzip") >= 0;
			}
			else
				compress = false;

			// create the output stream
			isOutputSended = true;
			if (compress.Value)
			{
				context.Response.Headers["Content-Encoding"] = "gzip";
				return new GZipStream(context.Response.OutputStream, CompressionMode.Compress);
			}
			else
			{
				if (contentLength >= 0)
					context.Response.ContentLength64 = contentLength;
				return context.Response.OutputStream;
			}
		} // func GetOutputStream

		public TextWriter GetOutputTextWriter(string contentType, Encoding encoding = null, long contentLength = -1)
		{
			// add encoding to the content type
			contentType = contentType + "; charset=" + (encoding ?? Http.Encoding).WebName;
			return new StreamWriter(GetOutputStream(contentType, contentLength, contentLength == -1));
		} // func GetOutputTextWriter

		public void Redirect(string url)
		{
			CheckOutputSended();
			isOutputSended = true;
			context.Response.Redirect(url);
		} // proc Redirect

		public CookieCollection OutputCookies => context.Response.Cookies;
		public WebHeaderCollection OutputHeaders => context.Response.Headers;

		public bool IsOutputStarted => isOutputSended;

		#endregion

		#region -- Relative Path ----------------------------------------------------------

		/// <summary>Change into a virtual directory.</summary>
		/// <param name="subPath">Virtual subPath, should not start with a slash.</param>
		public bool TryEnterSubPath(IServiceProvider sp, string subPath)
		{
			if (sp == null || subPath == null)
				throw new ArgumentNullException();
			if (subPath.Length > 0 && subPath[0] == '/')
				throw new ArgumentException("Invalid subPath.", "subPath");

			// is the current item available for the current user
			var item = sp as IDEConfigItem;
			if (item != null)
				DemandToken(item.SecurityToken);

			// Prüfe die Position
			if (subPath.Length > 0 && !RelativeSubPath.StartsWith(subPath, StringComparison.OrdinalIgnoreCase))
				return false;

			// Erzeuge einen neuen Frame
			if (relativeStack.Count == 0)
				relativeStack.Push(new RelativeFrame(1, sp));
			else
			{
				var c = relativeStack.Peek();
				var p = c.AbsolutePosition + subPath.Length;
				if (AbsolutePath[p] == '/')
					p++;
				relativeStack.Push(new RelativeFrame(p, sp));
			}

			// clear cache
			RelativeCacheClear();

			return true;
		} // proc TryEnterSubPath

		/// <summary>Setzt den relativen Bezug zurück.</summary>
		public void ExitSubPath(IServiceProvider sp)
		{
			if (relativeStack.Count > 0)
			{
				var f = relativeStack.Peek();
				if (f.Item != sp)
					throw new ArgumentException("Invalid Stack.");

				relativeStack.Pop();
				RelativeCacheClear();
			}
			else
				throw new ArgumentException("Invalid Stack.");
		} // proc ExitSubPath

		private void RelativeCacheClear()
		{
			currentRelativeSubPath = null;
			currentRelativeSubNode = null;
		} // proc RelativeCacheClear

		/// <summary>Gibt den zugeordneten Knoten zurück.</summary>
		public IServiceProvider RelativeSubNode
		{
			get
			{
				if (currentRelativeSubNode == null)
				{
					if (relativeStack.Count == 0)
						currentRelativeSubNode = Http.Server;
					else
						currentRelativeSubNode = relativeStack.Peek().Item;
				}
				return currentRelativeSubNode;
			}
		} // prop RelativeNode

		/// <summary>Gibt den Relativen Pfad zurück</summary>
		public string RelativeSubPath
		{
			get
			{
				if (currentRelativeSubPath == null)
				{
					if (relativeStack.Count == 0)
						currentRelativeSubPath = AbsolutePath;
					else
						currentRelativeSubPath = AbsolutePath.Substring(relativeStack.Peek().AbsolutePosition);
				}
				return currentRelativeSubPath;
			}
		} // prop RelativePath

		public string RelativeSubName
		{
			get
			{
				if (RelativeSubPath == null)
					return null;

				var pos = RelativeSubPath.IndexOf('/');
				if (pos == -1)
					return RelativeSubPath;
				else
					return RelativeSubPath.Substring(0, pos);
			}
		} // prop RelativePathName

		/// <summary></summary>
		public IDEConfigItem CurrentNode => RelativeSubNode as IDEConfigItem;

		#endregion
	} // class DEHttpContext

	#endregion

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal class DEHttpServer : DEConfigLogItem, IDEHttpServer
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

		#region -- class CacheItemListDescriptor ------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class CacheItemListDescriptor : IDEListDescriptor
		{
			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType("item");
				xml.WriteProperty("@id", typeof(string));
				xml.WriteProperty("@hit", typeof(int));
				xml.WriteProperty("@type", typeof(string));
				xml.WriteProperty("@length", typeof(int));
				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object _item)
			{
				var item = (HttpCacheItem)_item;
				xml.WriteStartProperty("item");
				xml.WriteAttributeProperty("id", item.CacheId);
				xml.WriteAttributeProperty("hit", item.HitCount);

				var d = item.Data;
				if (d is string)
				{
					xml.WriteAttributeProperty("type", "text");
					xml.WriteAttributeProperty("length", ((String)d).Length);
				}
				else if (d is byte[])
				{
					xml.WriteAttributeProperty("type", "blob");
					xml.WriteAttributeProperty("length", ((byte[])d).Length);
				}
				else if (d is ILuaScript)
				{
					var c = ((ILuaScript)d).Chunk;
					xml.WriteAttributeProperty("type", $"script[{c.ChunkName},{(c.HasDebugInfo ? "D" : "R")}]");
					xml.WriteAttributeProperty("length", c.Size);
				}
				else if (d != null)
					xml.WriteAttributeProperty("type", d.GetType().Name);

				xml.WriteEndProperty();
			} // CacheItemListDescriptor

			public static IDEListDescriptor Instance { get; } = new CacheItemListDescriptor();
		} // class CacheItemListDescriptor

		#endregion

		#region -- class CacheItemListController ------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class CacheItemListController : IDEListController
		{
			private DEHttpServer item;

			public CacheItemListController(DEHttpServer item)
			{
				this.item = item;
				this.item.RegisterList(Id, this, true);
			} // ctor

			public void Dispose()
			{
				this.item.UnregisterList(this);
			} // proc Dispose

			public IDisposable EnterReadLock()
			{
				Monitor.Enter(item.cacheItems);
				return new DisposableScope(() => Monitor.Exit(item.cacheItems));
			} // func EnterReadLock

			public IDisposable EnterWriteLock()
				=> EnterReadLock();

			public void OnBeforeList() { }

			public string Id => "tw_http_cache";
			public string DisplayName => "Http-Cache";
			public IDEListDescriptor Descriptor => CacheItemListDescriptor.Instance;

			public IEnumerable List => item.cacheItems.Where(c => !c.IsEmpty);
		} // class CacheItemListController

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

		private Encoding encoding = Encoding.UTF8;                // Encoding für die Textdaten von Datenströmen
		private HttpListener httpListener = new HttpListener();   // Zugriff auf den HttpListener
		private DEThreadList httpThreads = null;                  // Threads, die die Request behandeln

		private Dictionary<string, string> mimeInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Hält die Mime-Informationen
		private List<PrefixAuthentificationScheme> prefixAuthentificationSchemes = new List<PrefixAuthentificationScheme>(); // Mapped verschiedene Authentification-Schemas auf die Urls
		private List<PrefixPathTranslation> prefixPathTranslations = new List<PrefixPathTranslation>(); // Mapped den externen Pfad (URI) auf einen internen Pfad (Path)

		private bool debugMode = false;                           // Sollen detailiert die Request-Protokolliert werden
		private CultureInfo defaultCultureInfo;

		private HttpCacheItem[] cacheItems = new HttpCacheItem[256];
		private CacheItemListController cacheItemController;

		private DEList<IDEWebSocketProtocol> webSocketProtocols;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEHttpServer(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			httpThreads = new DEThreadList(this, "Http-Threads", "HTTP", ExecuteHttpRequest);

			ClearHttpCache();
			httpListener.AuthenticationSchemeSelectorDelegate = GetAuthenticationScheme;

			// Promote service
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDEHttpServer), this);

			defaultCultureInfo = CultureInfo.CurrentCulture;

			// create protocols
			this.webSocketProtocols = new DEList<IDEWebSocketProtocol>(this, "tw_websockets", "WebSockets");
			this.RegisterWebSocketProtocol((IDEWebSocketProtocol)this.Server); // register events

			cacheItemController = new CacheItemListController(this);
			PublishItem(new DEConfigItemPublicAction("clearCache") { DisplayName = "Clear http-cache" });
			PublishItem(new DEConfigItemPublicAction("debugOn") { DisplayName = "Http-Debug(on)" });
			PublishItem(new DEConfigItemPublicAction("debugOff") { DisplayName = "Http-Debug(off)" });
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
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

				Procs.FreeAndNil(ref webSocketProtocols);
				Procs.FreeAndNil(ref cacheItemController);
				Procs.FreeAndNil(ref httpThreads);
				try { Procs.FreeAndNil(ref httpListener); }
				catch { }
			}
			base.Dispose(disposing);
		} // proc Disposing

		#endregion

		#region -- Configuration ----------------------------------------------------------

		protected override void ValidateConfig(XElement config)
		{
			base.ValidateConfig(config);

			if (config.Elements(xnFiles).FirstOrDefault(x => String.Compare(x.GetAttribute("name", String.Empty), "des", StringComparison.OrdinalIgnoreCase) == 0) == null)
			{
				var currentAssembly = typeof(DEHttpServer).Assembly;
				var baseLocation = Path.GetDirectoryName(currentAssembly.Location);
				var alternativePaths = new string[]
					{
						Path.GetFullPath(Path.Combine(baseLocation, @"..\..\Resources\Http")),
						Path.GetFullPath(Path.Combine(baseLocation, @"..\..\..\ServerWebUI"))
					};

				var xFiles = new XElement(xnResources,
					new XAttribute("name", "des"),
					new XAttribute("displayname", "Data Exchange Server - Http"),
					new XAttribute("base", ""),
					new XAttribute("assembly", currentAssembly.FullName),
					new XAttribute("namespace", "TecWare.DE.Server.Resources.Http"),
					new XAttribute("priority", 100)
				);

				if (Directory.Exists(alternativePaths[0]))
				{
					xFiles.Add(new XAttribute("nonePresentAlternativeExtensions", ".map .ts")); // exception for debug files
					xFiles.Add(
						alternativePaths.Select(c => new XElement(xnAlternativeRoot, c))
					);
				}

				config.Add(xFiles);
			}
		} // proc ValidateConfig

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// clear prefixes, authentification schemes and mine infos
			prefixPathTranslations.Clear();
			prefixAuthentificationSchemes.Clear();
			mimeInfo.Clear();

			// set standard mime informations
			mimeInfo[".js"] = MimeTypes.Text.JavaScript;
			mimeInfo[".html"] = MimeTypes.Text.Html;
			mimeInfo[".css"] = MimeTypes.Text.Css;
			mimeInfo[".lua"] = MimeTypes.Text.Plain; // do not execute lua files
			mimeInfo[".png"] = MimeTypes.Image.Png;
			mimeInfo[".jpg"] = MimeTypes.Image.Jpeg;
			mimeInfo[".jpeg"] = MimeTypes.Image.Jpeg;
			mimeInfo[".gif"] = MimeTypes.Image.Gif;
			mimeInfo[".ico"] = MimeTypes.Image.Icon;
			mimeInfo[".xaml"] = MimeTypes.Application.Xaml;

			mimeInfo[".map"] = MimeTypes.Text.Json;
			mimeInfo[".ts"] = MimeTypes.Text.Plain;

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
							Log.LogMsg(LogMsgType.Warning, "http/mime has a invalid format (ext={0};mime={1}).", name, value);
					}
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, "<" + x.Name.LocalName + "> ignored.\n\n" + e.GetMessageString());
				}
			}

			// create the prefix list
			var prefixes = new List<string>();
			foreach (var prefix in prefixPathTranslations)
				prefix.AddHttpPrefix(prefixes);

			// do we need a restart of the listener
			var httpListenerPrefixesChanged = false;
			if (httpListener.IsListening && httpListener.Prefixes.Count == prefixes.Count)
			{
				var currentPrefixes = httpListener.Prefixes.ToArray();
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
					// Stop listener
					if (httpListener.IsListening)
						httpListener.Stop();
					httpListener.Prefixes.Clear();

					// re add prefixes
					foreach (string c in prefixes)
						httpListener.Prefixes.Add(c);
				}
			}

			var configNode = new XConfigNode(Server.Configuration[xnHttp], config.ConfigNew);

			// create the fixed worker
			httpThreads.Count = configNode.GetAttribute<int>("threads");
			// set a new realm
			httpListener.Realm = configNode.GetAttribute<string>("realm");
			// read the default encoding
			encoding = configNode.GetAttribute<Encoding>("encoding");
			// set the default user language
			defaultCultureInfo = configNode.GetAttribute<CultureInfo>("defaultUserLanguage");
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			// restart listener, if it was stopped during the configuration process
			lock (httpListener)
				try
				{
					// no prefixes set, set the default
					if (httpListener.Prefixes.Count == 0)
						httpListener.Prefixes.Add("http://localhost:8080/");

					// Start the listener
					if (!httpListener.IsListening)
						httpListener.Start();
				}
				catch (Exception e)
				{
					Server.LogMsg(EventLogEntryType.Error, e.GetMessageString());
				}
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Web Sockets ------------------------------------------------------------

		public void RegisterWebSocketProtocol(IDEWebSocketProtocol protocol)
		{
			using (webSocketProtocols.EnterWriteLock())
				webSocketProtocols.Add(protocol);
		} // proc RegisterWebSocketProtocol

		public void UnregisterWebSocketProtocol(IDEWebSocketProtocol protocol)
		{
			using (webSocketProtocols.EnterWriteLock())
				webSocketProtocols.Remove(protocol);
		} // proc UnregisterWebSocketProtocol

		#endregion

		#region -- Http Schnittstelle -----------------------------------------------------

		[
		DEConfigHttpAction("debugOn", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Turns the debug mode on.")
		]
		private XElement HttpDebugOnAction()
		{
			this.IsDebug = true;
			return new XElement("debug", IsDebug);
		} // proc HttpDebugAction

		[
		DEConfigHttpAction("debugOff", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Turns the debug mode off.")
		]
		private XElement HttpDebugOffAction()
		{
			this.IsDebug = false;
			return new XElement("debug", IsDebug);
		} // proc HttpDebugAction

		[
		DEConfigHttpAction("clearCache", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Löscht den aktuellen Cache")
		]
		private void HttpClearCacheAction()
		=> ClearHttpCache();

		#endregion

		#region -- MimeInfo ---------------------------------------------------------------

		public string GetContentType(string extension)
		{
			string contentType;
			lock (mimeInfo)
			{
				if (mimeInfo.TryGetValue(extension, out contentType))
					return contentType;
				else
					throw new ArgumentException(String.Format("No contentType defined for '{0}'.", extension), "extension");
			}
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
				{
					ProcessRequest(ctx);
				}
			}
			else
				DEThread.CurrentThread.WaitFinish(500);
		} // proc ExecuteHttpRequest

		private void ProcessRequest(HttpListenerContext ctx)
		{
			var url = ctx.Request.Url;

			// Find the prefix for a alternating path
			var pathTranslation = FindPrefix(prefixPathTranslations, url);
			string absolutePath;
			if (pathTranslation == null || pathTranslation.Prefix == null)
				absolutePath = url.AbsolutePath;
			else
				absolutePath = pathTranslation.Path + url.AbsolutePath.Substring(pathTranslation.PrefixPath.Length);

			var authentificationScheme = GetAuthenticationScheme(ctx.Request);

			if (ctx.Request.IsWebSocketRequest)
			{
				var subProtocol = GetWebSocketProtocol(absolutePath, Procs.ParseMultiValueHeader(ctx.Request.Headers["Sec-WebSocket-Protocol"]).ToArray());
				if (subProtocol != null)
				{
					var webSocketContext = ctx.AcceptWebSocketAsync(subProtocol.Protocol).Result;
					if (!subProtocol.AcceptWebSocket(new DEWebSocketContext(this, ctx, webSocketContext, absolutePath)))
						Task.Run(async () => await webSocketContext.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Endpoint failure", CancellationToken.None));
				}
				else
				{
					var webSocket = ctx.AcceptWebSocketAsync(null).Result;
					Task.Run(async () => await webSocket.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Endpoint is not defined.", CancellationToken.None));
				}
			}
			else
			{
				using (var r = new DEHttpContext(this, ctx, absolutePath, authentificationScheme != AuthenticationSchemes.Anonymous))
				{
					try
					{
						// authentificate user
						r.AuthentificateUser(ctx.User);

						// Start logging
						if (debugMode)
							r.LogStart();

						// change the current cultur to the client culture
						Thread.CurrentThread.CurrentCulture = r.CultureInfo;
						Thread.CurrentThread.CurrentUICulture = r.CultureInfo;

						// start to find the endpoint
						if (r.TryEnterSubPath(Server, String.Empty))
						{
							try
							{
								// try to map a node
								if (!ProcessRequestForConfigItem(r, (DEConfigItem)Server))
								{
									// Search all http worker nodes
									using (EnterReadLock())
									{
										if (!UnsafeProcessRequest(r))
											throw new HttpResponseException(HttpStatusCode.BadRequest, "Not processed");
									}
								}
							}
							finally
							{
								r.ExitSubPath(Server);
							}
						}

						// check the return value
						if (ctx.Request.HttpMethod != "OPTIONS" && ctx.Response.ContentType == null)
							throw new HttpResponseException(HttpStatusCode.NoContent, "No result defined.");
					}
					catch (Exception e)
					{
						// extract target exception
						var ex = e;
						while (ex is TargetInvocationException)
							ex = ex.InnerException;

						// check for a http exception
						var httpEx = ex as HttpResponseException;

						// Start logging, or stop
						if (httpEx != null && httpEx.Code == HttpStatusCode.Unauthorized)
							r.LogStop();
						else
						{
							r.LogStart();
							r.Log(l => l.WriteException(ex));
						}

						ctx.Response.StatusCode = httpEx != null ? (int)httpEx.Code : (int)HttpStatusCode.InternalServerError;
						ctx.Response.StatusDescription = FilterChar(ex.Message);
					}
				}
			}
		} // proc ProcessRequest

		private IDEWebSocketProtocol GetWebSocketProtocol(string absolutePath, string[] subProtocols)
		{
			lock (webSocketProtocols)
			{
				foreach (var p in webSocketProtocols)
				{
					// correct protocol
					if (Array.Exists(subProtocols, c => String.Compare(p.Protocol, c, StringComparison.OrdinalIgnoreCase) == 0))
					{
						if (p.BasePath.Length == 0 || absolutePath.StartsWith(p.BasePath, StringComparison.OrdinalIgnoreCase))
							return p;
					}
				}
			}

			return null;
		} // func GetWebSocketProtocol

		private bool ProcessRequestForConfigItem(IDEContext r, DEConfigItem current)
		{
			using (current.EnterReadLock())
			{
				// Search zum Nodes
				foreach (var cur in current.UnsafeChildren.Where(c => r.TryEnterSubPath(c, c.Name)))
					try
					{
						if (ProcessRequestForConfigItem(r, cur))
							return true;
					}
					finally
					{
						r.ExitSubPath(cur);
					}

				// 1. Check for a defined action of this node
				string actionName;
				if (r.RelativeSubPath.Length == 0 && !String.IsNullOrEmpty(actionName = r.GetProperty("action", String.Empty)))
				{
					if (actionName == "lines" || actionName == "states" || actionName == "events")
						r.LogStop();

					current.UnsafeInvokeHttpAction(actionName, r);
					return true;
				}
				// 2. process the current node
				else if (current.UnsafeProcessRequest(r))
					return true;
				else // 3. check for http worker
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

		public override string Icon { get { return "/images/http16.png"; } }

		/// <summary>Encodierung für Textdateien</summary>
		public Encoding Encoding { get { return encoding; } }

		[
		PropertyName("tw_http_debugmode"),
		DisplayName("Protokollierung"),
		Category("Http"),
		Description("Ist die Protokollierung der Http-Request aktiv."),
		]
		public bool IsDebug { get { return debugMode; } private set { SetProperty(ref debugMode, value); } }
	
		/// <summary></summary>
		public CultureInfo DefaultCultureInfo => defaultCultureInfo;
  } // class DEHttpServer
}
