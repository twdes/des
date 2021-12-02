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
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- class DECommonWebContext -------------------------------------------------

	/// <summary></summary>
	internal abstract class DECommonWebScope : DECommonScope
	{
		private readonly DEHttpServer http;
		private readonly Lazy<NameValueCollection> queryString;
		private readonly HttpListenerRequest request;
		private readonly string absolutePath;

		private readonly Lazy<CultureInfo> clientCultureInfo;

		#region -- Ctor/Dtor ----------------------------------------------------------

		protected DECommonWebScope(DEHttpServer http, HttpListenerRequest request, string absolutePath, bool httpAuthentification, string allowGroups)
			: base(http, httpAuthentification, allowGroups)
		{
			this.http = http;
			this.request = request;
			this.absolutePath = absolutePath;

			// fix: request.QueryString uses wrong encoding
			queryString = new Lazy<NameValueCollection>(() => HttpUtility.ParseQueryString(request.Url.Query, Encoding.UTF8));

			clientCultureInfo = new Lazy<CultureInfo>(() =>
			{
				try
				{
					var userLanguages = request.UserLanguages;
					return userLanguages == null || userLanguages.Length == 0
						? http.DefaultCultureInfo
						: CultureInfo.GetCultureInfo(userLanguages[0]);
				}
				catch
				{
					return http.DefaultCultureInfo;
				}
			}
			);
		} // ctor

		#endregion

		#region -- TryGetProperty -----------------------------------------------------

		private bool TryGetNameValueKeyIgnoreCase(NameValueCollection list, string name, out object value)
		{
			value = list[name];
			if (value == null)
			{
				name = list.AllKeys.FirstOrDefault(c => String.Compare(name, c, StringComparison.OrdinalIgnoreCase) == 0);
				if (name != null)
					value = list[name];
			}
			return value != null;
		} // func TryGetNameValueKeyIgnoreCase

		public override bool TryGetProperty(string name, out object value)
		{
			if (TryGetNameValueKeyIgnoreCase(queryString.Value, name, out value)
				|| TryGetNameValueKeyIgnoreCase(request.Headers, name, out value))
				return true;
			return base.TryGetProperty(name, out value);
		} // func TryGetProperty

		public override Exception CreateAuthorizationException(bool isRestrictedToken, string message)
			=> new HttpResponseException(User == null && isRestrictedToken ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, message);

		#endregion

		public Uri GetOrigin(Uri relativeUri)
			=> new Uri(request.Url, relativeUri);

		/// <summary>Parameter names</summary>
		public string[] ParameterNames => queryString.Value.AllKeys;
		/// <summary>Header names</summary>
		public string[] HeaderNames => request.Headers.AllKeys;
		
		/// <summary>Client culture</summary>
		public override CultureInfo CultureInfo => clientCultureInfo.Value;
		/// <summary>Request path, absolute</summary>
		public string AbsolutePath => absolutePath;

		/// <summary>Is this a local request.</summary>
		public bool IsLocal => request.IsLocal;
		/// <summary>Orgination of the request.</summary>
		public IPEndPoint RemoteEndPoint => request.RemoteEndPoint;
		
		/// <summary>Access to the http server</summary>
		public IDEHttpServer Http => http;
	} // class DECommonContext

	#endregion

	#region -- class DEWebSocketContext -------------------------------------------------

	/// <summary></summary>
	internal sealed class DEWebSocketContext : DECommonWebScope, IDEWebSocketScope
	{
		private readonly HttpListenerContext context;
		private HttpListenerWebSocketContext webSocketContext;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEWebSocketContext(DEHttpServer http, HttpListenerContext context, string absolutePath, bool httpAuthentification, string allowGroups)
			: base(http, context.Request, absolutePath, httpAuthentification, allowGroups)
		{
			this.context = context ?? throw new ArgumentNullException(nameof(context));
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing && webSocketContext != null)
				{
					if (webSocketContext.WebSocket != null)
					{
						if (webSocketContext.WebSocket.State == WebSocketState.Open)
							webSocketContext.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed.", CancellationToken.None).AwaitTask();

						webSocketContext.WebSocket.Dispose();
					}
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		internal async Task AcceptWebSocketAsync(string protocol)
			=> webSocketContext = await context.AcceptWebSocketAsync(protocol);

		/// <summary>Returns the websocket</summary>
		public WebSocket WebSocket => webSocketContext.WebSocket;
	} // class DEWebSocketContext

	#endregion

	#region -- class DEWebRequestScope --------------------------------------------------

	/// <summary></summary>
	internal sealed class DEWebRequestScope : DECommonWebScope, IDEWebRequestScope
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

		private readonly Stack<RelativeFrame> relativeStack = new Stack<RelativeFrame>();
		private string currentRelativeSubPath = null;
		private IServiceProvider currentRelativeSubNode = null;

		private bool isOutputSended = false;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEWebRequestScope(DEHttpServer http, HttpListenerContext context, string absolutePath, bool httpAuthentification, string allowGroups)
			: base(http, context.Request, absolutePath, httpAuthentification, allowGroups)
		{
			this.context = context;

			// prepare response header
			context.Response.Headers["Server"] = "DES";

			// prepare log
			if (http.IsDebug)
				LogStart();
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					// close objects
					Procs.FreeAndNil(ref log);

					// close the context
					try { context.Response.Close(); }
					catch { }
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		#region -- Log --------------------------------------------------------------------

		public void LogStart()
		{
			if (log != null)
				return; // Log schon gestartet

			log = ((DEHttpServer)Http).LogProxy().CreateScope(LogMsgType.Information, true, true);
			log.WriteLine("{0}: {1}", InputMethod, context.Request.Url);
			log.WriteLine();
			log.WriteLine("UrlReferrer: {0}", context.Request.UrlReferrer);
			log.WriteLine("UserAgent: {0}", context.Request.UserAgent);
			log.WriteLine("UserHostAddress: {0} ({1})", context.Request.UserHostAddress, context.Request.RemoteEndPoint?.Address?.ToString());
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
			log?.AutoFlush(false);
			Procs.FreeAndNil(ref log);
		} // proc LogStop
		
		public void Log(Action<LogMessageScopeProxy> action)
		{
			if (log != null)
				action(log);
		} // proc Log

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

		private const long bufferFlushSize = 0x10000L;
		private const int outputStreamChunkedBorder = 0x40000;
		private static readonly Regex encodingValueRegex = new Regex(@"(?<n>\w+)(;q=(?<q>\d+.?\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
		private static readonly string[] compressTags = new string[] { "gzip", "deflate" };

		#region -- class HttpOutputBufferStream -------------------------------------------

		private sealed class HttpOutputBufferStream : Stream
		{
			private readonly HttpListenerResponse response;
			private readonly long contentLength;
			private readonly bool isCompressed;

			private readonly byte[] bufferArray = new byte[(int)bufferFlushSize];
			private Stream outputStream = null;
			private long position = 0;

			public HttpOutputBufferStream(HttpListenerResponse response, bool isCompressed, long contentLength)
			{
				this.response = response ?? throw new ArgumentNullException(nameof(response));
				this.isCompressed = isCompressed;
				this.contentLength = contentLength;
			} // ctor

			protected override void Dispose(bool disposing)
			{
				if (!IsBufferFlushed)
					FlushBuffer(true);
				outputStream.Dispose();
				
				base.Dispose(disposing);
			} // proc Dispose

			private void FlushBuffer(bool onClose)
			{
				if (outputStream != null)
					throw new InvalidOperationException();

				// update content header fields
				if (onClose) // is on close, and all data is in buffer, we will calcuate the length for the header
				{	
					response.ContentLength64 = position;
					if (isCompressed && contentLength > 0)
						response.Headers["des-content-size"] = contentLength.ToString();
					response.SendChunked = false;
				}
				else // more data is incoming, content-length can not be set if compressed
				{
					if (isCompressed || contentLength < 0 || contentLength > outputStreamChunkedBorder)
						SetResponseToChunked(response, contentLength);
					else
						response.ContentLength64 = contentLength; // send one block of data
				}

				// start output to http
				outputStream = response.OutputStream;
				outputStream.Write(bufferArray, 0, (int)position);
			} // proc FlushBuffer

			public override Task FlushAsync(CancellationToken cancellationToken)
			{
				return IsBufferFlushed
					? outputStream.FlushAsync(cancellationToken)
					: Task.CompletedTask;
			} // proc FlushAsync

			public override void Flush()
			{
				if (IsBufferFlushed)
					outputStream.Flush();
			} // proc Flush

			public override void Write(byte[] buffer, int offset, int count)
			{
				if (IsBufferFlushed) // buffer flushed, write direct to output
				{
					outputStream.Write(buffer, offset, count);
				}
				else if (position + count > bufferFlushSize) // not enough memory to buffer 
				{
					FlushBuffer(false);
					outputStream.Write(buffer, offset, count);
				}
				else // write into buffer
				{
					Array.Copy(buffer, offset, bufferArray, position, count);
				}

				position += count;
			} // proc Write

			public override int Read(byte[] buffer, int offset, int count) 
				=> throw new NotSupportedException();

			public override long Seek(long offset, SeekOrigin origin)
				=> throw new NotSupportedException();

			public override void SetLength(long value)
			{
				if (value != position)
					throw new NotSupportedException();
			} // proc SetLength

			public override long Position { get => position; set => throw new NotSupportedException(); }

			public override bool CanRead => false;
			public override bool CanWrite => true;
			public override bool CanSeek => false;
			public override long Length => position;

			private bool IsBufferFlushed => outputStream != null;
		} // class HttpOutputBufferStream

		#endregion

		private void CheckOutputSended()
		{
			if (isOutputSended)
				throw new InvalidOperationException("Output stream is already started.");
		} // proc CheckOutputSended

		private Encoding GetOutputEncoding()
		{
			if (this.TryGetProperty<string>("Accept-Charset", out var charset))
			{
				try
				{
					return Encoding.GetEncoding(charset);
				}
				catch (ArgumentException) { }
			}
			return Http.DefaultEncoding;
		} // func GetOutputEncoding

		private static int CheckForEncoding(string acceptEncoding, string[] names)
		{
			var currentIndex = -1;
			var currentQuality = 0.0f;

			if (acceptEncoding != null && names.Length > 0)
			{
				foreach (var c in acceptEncoding.Split(','))
				{
					var m = encodingValueRegex.Match(c);
					if (m.Success)
					{
						var idx = Array.FindIndex(names, c => String.Compare(c, m.Groups["n"].Value, StringComparison.OrdinalIgnoreCase) == 0);
						if (idx >= 0)
						{
							var q = m.Groups["q"].Length > 0 ? Single.Parse(m.Groups["q"].Value) : 1.0f;
							if (q > currentQuality)
							{
								currentQuality = q;
								currentIndex = idx;
							}
						}
					}
				}
			}

			return currentIndex;
		} // func CheckForEncoding

		private static bool TryGetAllowCompression(ref string contentType)
		{
			var p = contentType.IndexOf(";gzip", StringComparison.OrdinalIgnoreCase);
			if (p >= 0)
			{
				contentType = contentType.Remove(p, 5);
				return true;
			}
			return false;
		} // func TryGetAlowCompression

		private static void SetResponseToChunked(HttpListenerResponse response, long contentLength)
		{
			if (contentLength >= 0)
				response.Headers["des-content-size"] = contentLength.ToString();
			response.SendChunked = true; // send all data chunked
		} // proc SetResponseToChunked

		public Stream GetOutputStream(string contentType, long contentLength = -1)
		{
			CheckOutputSended();

			if (String.IsNullOrEmpty(contentType))
				throw new ArgumentNullException(nameof(contentType));

			// is the content type marked
			var allowCompression = TryGetAllowCompression(ref contentType)
				|| !MimeTypeMapping.GetIsCompressedContent(contentType); // set default value from content type

			// set the content type 
			context.Response.ContentType = contentType;

			// compressed streams allowed
			var compressIndex = allowCompression
				? CheckForEncoding(context.Request.Headers["Accept-Encoding"], compressTags)
				: -1;

			// create the output stream
			isOutputSended = true;
			if (IsHeadRequest)
			{
				if (compressIndex >= 0)
					context.Response.Headers["Content-Encoding"] = compressTags[compressIndex];
				return null;
			}
			else if (compressIndex == -1 && contentLength >= 0) // is not compressed, and content-length is set, we do not need a buffer
			{
				if (contentLength < outputStreamChunkedBorder)
					SetResponseToChunked(context.Response, contentLength);
				else
					context.Response.ContentLength64 = contentLength;

				return context.Response.OutputStream;
			}
			else // we wait for the first bytes, to test which method is the best
			{
				var returnStream = (Stream)new HttpOutputBufferStream(context.Response, compressIndex >= 0, contentLength);
				switch (compressIndex)
				{
					case 0:
						returnStream = new GZipStream(returnStream, CompressionMode.Compress, false);
						context.Response.Headers["Content-Encoding"] = compressTags[0];
						break;
					case 1:
						returnStream = new DeflateStream(returnStream, CompressionMode.Compress, false);
						context.Response.Headers["Content-Encoding"] = compressTags[1];
						break;
				}
				return returnStream;
			}
		} // func GetOutputStream

		public TextWriter GetOutputTextWriter(string contentType, Encoding encoding = null, long contentLength = -1)
		{
			encoding ??= OutputEncoding;
		
			// add encoding to the content type
			contentType = contentType + "; charset=" + encoding.WebName;
			var outputStream = GetOutputStream(contentType, contentLength);

			if (encoding == Encoding.UTF8)
				encoding = Procs.Utf8Encoding;
			return outputStream == null ? null : new StreamWriter(outputStream, encoding);
		} // func GetOutputTextWriter

		public void Redirect(string url, string statusDescription = null)
		{
			if (String.IsNullOrEmpty(url))
				throw new ArgumentNullException(nameof(url));

			CheckOutputSended();
			isOutputSended = true;

			Uri uri;
			bool externRedirect;
			if (url[0] == '/') // absolute same server
			{
				uri = new Uri(new Uri(context.Request.Url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), UriKind.Absolute), url);
				externRedirect = false;
			}
			else
			{
				var p = url.IndexOf("://");
				if (p > 0 && p < 10) // external server
				{
					uri = new Uri(url, UriKind.Absolute);
					externRedirect = true;
				}
				else // relative redirect
				{
					uri = new Uri(context.Request.Url, url);
					externRedirect = false;
				}
			}

			context.Response.Headers[HttpResponseHeader.Location] = 
				externRedirect ? uri.ToString() : uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
			context.Response.StatusCode = 301;
			context.Response.StatusDescription = Procs.FilterHttpStatusDescription(statusDescription) ?? "Redirect";
		} // proc Redirect

		public void SetStatus(HttpStatusCode statusCode, string statusDescription)
		{
			CheckOutputSended();
			isOutputSended = true;

			context.Response.StatusCode = (int)statusCode;
			context.Response.StatusDescription = statusDescription ?? statusCode.ToString();
		} // proc SetStatus

		public bool IsHeadRequest => context.Request.HttpMethod == HttpMethod.Head.Method;
		public CookieCollection OutputCookies => context.Response.Cookies;
		public WebHeaderCollection OutputHeaders => context.Response.Headers;

		public Encoding OutputEncoding => GetOutputEncoding();
		public bool IsOutputStarted => isOutputSended;

		#endregion

		#region -- Relative Path ----------------------------------------------------------

		/// <summary>Change into a virtual directory.</summary>
		/// <param name="sp">Service provider, that represents the sub-path.</param>
		/// <param name="subPath">Virtual sub-path, should not start with a slash.</param>
		/// <returns><c>true</c>, if the subpath was entered.</returns>
		public bool TryEnterSubPath(IServiceProvider sp, string subPath)
		{
			if (sp == null)
				throw new ArgumentNullException(nameof(sp));
			if (subPath == null)
				throw new ArgumentNullException(nameof(subPath));

			if (subPath.Length > 0 && subPath[0] == '/')
				throw new ArgumentException("Invalid subPath.", nameof(subPath));

			// check the path position
			if (subPath.Length > 0 && !RelativeSubPath.StartsWith(subPath, StringComparison.OrdinalIgnoreCase))
				return false;

			// is the current item available for the current user
			if (sp is IDEConfigItem item)
				DemandToken(item.SecurityToken);

			// create a new frame for stack 
			if (relativeStack.Count == 0)
				relativeStack.Push(new RelativeFrame(1, sp));
			else
			{
				var c = relativeStack.Peek();
				var p = c.AbsolutePosition + subPath.Length;
				if (p < AbsolutePath.Length && AbsolutePath[p] == '/')
					p++;
				relativeStack.Push(new RelativeFrame(p, sp));
			}

			// clear cache
			RelativeCacheClear();

			return true;
		} // proc TryEnterSubPath

		/// <summary>Exit the sub-path.</summary>
		/// <param name="sp">Service provider, that represents the sub-path.</param>
		public void ExitSubPath(IServiceProvider sp)
		{
			if (relativeStack.Count > 0)
			{
				var f = relativeStack.Peek();
				if (f.Item != sp)
					throw new ArgumentException("Invalid Stack.", nameof(sp));

				relativeStack.Pop();
				RelativeCacheClear();
			}
			else
				throw new ArgumentException("Invalid Stack.", nameof(sp));
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
						currentRelativeSubNode = Server;
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
				return pos == -1
					? RelativeSubPath
					: RelativeSubPath.Substring(0, pos);
			}
		} // prop RelativePathName

		/// <summary></summary>
		public IDEConfigItem CurrentNode => RelativeSubNode as IDEConfigItem;

		#endregion

		public HttpListenerContext Context => context;
	} // class DEWebRequestScope

	#endregion

	#region -- class DEHttpServer -----------------------------------------------------

	/// <summary></summary>
	internal class DEHttpServer : DEConfigLogItem, IDEHttpServer
	{
		#region -- struct HttpCacheItem -----------------------------------------------

		private struct HttpCacheItem
		{
			public void Set(string cacheId, object data)
			{
				HitCount = 0;
				CacheId = cacheId;
				Data = data;
			} // proc Set

			public bool Hit(string _cacheId)
			{
				if (HitCount >= 0 && String.Compare(CacheId, _cacheId, true) == 0)
				{
					HitCount++;
					return true;
				}
				else
					return false;
			} // func Hit

			public void Clear()
			{
				HitCount = -1;
				CacheId = null;
				Data = null;
			} // proc Clear

			public bool IsEmpty => HitCount < 0;

			public string CacheId { get; private set; }
			public int HitCount { get; private set; }

			public object Data { get; private set; }
		} // struct HttpCacheItem

		#endregion

		#region -- class CacheItemListDescriptor --------------------------------------

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
				if (d is string s)
				{
					xml.WriteAttributeProperty("type", "text");
					xml.WriteAttributeProperty("length", s.Length);
				}
				else if (d is byte[] b)
				{
					xml.WriteAttributeProperty("type", "blob");
					xml.WriteAttributeProperty("length", b.Length);
				}
				else if (d is ILuaScript script)
				{
					var c = script.Chunk;
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

		#region -- class CacheItemListController --------------------------------------

		private sealed class CacheItemListController : IDEListController
		{
			private readonly DEHttpServer item;

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
			public string SecurityToken => DEConfigItem.SecuritySys;
			public IDEListDescriptor Descriptor => CacheItemListDescriptor.Instance;

			public IEnumerable List => item.cacheItems.Where(c => !c.IsEmpty);
		} // class CacheItemListController

		#endregion

		#region -- class PrefixDefinition ---------------------------------------------

		private abstract class PrefixDefinition
		{
			private readonly string protocol;
			private readonly string hostname;
			private readonly int port;
			private readonly string relativeUriPath;

			public PrefixDefinition(XElement x, bool allowFileName)
			{
				var uri = x.Value;

				if (String.IsNullOrEmpty(uri))
				{
					this.protocol = null;
				}
				else
				{
					if (uri[uri.Length - 1] != '/')
					{
						if (allowFileName)  // cut to a relative path
						{
							var p = uri.LastIndexOf('/');
							if (p == -1)
								throw new DEConfigurationException(x, "A prefix must and on '/'.");
							SetFileName(uri.Substring(p + 1));
							uri = uri.Substring(0, p + 1);
						}
						else
							throw new DEConfigurationException(x, "A prefix must and on '/'.");
					}

					// Prüfe das Protokoll
					var pos = uri.IndexOf("://");
					if (pos == -1)
						throw new DEConfigurationException(x, "Protocol was not parsable (missing '://').");

					this.protocol = uri.Substring(0, pos);
					if (protocol != "http" &&
						protocol != "https")
						throw new DEConfigurationException(x, $"Unknown protocol ({protocol}), only http, https allowed.");

					// Teste den Hostnamen
					var startAt = pos + 3;
					pos = uri.IndexOfAny(new char[] { ':', '/' }, startAt);

					this.hostname = uri.Substring(startAt, pos - startAt);

					// Optionaler Port
					if (uri[pos] == ':')
					{
						startAt = pos + 1;
						pos = uri.IndexOf('/', startAt);

						if (!Int32.TryParse(uri.Substring(startAt, pos - startAt), out this.port))
							throw new DEConfigurationException(x, "Could not parse port.");
					}
					else
						this.port = 80;

					// Pfad
					this.relativeUriPath = uri.Substring(pos);
				}
			} // ctor

			public override string ToString()
				=> $"[{GetType().Name}] {protocol}://{hostname}:{port}/{relativeUriPath}";

			protected virtual void SetFileName(string fileName) { }

			/// <summary>Add the path to HttpListener prefix list.</summary>
			/// <param name="prefixes">Prefixes to add to the HttpListener</param>
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

			public virtual bool MatchPrefix(Uri url, bool ignoreProtocol)
			{
				if (protocol == null)
					return true; // default rule
				else if (!ignoreProtocol && url.Scheme != protocol)
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
			/// <summary>Length of the prefix</summary>
			public virtual int PrefixLength => protocol == null ? 0 : relativeUriPath.Length;

			#region -- class PrefixLengthComparerImpl ---------------------------------

			private sealed class PrefixLengthComparerImpl : IComparer<PrefixDefinition>
			{
				public int Compare(PrefixDefinition x, PrefixDefinition y)
					=> y.PrefixLength - x.PrefixLength; // reverse sort
			} // class PrefixLengthComparerImpl

			#endregion

			public static IComparer<PrefixDefinition> PrefixLengthComparer { get; } = new PrefixLengthComparerImpl();
		} // class PrefixDefinition

		#endregion

		#region -- class PrefixPathTranslation ----------------------------------------

		private sealed class PrefixPathTranslation : PrefixDefinition
		{
			private readonly string redirectPath;

			public PrefixPathTranslation(XElement x)
				: base(x, false)
			{
				redirectPath = x.GetAttribute("path", "/");
				if (String.IsNullOrEmpty(redirectPath) || redirectPath[0] != '/')
					throw new DEConfigurationException(x, "Invalid internal @path (must start with '/').");

				AllowGroups = x.GetAttribute("allowGroups", "*");
				IsHttpDebugOn = x.GetAttribute("debugOn", false);
			} // ctor

			public string Path => redirectPath;
			public string AllowGroups { get; }
			public bool IsHttpDebugOn { get; }
		} // class PrefixPathTranslation

		#endregion

		#region -- class PrefixAuthentificationScheme ---------------------------------

		private sealed class PrefixAuthentificationScheme : PrefixDefinition
		{
			private readonly AuthenticationSchemes scheme;
			private string fileName = null;

			public PrefixAuthentificationScheme(XElement x)
				: base(x, true)
			{
				var v = x.GetAttribute("scheme", String.Empty);
				if (String.IsNullOrEmpty(v))
					v = "none";

				var schemeValue = AuthenticationSchemes.None;
				foreach (var cur in v.Split(new char[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					schemeValue |= cur switch
					{
						"ntlm" => AuthenticationSchemes.Ntlm,
						"digest" => AuthenticationSchemes.Digest,
						"basic" => AuthenticationSchemes.Basic,
						"negotiate" => AuthenticationSchemes.Negotiate,
						"none" => AuthenticationSchemes.Anonymous,
						_ => throw new DEConfigurationException(x, $"Unknown authentification scheme ({v})."),
					};
				}

				this.scheme = schemeValue;
			} // ctor

			public override string ToString()
				=> fileName == null ? base.ToString() : base.ToString() + fileName;

			protected override void SetFileName(string fileName)
				=> this.fileName = fileName;
			
			public override bool MatchPrefix(Uri url, bool ignorePrefix)
			{
				var r = base.MatchPrefix(url, ignorePrefix);
				if (r && fileName != null)
				{
					r = url.AbsolutePath.Length == PrefixLength 
						&& url.AbsolutePath.Substring(PrefixPath.Length) == fileName;
				}
				return r;
			} // func MatchPrefix

			public AuthenticationSchemes Scheme => scheme;
			public override int PrefixLength => fileName == null ? base.PrefixLength : base.PrefixLength + fileName.Length;
		} // class PrefixAuthentificationScheme

		#endregion

		private readonly HttpListener httpListener = new HttpListener();	// Access to the HttpListener
		private readonly DEThread httpThread;								// Thread, that process requests
		private Uri defaultBaseUri = new Uri("http://localhost:8080/", UriKind.Absolute);

		private readonly List<PrefixAuthentificationScheme> prefixAuthentificationSchemes = new List<PrefixAuthentificationScheme>(); // Mapped verschiedene Authentification-Schemas auf die Urls
		private readonly List<PrefixPathTranslation> prefixPathTranslations = new List<PrefixPathTranslation>(); // Mapped den externen Pfad (URI) auf einen internen Pfad (Path)

		private readonly HttpCacheItem[] cacheItems = new HttpCacheItem[256];
		private CacheItemListController cacheItemController;

		private DEList<IDEWebSocketProtocol> webSocketProtocols;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public DEHttpServer(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
			httpThread = new DEThread(this, "Http-Dispatcher", ExecuteHttpRequestAsyc, "Http");

			ClearHttpCache();

			//httpListener.TimeoutManager.EntityBody = new TimeSpan(0, 2, 0, 0, 0);
			//httpListener.TimeoutManager.DrainEntityBody = new TimeSpan(0, 2, 0, 0, 0);
			//httpListener.TimeoutManager.RequestQueue = new TimeSpan(0, 2, 0, 0, 0);
			//httpListener.TimeoutManager.HeaderWait = new TimeSpan(0, 2, 0, 0, 0);
			//httpListener.TimeoutManager.IdleConnection = new TimeSpan(0, 2, 0, 0, 0);

			httpListener.AuthenticationSchemeSelectorDelegate = GetAuthenticationScheme;

			// Promote service
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDEHttpServer), this);

			DefaultCultureInfo = CultureInfo.CurrentCulture;

			// create protocols
			this.webSocketProtocols = new DEList<IDEWebSocketProtocol>(this, "tw_websockets", "WebSockets");
			this.RegisterWebSocketProtocol((IDEWebSocketProtocol)this.Server); // register events

			cacheItemController = new CacheItemListController(this);
			PublishItem(new DEConfigItemPublicAction("clearCache") { DisplayName = "Clear http-cache" });
			PublishDebugInterface();
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				// Entferne die Promotion
				var sc = this.GetService<IServiceContainer>(true);
				sc.RemoveService(typeof(IDEHttpServer));

				lock (httpListener)
				{
					try
					{
						httpListener.Stop();
					}
					catch (Exception e)
					{
						Server.LogMsg(e);
					}
				}

				Procs.FreeAndNil(ref webSocketProtocols);
				Procs.FreeAndNil(ref cacheItemController);
				if (!httpThread.IsDisposed)
					httpThread.Dispose();
				try { httpListener.Close(); }
				catch { }
			}
			base.Dispose(disposing);
		} // proc Disposing

		#endregion

		#region -- Configuration ------------------------------------------------------

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// clear prefixes, authentification schemes and mine infos
			prefixPathTranslations.Clear();
			prefixAuthentificationSchemes.Clear();

			var defaultPrefix = (PrefixDefinition)null;

			foreach (var x in config.ConfigNew.Elements())
			{
				try
				{
					if (x.Name == xnHttpPrefix)
					{
						var p = AddPrefix(prefixPathTranslations, new PrefixPathTranslation(x));
						if (defaultPrefix == null)
							defaultPrefix = p;
						else if (p.Path == "/")
							defaultPrefix = p;
					}
					else if (x.Name == xnHttpAccess)
					{
						var p = AddPrefix(prefixAuthentificationSchemes, new PrefixAuthentificationScheme(x));
						if (defaultPrefix == null)
							defaultPrefix = p;
					}
					else if (x.Name == xnHttpMime) // Lade die Mime-Informationen
					{
						var name = x.GetAttribute("ext", String.Empty);
						var isPackedContent = x.GetAttribute("packed", false);
						var value = x.GetAttribute("mime", String.Empty);

						if (!String.IsNullOrEmpty(name) && !String.IsNullOrEmpty(value) && name[0] == '.')
							MimeTypeMapping.Update(value, isPackedContent, false, name);
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
				for (var i = 0; i < currentPrefixes.Length; i++)
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

					// change default prefix
					if (defaultPrefix != null)
					{
						var defaultUriString = Regex.Replace(defaultPrefix.Prefix, @"\:\/\/[\*\+]", "://" + Environment.MachineName);
						try
						{
							defaultBaseUri = new Uri(defaultUriString, UriKind.Absolute);
						}
						catch (UriFormatException)
						{
						}
					}

					// re add prefixes
					foreach (var c in prefixes)
						httpListener.Prefixes.Add(c);
				}
			}

			var configNode = XConfigNode.Create(Server.Configuration, config.ConfigNew);

			// set a new realm
			httpListener.Realm = configNode.GetAttribute<string>("realm");
			// read the default encoding
			DefaultEncoding = configNode.GetAttribute<Encoding>("encoding");
			// set the default user language
			DefaultCultureInfo = configNode.GetAttribute<CultureInfo>("defaultUserLanguage");
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			// restart listener, if it was stopped during the configuration process
			lock (httpListener)
			{
				var msg = new StringBuilder();

				try
				{
					// Start the listener
					if (httpListener.Prefixes.Count > 0 && !httpListener.IsListening)
					{
						msg.AppendLine("Http-Server start failed.");
						foreach (var p in httpListener.Prefixes)
							msg.AppendLine(p);
						msg.AppendLine();

						httpListener.Start();
					}
					else
						Server.LogMsg(EventLogEntryType.Error, "No prefixes defined (des/http/prefix is missing).");
				}
				catch (Exception e)
				{
					msg.AppendLine(e.GetMessageString());
					Server.LogMsg(EventLogEntryType.Error, msg.ToString());
				}
			}
		} // proc OnEndReadConfiguration

		protected override void ValidateConfig(XElement config)
		{
			base.ValidateConfig(config);

			if (!config.Elements(xnHttpPrefix).Any())
				config.AddFirst(new XElement(xnHttpPrefix, new XAttribute("path", "/"), "http://localhost:8080/"));
		} // proc ValidateConfig

		#endregion

		#region -- Web Sockets --------------------------------------------------------

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

		#region -- Http Schnittstelle -------------------------------------------------

		
		[
		DEConfigHttpAction("clearCache", SecurityToken = SecuritySys, IsSafeCall = true),
		Description("Clear current cached information."),
		LuaMember
		]
		internal void ClearCacheAction()
			=> ClearHttpCache();

		#endregion

		#region -- MimeInfo -----------------------------------------------------------

		public string GetContentType(string extension)
		{
			return TryGetContentType(extension, out var mimeType)
				? mimeType
				: throw new ArgumentException(String.Format("No contentType defined for '{0}'.", extension), "extension");
		} // func GetContentType

		public bool TryGetContentType(string extension, out string mimeType)
			=> MimeTypeMapping.TryGetMimeTypeFromExtension(extension, out mimeType);

		#endregion

		#region -- Web Cache ----------------------------------------------------------

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
				throw new ArgumentException("data muss string, byte[] oder ein script sein.", nameof(data));

			lock (cacheItems)
			{
				var freeIndex = -1;
				var minHitCount = -1;
				var minHitIndex = -1;

				// Scan den Cache
				for (var i = 0; i < cacheItems.Length; i++)
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

				// Set cached item
				if (freeIndex >= 0)
					cacheItems[freeIndex].Set(cacheId, data);
				else if (minHitIndex >= 0)
					cacheItems[minHitIndex].Set(cacheId, data);

				return true;
			}
		} // proc UpdateWebCache

		private void ClearHttpCache()
		{
			lock (cacheItems)
			{
				for (var i = 0; i < cacheItems.Length; i++)
					cacheItems[i].Clear();
			}
		} // proc ClearHttpCache

		#endregion

		#region -- ProcessRequest -----------------------------------------------------

		private AuthenticationSchemes GetAuthenticationScheme(HttpListenerRequest r)
		{
			var prefixScheme = FindPrefix(prefixAuthentificationSchemes, r.Url, true);
			if (prefixScheme == null)
				return AuthenticationSchemes.Anonymous;
			else
			{
				var allowMultipleAuthentification = r.Headers["des-multiple-authentifications"];
				if (Boolean.TryParse(allowMultipleAuthentification, out var t) && t)
					return prefixScheme.Scheme;
				else // standard browser's only accept one authentification scheme
				{
					var scheme = prefixScheme.Scheme;

					// set the requested scheme
					if ((scheme & AuthenticationSchemes.Basic) == AuthenticationSchemes.Basic && allowMultipleAuthentification == "basic")
						scheme = AuthenticationSchemes.Basic;
					else if ((scheme & AuthenticationSchemes.Ntlm) == AuthenticationSchemes.Ntlm && allowMultipleAuthentification == "ntml")
						scheme = AuthenticationSchemes.Ntlm;
					else
					{
						// select the best for the remote host (currently we try to use the highest sec level)
						if ((scheme & AuthenticationSchemes.IntegratedWindowsAuthentication) != 0)
							scheme &= AuthenticationSchemes.IntegratedWindowsAuthentication;
						else if ((scheme & AuthenticationSchemes.Digest) != 0)
							scheme = AuthenticationSchemes.Digest;
						else if ((scheme & AuthenticationSchemes.Basic) != 0)
							scheme = AuthenticationSchemes.Basic;
					}

					return prefixScheme == null ? AuthenticationSchemes.Anonymous : scheme;
				}
			}
		} // func GetAuthenticationScheme

		private async Task ExecuteHttpRequestAsyc(DEThread thread)
		{
			while (thread.IsRunning)
			{
				if (IsHttpListenerRunning)
				{
					HttpListenerContext ctx = null;
					try
					{
						ctx = await Task.Run(() => httpListener.GetContext()); // await httpListener.GetContextAsync();
					}
					catch (HttpListenerException e)
					{
						if (e.ErrorCode != 995)
							throw;
					}
					if (ctx != null)
					{
						// post message, and wait for more
						ProcessRequestAsync(ctx).GetAwaiter();
					}
				}
				else
					await Task.Delay(500);
			}
		} // proc ExecuteHttpRequest

		private async Task ProcessAcceptWebSocketAsync(HttpListenerContext ctx, string absolutePath, PrefixPathTranslation pathTranslation, AuthenticationSchemes authentificationScheme)
		{
			// search for the websocket endpoint
			var subProtocol = GetWebSocketProtocol(absolutePath, Procs.ParseMultiValueHeader(ctx.Request.Headers["Sec-WebSocket-Protocol"]).ToArray());
			if (subProtocol == null) // no endpoint registered -> close the socket
			{
				var webSocket = await ctx.AcceptWebSocketAsync(null);
				await webSocket.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Endpoint is not defined.", CancellationToken.None);
				webSocket.WebSocket.Dispose();
			}
			else
			{
				using (var context = new DEWebSocketContext(this, ctx, absolutePath, authentificationScheme != AuthenticationSchemes.Anonymous, pathTranslation.AllowGroups))
				{
					// start authentification
					await context.AuthentificateUserAsync(FixUserEncoding(ctx, ctx.User?.Identity));

					// authentificate the user
					context.DemandToken(SecurityUser);
					context.DemandToken(subProtocol.SecurityToken);

					// accept the protocol to the client
					await context.AcceptWebSocketAsync(subProtocol.Protocol);

					// accept the protocol to the server
					await subProtocol.ExecuteWebSocketAsync(context);
				}
			}
		} // func ProcessAcceptWebSocketAsync

		private async Task ProcessRequestAsync(HttpListenerContext ctx)
		{
			var url = ctx.Request.Url;

			// Find the prefix for a alternating path
			var pathTranslation = FindPrefix(prefixPathTranslations, url, false);
			if (pathTranslation == null) // no translation, no response
			{
				ctx.Response.StatusDescription = "Invalid prefix.";
				ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				ctx.Response.Close();
				return;
			}

			var absolutePath = pathTranslation.Path + url.AbsolutePath.Substring(pathTranslation.PrefixPath.Length);

			var authentificationScheme = GetAuthenticationScheme(ctx.Request);

			if (ctx.Request.IsWebSocketRequest)
			{
				try
				{
					await ProcessAcceptWebSocketAsync(ctx, absolutePath, pathTranslation, authentificationScheme);
				}
				catch (AggregateException e)
				{
					ProcessResponeOnException(ctx, e.InnerException, null);
				}
				catch (Exception e)
				{
					ProcessResponeOnException(ctx, e, null);
				}
			}
			else
			{
				var httpAuthentification = authentificationScheme != AuthenticationSchemes.Anonymous;
				using var context = new DEWebRequestScope(this, ctx, absolutePath, httpAuthentification, pathTranslation.AllowGroups);
				try
				{
					// authentificate user
					await context.AuthentificateUserAsync(FixUserEncoding(ctx, ctx.User?.Identity));
					if (httpAuthentification && context.TryDemandUser() == null) // we need a user, skip exception, for debugging reasons
					{
						ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
						ctx.Response.StatusDescription = "Authorization needed.";
						ctx.Response.Close();
					}
					else
					{
						// Ask for token again, might be restricted
						context.DemandToken(SecurityUser);

						// Start logging
						if (IsDebug || pathTranslation.IsHttpDebugOn)
							context.LogStart();

						// start to find the endpoint
						if (context.TryEnterSubPath(Server, String.Empty))
						{
							try
							{
								// try to map a node
								if (!await ProcessRequestForConfigItemAsync(context, (DEConfigItem)Server))
								{
									// Search all http worker nodes
									using (EnterReadLock())
									{
										if (!await UnsafeProcessRequestAsync(context))
											throw new HttpResponseException(HttpStatusCode.NotFound, "Requested resource not found.");
									}
								}
							}
							finally
							{
								context.ExitSubPath(Server);
							}
						}

						// check the return value
						if (!context.IsOutputStarted && ctx.Request.HttpMethod != "OPTIONS" && ctx.Response.ContentType == null)
							throw new HttpResponseException(HttpStatusCode.NoContent, "No result defined.");

						// commit all is fine!
						if (!context.IsCommitted.HasValue)
							await context.CommitAsync();
					}
				}
				catch (Exception e)
				{
					ProcessResponeOnException(ctx, e, context);
				}

				await context.DisposeAsync();
			}
		} // proc ProcessRequest

		private IIdentity FixUserEncoding(HttpListenerContext ctx, IIdentity identity)
		{
			// fix: HttpListener convert all string 8bit wise, but the authentification header is utf8 encoded.
			if (identity is HttpListenerBasicIdentity)
			{
				var auth = ctx.Request.Headers["Authorization"];
				if (auth != null && auth.StartsWith("Basic "))
				{
					var user = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring(6)));
					var p = user.IndexOf(':');
					if (p >= 0)
						return new HttpListenerBasicIdentity(user.Substring(0, p), user.Substring(p + 1));
				}
			}

			return identity;
		} // func FixUserEncoding

		private void ProcessResponeOnException(HttpListenerContext ctx, Exception e, DEWebRequestScope r)
		{
			// extract target exception
			var ex = e;
			while (ex is AggregateException)
				ex = ex.InnerException;
			while (ex is TargetInvocationException)
				ex = ex.InnerException;

			// check for a http exception
			var httpEx = ex as HttpResponseException;

			// Start logging, or stop
			if (httpEx != null && httpEx.StatusCode == HttpStatusCode.Unauthorized)
			{
				r?.LogStop();
			}
			else
			{
				if (r != null)
				{
					r.LogStart();
					r.Log(l => l.WriteException(ex));
				}
				else
				{
					Log.Except(e);
				}
			}

			// set utf8 encoded version of the message
			ctx.Response.AddHeader("x-reason-utf8", Convert.ToBase64String(Encoding.UTF8.GetBytes(ex.Message)));

			// set status
			ctx.Response.StatusCode = httpEx != null ? (int)httpEx.StatusCode : (int)HttpStatusCode.InternalServerError;
			ctx.Response.StatusDescription = Procs.FilterHttpStatusDescription(ex.Message);
			ctx.Response.Close();
		} // proc ProcessResponeOnException

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

		private async Task<bool> ProcessRequestForConfigItemAsync(IDEWebRequestScope r, DEConfigItem current)
		{
			using (current.EnterReadLock())
			{
				// Search zum Nodes
				foreach (var cur in current.UnsafeChildren.Where(c => r.TryEnterSubPath(c, c.Name)))
				{
					try
					{
						if (await ProcessRequestForConfigItemAsync(r, cur))
							return true;
					}
					finally
					{
						r.ExitSubPath(cur);
					}
				}

				// 1. Check for a defined action of this node
				string actionName;
				if (r.RelativeSubPath.Length == 0 && !String.IsNullOrEmpty(actionName = r.GetProperty("action", String.Empty)))
				{
					if (actionName == "listget")
					{
						var id = r.GetProperty("id", String.Empty);
						if (id == LogLineListId || id == PropertiesListId || id == ActionsListId || id == AttachedScriptsListId)
							r.LogStop();
					}
					else if (actionName == "serverinfo")
						r.LogStop();

					await current.UnsafeInvokeHttpActionAsync(actionName, r);
					return true;
				}
				// 2. process the current node
				else if (await current.UnsafeProcessRequestAsync(r))
					return true;
				else // 3. check for http worker
					return false;
			}
		} // func ProcessRequestForConfigItemAsync

		private static T AddPrefix<T>(List<T> prefixes, T add)
			where T : PrefixDefinition
		{
			lock (prefixes)
			{
				var p = prefixes.BinarySearch(add, PrefixDefinition.PrefixLengthComparer);
				if (p < 0)
					prefixes.Insert(~p, add);
				else
				{
					while (p < prefixes.Count && PrefixDefinition.PrefixLengthComparer.Compare(prefixes[p], add) == 0)
						p++;
					prefixes.Insert(p, add);
				}
			}

			return add;
		} // func AddPrefix

		private static T FindPrefix<T>(List<T> prefixes, Uri url, bool ignoreProtocol)
			where T : PrefixDefinition
		{
			lock (prefixes)
				return prefixes.Find(c => c.MatchPrefix(url, ignoreProtocol));
		} // func FindPrefix
				
		private bool IsHttpListenerRunning { get { lock (httpListener) return httpListener.IsListening; } }

		#endregion

		public override string Icon => "/images/http16.png";

		/// <summary>Default encoding for text requests.</summary>
		public Encoding DefaultEncoding { get; private set; } = Encoding.UTF8;
		
		/// <summary>Default base uri for e.g. debug requests.</summary>
		public Uri DefaultBaseUri => defaultBaseUri;
		/// <summary>Default culture</summary>
		public CultureInfo DefaultCultureInfo { get; private set; }
	} // class DEHttpServer

	#endregion
}
