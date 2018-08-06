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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Http
{
	#region -- interface IDEHttpServer ------------------------------------------------

	/// <summary></summary>
	public interface IDEHttpServer
	{
		/// <summary></summary>
		/// <param name="protocol"></param>
		void RegisterWebSocketProtocol(IDEWebSocketProtocol protocol);
		/// <summary></summary>
		/// <param name="protocol"></param>
		void UnregisterWebSocketProtocol(IDEWebSocketProtocol protocol);

		/// <summary>Fragt den Cache ab</summary>
		/// <param name="cacheId">Eindeutige Id des Cache-Eintrages</param>
		/// <returns>Gecachtes Objekt oder <c>null</c></returns>
		object GetWebCache(string cacheId);
		/// <summary>Trägt etwas in den Cache neu ein.</summary>
		/// <param name="cacheId">Eindeutige Id des Cache-Eintrages</param>
		/// <param name="cache">Objekt</param>
		/// <returns>Wurde der Eintrag in den Cache aufgenommen</returns>
		bool UpdateWebCache(string cacheId, object cache);

		/// <summary>Get a mime type to the given extension.</summary>
		/// <param name="extension">file extension</param>
		/// <returns>mime-type</returns>
		string GetContentType(string extension);

		/// <summary>Default uri for the service.</summary>
		Uri DefaultBaseUri { get; }
		/// <summary>Is debugging of the http server on.</summary>
		bool IsDebug { get; }

		/// <summary>Gets the default encoding for the requests.</summary>
		Encoding DefaultEncoding { get; }
		/// <summary>Default culture for the client requests.</summary>
		CultureInfo DefaultCultureInfo { get; }
	} // interface IDEHttpServer

	#endregion

	#region -- interface IDEWebSocketProtocol -----------------------------------------

	/// <summary></summary>
	public interface IDEWebSocketProtocol
	{
		/// <summary>Run the web-socket code. The current context is still the dispatcher. IDEWebSocketScope is not setted.</summary>
		/// <param name="webSocket"></param>
		Task ExecuteWebSocketAsync(IDEWebSocketScope webSocket);

		/// <summary>Basepath for the protocol within the server.</summary>
		[DEListTypeProperty("@base")]
		string BasePath { get; }
		/// <summary>Name of the protocol.</summary>
		[DEListTypeProperty("@protocol")]
		string Protocol { get; }
		/// <summary></summary>
		[DEListTypeProperty("@security")]
		string SecurityToken { get; }
	} // interface IDEWebSocketProtocol

	#endregion

	#region -- interface IDEWebScope --------------------------------------------------

	/// <summary></summary>
	public interface IDEWebScope : IDECommonScope
	{
		/// <summary>Access to the http server.</summary>
		IDEHttpServer Http { get; }
	} // interface IDEWebScope

	#endregion

	#region -- interface IDEWebSocketScope --------------------------------------------

	/// <summary></summary>
	public interface IDEWebSocketScope : IDEWebScope
	{
		/// <summary>Access to the websocket.</summary>
		WebSocket WebSocket { get; }

		/// <summary>The full request path.</summary>
		string AbsolutePath { get; }
	} // interface IDEWebSocketScope

	#endregion

	#region -- interface IDEWebRequestScope -------------------------------------------

	/// <summary></summary>
	public interface IDEWebRequestScope : IDEWebScope
	{
		/// <summary>Starts the logging.</summary>
		void LogStart();
		/// <summary>Cancels the logging.</summary>
		void LogStop();
		/// <summary>Writes the log, if it is started.</summary>
		/// <param name="action"></param>
		void Log(Action<LogMessageScopeProxy> action);

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="subPath"></param>
		/// <returns></returns>
		bool TryEnterSubPath(IServiceProvider sp, string subPath);
		/// <summary></summary>
		/// <param name="sp"></param>
		void ExitSubPath(IServiceProvider sp);

		/// <summary>Mime-types they are accepted from the client.</summary>
		string[] AcceptedTypes { get; }

		/// <summary>Method</summary>
		string InputMethod { get; }
		/// <summary>Mime-type of the input stream.</summary>
		string InputContentType { get; }
		/// <summary>The length of the input stream.</summary>
		long InputLength { get; }
		/// <summary>Encoding of text data.</summary>
		Encoding InputEncoding { get; }
		/// <summary>Cookies</summary>
		CookieCollection InputCookies { get; }
		/// <summary>Is the a input content.</summary>
		bool HasInputData { get; }

		/// <summary></summary>
		CookieCollection OutputCookies { get; }
		/// <summary></summary>
		WebHeaderCollection OutputHeaders { get; }
		/// <summary></summary>
		/// <returns></returns>
		Stream GetInputStream();
		/// <summary></summary>
		/// <returns></returns>
		TextReader GetInputTextReader();

		/// <summary></summary>
		/// <param name="contentType"></param>
		/// <param name="contentLength"></param>
		/// <param name="compress"></param>
		/// <returns></returns>
		Stream GetOutputStream(string contentType, long contentLength = -1, bool? compress = null);
		/// <summary></summary>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		/// <param name="contentLength"></param>
		/// <returns></returns>
		TextWriter GetOutputTextWriter(string contentType, Encoding encoding = null, long contentLength = -1);
		/// <summary>Sends a redirect.</summary>
		/// <param name="url"></param>
		void Redirect(string url);
		/// <summary>Set status code, and closes the output.</summary>
		/// <param name="statusCode"></param>
		/// <param name="statusDescription"></param>
		void SetStatus(HttpStatusCode statusCode, string statusDescription);
		/// <summary>Is there a call of GetOutputStream, GetOutputTextWriter or Redirect.</summary>
		bool IsOutputStarted { get; }

		/// <summary>Current node, on which the request is executed.</summary>
		IDEConfigItem CurrentNode { get; }

		/// <summary>Returns the available parameter</summary>
		string[] ParameterNames { get; }
		/// <summary>Returns the available parameter</summary>
		string[] HeaderNames { get; }

		/// <summary>Current path of the uri, relative to the current node.</summary>
		string RelativeSubPath { get; }
		/// <summary>Name of the current path position.</summary>
		string RelativeSubName { get; }
		/// <summary>Get the full request path.</summary>
		string AbsolutePath { get; }
	} // interface IDEWebRequestScope

	#endregion

	#region -- class LuaHtmlTable -----------------------------------------------------

	internal sealed class LuaHtmlTable : LuaTable, IDisposable
	{
		private readonly IDEWebRequestScope context;

		private string contentType; // content type to emit

		// output streams
		private Stream streamOutput = null;
		private TextWriter textOutput = null;
		private Encoding encoding = null;

		public LuaHtmlTable(IDEWebRequestScope context, string contentType)
		{
			this.context = context;
			this.contentType = contentType;
		} // ctor

		public void Dispose()
		{
			Procs.FreeAndNil(ref streamOutput);
			Procs.FreeAndNil(ref textOutput);
		} // proc Dispose

		private string ConvertForHtml(object value)
			=> ConvertForHtml(value, null);

		private string ConvertForHtml(object value, string fmt)
		{
			if (value is string s)
				return s;
			else if (!String.IsNullOrEmpty(fmt)
				&& value is IFormattable formattable)
				return formattable.ToString(fmt, context.CultureInfo);
			else
				return Convert.ToString(value, context.CultureInfo);
		} // func ConvertForHtml

		[LuaMember("printValue")]
		private void LuaPrintValue(object value, string fmt = null)
			=> LuaPrintText(ConvertForHtml(value, fmt));

		[LuaMember("print")]
		private void LuaPrint(params object[] values)
		{
			if (values == null || values.Length == 0)
				return;

			var text = values.Length == 1
				? ConvertForHtml(values[0])
				: String.Join(" ", values.Select(ConvertForHtml));

			LuaPrintText(text);
		} // proc OnPrint

		private void LuaPrintText(string text)
		{
			if (textOutput != null)
				textOutput.Write(text);
			else if (streamOutput != null)
			{
				var b = encoding.GetBytes(text);
				streamOutput.Write(b, 0, b.Length);
			}
			else
				throw new ArgumentException("out is not open.");
		} // proc LuaPrintText

		private void CheckOutputOpened()
		{
			if (textOutput != null || streamOutput != null)
				throw new ArgumentException("out is already open.");
		} // func CheckOutputOpened

		private void PrepareOutput()
		{
			CheckOutputOpened();

			if (encoding == null)
				encoding = context.InputEncoding ?? context.Http.DefaultEncoding;
		} // proc PrepareOutput

		[LuaMember("otext")]
		public void OpenText(string contentType = null, Encoding encoding = null)
		{
			PrepareOutput();
			textOutput = context.GetOutputTextWriter(contentType ?? this.contentType, encoding);
		} // proc OpenText

		[LuaMember("obinary")]
		private void OpenBinary(string contentType = null, Encoding encoding = null)
		{
			PrepareOutput();
			streamOutput = context.GetOutputStream(contentType ?? this.contentType);
		} // proc OpenBinary

		protected override object OnIndex(object key)
		{
			var r = base.OnIndex(key);
			if (r != null)
				return r;

			if (key is string memberName && context.TryGetProperty(memberName, out var tmp))
				return tmp;

			return context.CurrentNode is LuaTable t ? t.GetValue(key) : null;
		} // func OnIndex

		[LuaMember("ContentType")]
		public string ContentType
		{
			get => contentType;
			set
			{
				CheckOutputOpened();
				contentType = value;
			}
		} // prop ContentType

		[LuaMember("Context")]
		public IDEWebRequestScope Context { get => context; set { } }
		[LuaMember("out")]
		public object Output { get => (object)streamOutput ?? textOutput; set { } }
	} // class LuaHtmlTable

	#endregion

	#region -- class HttpResponseHelper -----------------------------------------------

	/// <summary>Helper for response creation.</summary>
	public static class HttpResponseHelper
	{
		/// <summary></summary>
		public const long CacheSize = 2L << 20;

		#region -- ParseHtml ----------------------------------------------------------

		internal static string ParseHtml(TextReader tr, long capacity, out bool isPlainHtml, out bool openOutput)
		{
			var readBuffer = new char[4096];

			var luaCode = new StringBuilder(unchecked((int)capacity));
			var htmlBuffer = new StringBuilder();
			var luaCmd = new StringBuilder();
			var fmtBuffer = new StringBuilder();
			int readed;
			var state = 0;
			var isFirst = true;
			var inCommand = 0;  // 0
								// 1 lua code
								// 2 lua var

			isPlainHtml = true;
			openOutput = true;

			do
			{
				// read content
				readed = tr.Read(readBuffer, 0, readBuffer.Length);

				// parse the html content for inline lua
				for (var i = 0; i < readed; i++)
				{
					var c = readBuffer[i];

					switch (state)
					{
						#region -- Basis --
						case 0: // Basis
							if (c == '<') // open bracket
							{
								state = 1;
							}
							else if (!isFirst)
								htmlBuffer.Append(c);
							else if (!Char.IsWhiteSpace(c))
							{
								isFirst = false;
								htmlBuffer.Append(c);
							}
							break;

						case 1: // check type of bracket
							if (c == '!') // comment?
								state = 10;
							else if (c == '%') // command
								state = 20;
							else
							{
								htmlBuffer.Append('<');
								state = 0;
								goto case 0;
							}
							break;
						#endregion
						#region -- 10 - Comment --
						case 10:
							if (c == '-')
								state = 11;
							else
							{
								htmlBuffer.Append("<!");
								state = 0;
								goto case 0;
							}
							break;
						case 11:
							if (c == '-')
								state = 12;
							else
							{
								htmlBuffer.Append("<!-");
								state = 0;
								goto case 0;
							}
							break;
						case 12:
							if (c == '-')
								state = 13;
							break;
						case 13:
							if (c == '-')
								state = 14;
							else
								state = 12;
							break;
						case 14:
							if (c == '>')
								state = 0;
							else
								state = 12;
							break;
						#endregion
						#region -- 20 - Command --
						case 20:
							if (c == '=') // variable syntax
							{
								inCommand = 2;
								state = 21;
								break;
							}
							else
							{
								inCommand = 1;
								luaCmd.Length = 0;
								state = 21;
								goto case 21;
							}
						case 21:
							if (c == '%')
								state = 22;
							else if (inCommand == 2 && c == ':')
								state = 23;
							else if (inCommand == 3)
								fmtBuffer.Append(c);
							else
								luaCmd.Append(c);
							break;
						case 22:
							if (c == '>')
							{
								state = 0;

								isPlainHtml = false;
								if (isFirst)
									openOutput = false;

								GenerateHtmlBlock(luaCode, htmlBuffer);
								if (inCommand == 1)
									luaCode.Append(luaCmd).AppendLine(); // append the script
								else if (inCommand == 2)
								{
									luaCode.Append("printValue(")
										.Append(luaCmd)
										.AppendLine(");");
								}
								else if (inCommand == 3)
								{
									luaCode.Append("printValue(")
										.Append(luaCmd)
										.Append(", \"")
										.Append(fmtBuffer.ToString())
										.Append('"')
										.AppendLine(");");
								}
								else
									throw new ArgumentException();

								inCommand = 0;
								fmtBuffer.Length = 0;
								luaCmd.Length = 0;
							}
							else
							{
								state = 21;
								luaCmd.Append('%');
								goto case 21;
							}
							break;
						case 23:
							if (c == ':')
							{
								inCommand = 3;
								state = 21;
								break;
							}
							else
							{
								luaCmd.Append(':');
								state = 21;
								goto case 21;
							}
						#endregion
						default:
							throw new InvalidOperationException();
					}
				}
			} while (readed > 0);

			// Prüfe einen offnen Commandblock
			if (inCommand != 0)
				throw new ArgumentException("Unexpected eof.");

			if (htmlBuffer.Length > 0)
			{
				if (isPlainHtml)
					luaCode.Append(htmlBuffer);
				else
					GenerateHtmlBlock(luaCode, htmlBuffer);
			}

			return luaCode.ToString();
		} // proc Parse

		private static void GenerateHtmlBlock(StringBuilder lua, StringBuilder html)
		{
			if (html.Length > 0)
			{
				lua.Append("print(\"");

				for (var i = 0; i < html.Length; i++)
				{
					var c = html[i];
					switch (c)
					{
						case '\r':
							lua.Append("\\r");
							break;
						case '\n':
							lua.Append("\\n");
							break;
						case '\t':
							lua.Append("\\t");
							break;
						case '"':
							lua.Append("\\\"");
							break;
						default:
							lua.Append(c);
							break;
					}
				}

				lua.AppendLine("\");");
			}
			html.Length = 0;
		} // proc GenerateHtmlBlock

		#endregion

		#region -- SetLastModified, SetXXXXFileName -----------------------------------

		/// <summary>Sets the output last modified date</summary>
		/// <param name="context"></param>
		/// <param name="lastModified"></param>
		/// <returns></returns>
		public static IDEWebRequestScope SetLastModified(this IDEWebRequestScope context, DateTime lastModified)
		{
			if (lastModified.Kind != DateTimeKind.Utc)
				lastModified = lastModified.ToUniversalTime();

			context.OutputHeaders[HttpResponseHeader.LastModified] = lastModified.ToString("R", CultureInfo.InvariantCulture);
			return context;
		} // proc SetLastModified

		/// <summary>Sets the content disposition to the given filename.</summary>
		/// <param name="context"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static IDEWebRequestScope SetInlineFileName(this IDEWebRequestScope context, string fileName)
		{
			context.OutputHeaders["Content-Disposition"] = $"inline; filename = \"{fileName}\"";
			return context;
		} // proc SetAttachment

		/// <summary>Sets the content disposition to the given filename.</summary>
		/// <param name="context"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static IDEWebRequestScope SetAttachment(this IDEWebRequestScope context, string fileName)
		{
			context.OutputHeaders["Content-Disposition"] = $"attachment; filename = \"{fileName}\"";
			return context;
		} // proc SetAttachment

		#endregion

		#region -- Accept Type --------------------------------------------------------

		/// <summary>Test if the type is accepted.</summary>
		/// <param name="r"></param>
		/// <param name="contentType"></param>
		/// <returns></returns>
		public static bool AcceptType(this IDEWebRequestScope r, string contentType)
			=> Array.FindIndex(r.AcceptedTypes, c => c.StartsWith(contentType)) >= 0;

		#endregion

		#region -- WriteText, WriteBytes, WriteStream ---------------------------------

		private static void PrepareWriteText(IDEWebRequestScope context, ref string contentType, ref Encoding encoding)
		{
			if (encoding == null)
				encoding = context.InputEncoding ?? context.Http.DefaultEncoding;
			if (contentType.IndexOf("charset=") == -1)
				contentType += ";charset=" + encoding.WebName;
		} // proc PrepareWriteText

		/// <summary>Writes the text to the output.</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		public static void WriteText(this IDEWebRequestScope context, string value, string contentType = MimeTypes.Text.Plain, Encoding encoding = null)
		{
			PrepareWriteText(context, ref contentType, ref encoding);
			WriteBytes(context, encoding.GetBytes(value), contentType);
		} // proc WriteText

		/// <summary>Writes the text to the output.</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		public static Task WriteTextAsync(this IDEWebRequestScope context, string value, string contentType = MimeTypes.Text.Plain, Encoding encoding = null)
		{
			PrepareWriteText(context, ref contentType, ref encoding);
			return WriteBytesAsync(context, encoding.GetBytes(value), contentType);
		} // proc WriteText

		/// <summary>Writes the bytes to the output.</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteBytes(this IDEWebRequestScope context, byte[] value, string contentType = MimeTypes.Application.OctetStream)
			=> WriteStream(context, new MemoryStream(value, false), contentType);

		/// <summary>Write bytes in the output stream</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		/// <returns></returns>
		public static Task WriteBytesAsync(this IDEWebRequestScope context, byte[] value, string contentType = MimeTypes.Application.OctetStream)
			=> WriteStreamAsync(context, new MemoryStream(value, false), contentType);

		private static long GetStreamLength(Stream src)
			=> src.CanSeek ? src.Length : -1L;

		/// <summary>Writes the stream to the output.</summary>
		/// <param name="context"></param>
		/// <param name="stream"></param>
		/// <param name="contentType"></param>
		public static void WriteStream(this IDEWebRequestScope context, Stream stream, string contentType = MimeTypes.Application.OctetStream)
		{
			using (var dst = context.GetOutputStream(contentType, GetStreamLength(stream)))
			{
				if (dst != null)
					stream.CopyTo(dst);
			}
		} // proc WriteStream

		/// <summary>Writes the stream to the output.</summary>
		/// <param name="context"></param>
		/// <param name="stream"></param>
		/// <param name="contentType"></param>
		public static async Task WriteStreamAsync(this IDEWebRequestScope context, Stream stream, string contentType = MimeTypes.Application.OctetStream)
		{
			using (var dst = context.GetOutputStream(contentType, GetStreamLength(stream)))
			{
				if (dst != null)
					await stream.CopyToAsync(dst);
			}
		} // proc WriteStream

		#endregion

		#region -- WriteFile, WriteResource, WriteContent -----------------------------

		/// <summary>Writes the file to the output.</summary>
		/// <param name="context"></param>
		/// <param name="fileName"></param>
		/// <param name="contentType"></param>
		/// <param name="defaultReadEncoding"></param>
		public static void WriteFile(this IDEWebRequestScope context, string fileName, string contentType = null, Encoding defaultReadEncoding = null)
			=> WriteFile(context, new FileInfo(fileName), contentType);

		/// <summary>Writes the file to the output.</summary>
		/// <param name="context"></param>
		/// <param name="fi"></param>
		/// <param name="contentType"></param>
		/// <param name="defaultReadEncoding"></param>
		public static void WriteFile(this IDEWebRequestScope context, FileInfo fi, string contentType = null, Encoding defaultReadEncoding = null)
		{
			// set last modified
			SetLastModified(context, fi.LastWriteTimeUtc);
			// set the filename
			SetInlineFileName(context, fi.Name);

			// fint the correct content type
			if (contentType == null)
				contentType = context.Http.GetContentType(fi.Extension);

			// write the content
			WriteContent(context,
				() => new FileStream(fi.FullName, FileMode.Open, FileAccess.Read),
				fi.DirectoryName + "\\[" + fi.Length + "," + fi.LastWriteTimeUtc.ToString("R") + "]\\" + fi.Name,
				contentType,
				defaultReadEncoding);
		} // func WriteFile

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="type"></param>
		/// <param name="resourceName"></param>
		/// <param name="contentType"></param>
		public static void WriteResource(this IDEWebRequestScope context, Type type, string resourceName, string contentType = null)
		{
			if (String.IsNullOrEmpty(resourceName))
				throw new ArgumentNullException("resourceName");

			// Öffne die Resource
			WriteResource(context, type.Assembly, type.Namespace + '.' + resourceName, contentType);
		} // proc WriteResource

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="assembly"></param>
		/// <param name="resourceName"></param>
		/// <param name="contentType"></param>
		public static void WriteResource(this IDEWebRequestScope context, Assembly assembly, string resourceName, string contentType = null)
		{
			// Ermittle den ContentType
			if (contentType == null)
				contentType = context.Http.GetContentType(Path.GetExtension(resourceName));

			WriteContent(context,
				() =>
				{
					var src = assembly.GetManifestResourceStream(resourceName);
					if (src == null)
						throw new ArgumentException(String.Format("Resource '{0}' not found.", resourceName));
					return src;
				},
				assembly.FullName.Replace(" ", "") + "\\" + resourceName, contentType);
		} // proc WriteResource

		private static object CreateScript(IDEWebRequestScope context, Func<TextReader> createSource, string sciptBase)
		{
			var luaEngine = context.Server.GetService<IDELuaEngine>(true);
			return luaEngine.CreateScript(
				createSource,
				sciptBase
			);
		} // func CreateScript

		private static string GetFileNameFromSource(Stream src)
			=> src is FileStream fs ? fs.Name : null;

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="createSource"></param>
		/// <param name="cacheId"></param>
		/// <param name="contentType"></param>
		/// <param name="defaultReadEncoding">Encoding to read text files, default is utf-8.</param>
		public static void WriteContent(this IDEWebRequestScope context, Func<Stream> createSource, string cacheId, string contentType, Encoding defaultReadEncoding = null)
		{
			if (cacheId == null)
				throw new ArgumentNullException("cacheId");
			if (contentType == null)
				throw new ArgumentNullException("contentType");

			// check for head request
			if (context.InputMethod == HttpMethod.Head.Method)
			{
				context.SetStatus(HttpStatusCode.OK, "Ok");
				return;
			}

			var http = context.Server as IDEHttpServer;
			var o = http?.GetWebCache(cacheId);

			// create the item
			if (o == null)
			{
				using (var src = createSource())
				{
					var isLua = contentType.StartsWith(MimeTypes.Text.Lua);
					var isHtml = contentType.StartsWith(MimeTypes.Text.Html);
					var cacheItem = isLua || isHtml || (src.CanSeek && src.Length < CacheSize);
					if (cacheItem)
					{
						var isText = contentType.StartsWith("text/");
						if (isLua)
							o = CreateScript(context, () => new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true), GetFileNameFromSource(src));
						else if (isHtml)
						{
							var content = ParseHtml(new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true), src.CanSeek ? src.Length : 1024, out var isPlainText, out var openOutput);
							o = isPlainText ? content : CreateScript(context, () => new StringReader(openOutput ? "otext('text/html');" + content : content), GetFileNameFromSource(src));
						}
						else if (isText)
						{
							using (var tr = new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true))
								o = tr.ReadToEnd();
						}
						else
							o = src.ReadInArray();

						// write the cache item
						http?.UpdateWebCache(cacheId, o);
					}
					else // write data without cache
					{
						WriteStream(context, src, contentType);
						return;
					}
				}
			}

			// write the item to the output
			switch (o)
			{
				case null:
					throw new ArgumentNullException("output", "No valid output.");

				case ILuaScript c:
					{
						LuaResult r;
						using (context.Use())
						using (var g = new LuaHtmlTable(context, contentType))
							r = c.Run(g, true);

						if (!context.IsOutputStarted && r.Count > 0)
							WriteObject(context, r[0], r.GetValueOrDefault(1, MimeTypes.Text.Html));
					}
					break;
				case byte[] b:
					WriteBytes(context, b, contentType);
					break;
				case string s:
					WriteText(context, s, contentType, context.Http.DefaultEncoding);
					break;
				default:
					throw new ArgumentException($"Invalid cache item. Type '{o.GetType()}' is not supported.");
			}
		} // func GetContent

		#endregion

		#region -- WriteXXXX ----------------------------------------------------------

		/// <summary>Writes the text as an html page.</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="title"></param>
		public static void WriteTextAsHtml(this IDEWebRequestScope context, string value, string title = "page")
		{
			WriteText(context,
				String.Join(Environment.NewLine,
					"<!DOCTYPE html>",
					"<html>",
					"<head>",
					"  <meta charset=\"utf-8\"/>",
					$"  <title>{title}</title>",
					"</head>",
					"<body>",
					$"  <pre>{value}</pre>",
					"</body>"),
				MimeTypes.Text.Html, Encoding.UTF8
			);
		} // proc WriteTextAsHtml

		private static TextWriter PrepareWriteXml(IDEWebRequestScope context, XDocument value, string contentType)
			=> context.GetOutputTextWriter(contentType, context.Http.DefaultEncoding, -1);
		
		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteXml(this IDEWebRequestScope context, XElement value, string contentType = MimeTypes.Text.Xml)
		{
			WriteXml(context,
				new XDocument(
					new XDeclaration("1.0", context.Http.DefaultEncoding.WebName, "yes"),
					value
				), contentType);
		} // proc WriteXml

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static Task WriteXmlAsync(this IDEWebRequestScope context, XElement value, string contentType = MimeTypes.Text.Xml)
		{
			return WriteXmlAsync(context,
				new XDocument(
					new XDeclaration("1.0", context.Http.DefaultEncoding.WebName, "yes"),
					value
				), contentType);
		} // proc WriteXml

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteXml(this IDEWebRequestScope context, XDocument value, string contentType = MimeTypes.Text.Xml)
		{
			using (var tw = PrepareWriteXml(context, value, contentType))
				value.Save(tw);
		} // proc WriteXml

		/// <summary>Write Xml to output</summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static async Task WriteXmlAsync(this IDEWebRequestScope context, XDocument value, string contentType = MimeTypes.Text.Xml)
		{
			using (var tw = PrepareWriteXml(context, value, contentType))
				await Task.Run(() => value.Save(tw));
		} // proc WriteXml

		/// <summary>Write Lua-Table to output.</summary>
		/// <param name="context"></param>
		/// <param name="table"></param>
		public static void WriteLuaTable(this IDEWebRequestScope context, LuaTable table)
		{
			if (context.AcceptType(MimeTypes.Text.Lson)) // test if lson is givven
				WriteText(context, table.ToLson(false), MimeTypes.Text.Lson);
			else
				WriteXml(context, new XDocument(Procs.ToXml(table)));
		} // WriteLuaTable

		/// <summary>Write Lua-Table to output</summary>
		/// <param name="context"></param>
		/// <param name="table"></param>
		/// <returns></returns>
		public static Task WriteLuaTableAsync(this IDEWebRequestScope context, LuaTable table)
		{
			if (context.AcceptType(MimeTypes.Text.Lson)) // test if lson is givven
				return WriteTextAsync(context, table.ToLson(false), MimeTypes.Text.Lson);
			else
				return WriteXmlAsync(context, new XDocument(Procs.ToXml(table)));
		} // func WriteLuaTableAsync

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteObject(this IDEWebRequestScope context, object value, string contentType = null)
		{
			switch (value)
			{
				case null:
					throw new ArgumentNullException(nameof(value));
				case XElement e:
					WriteXml(context, e, contentType ?? MimeTypes.Text.Xml);
					break;
				case XDocument d:
					WriteXml(context, d, contentType ?? MimeTypes.Text.Xml);
					break;
				case string s:
					WriteText(context, s, contentType ?? MimeTypes.Text.Plain);
					break;
				case Stream st:
					WriteStream(context, st, contentType ?? MimeTypes.Application.OctetStream);
					break;
				case byte[] b:
					WriteBytes(context, b, contentType ?? MimeTypes.Application.OctetStream);
					break;
				case LuaTable t:
					WriteLuaTable(context, t);
					break;
				default:
					throw ThrowUnknownObjectType(value);
			}
		} // proc WriteObject

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static Task WriteObjectAsync(this IDEWebRequestScope context, object value, string contentType = null)
		{
			switch (value)
			{
				case null:
					throw new ArgumentNullException(nameof(value));
				case XElement e:
					return WriteXmlAsync(context, e, contentType ?? MimeTypes.Text.Xml);
				case XDocument d:
					return WriteXmlAsync(context, d, contentType ?? MimeTypes.Text.Xml);
				case string s:
					return WriteTextAsync(context, s, contentType ?? MimeTypes.Text.Plain);
				case Stream st:
					return WriteStreamAsync(context, st, contentType ?? MimeTypes.Application.OctetStream);
				case byte[] b:
					return WriteBytesAsync(context, b, contentType ?? MimeTypes.Application.OctetStream);
				case LuaTable t:
					return WriteLuaTableAsync(context, t);
				default:
					throw ThrowUnknownObjectType(value);
			}
		} // proc WriteObject
		
		private static HttpResponseException ThrowUnknownObjectType(object value)
			=> new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Can not send return value of type '{0}'.", value.GetType().FullName));

		#endregion

		#region -- WriteSafeCall ------------------------------------------------------

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="x"></param>
		/// <param name="successMessage"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, XElement x, string successMessage = null)
			=> WriteXml(r, DEConfigItem.SetStatusAttributes(x ?? new XElement("return"), true, successMessage));

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="errorMessage"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, string errorMessage)
			=> WriteXml(r, DEConfigItem.CreateDefaultXmlReturn(false, errorMessage));

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="e"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, Exception e)
			=> WriteSafeCall(r, e.Message);

		#endregion
	} // class HttpResponseHelper

	#endregion

	#region -- class HttpResponseException --------------------------------------------

	/// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
	public class HttpResponseException : Exception
	{
		/// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
		/// <param name="code">Http-Fehlercode</param>
		/// <param name="message">Nachricht zu diesem Fehlercode</param>
		/// <param name="innerException">Optionale </param>
		public HttpResponseException(HttpStatusCode code, string message, Exception innerException = null)
			: base(message, innerException)
		{
			this.Code = code;
		} // ctor

		/// <summary>Code der übermittelt werden soll.</summary>
		public HttpStatusCode Code { get; }
	} // class HttpResponseException

	#endregion
}
