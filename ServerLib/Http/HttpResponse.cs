using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Http
{
	#region -- interface IDEHttpServer --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEHttpServer : IDEConfigItem
	{
		/// <summary></summary>
		/// <param name="protocol"></param>
		void RegisterWebSocketProtocol(IDEWebSocketProtocol protocol);
		/// <summary></summary>
		/// <param name="protocol"></param>
		void UnregisterWebSocketProtocol(IDEWebSocketProtocol protocol);

		/// <summary>Gibt die zur Endung registrierten mime-type zurück.</summary>
		/// <param name="extension">Dateiendung</param>
		/// <returns>mime-type</returns>
		string GetContentType(string extension);

		/// <summary>Fragt den Cache ab</summary>
		/// <param name="cacheId">Eindeutige Id des Cache-Eintrages</param>
		/// <returns>Gecachtes Objekt oder <c>null</c></returns>
		object GetWebCache(string cacheId);
		/// <summary>Trägt etwas in den Cache neu ein.</summary>
		/// <param name="cacheId">Eindeutige Id des Cache-Eintrages</param>
		/// <param name="cache">Objekt</param>
		/// <returns>Wurde der Eintrag in den Cache aufgenommen</returns>
		bool UpdateWebCache(string cacheId, object cache);

		/// <summary>Bestimmt die Kodierung, für die Textausgabe.</summary>
		Encoding Encoding { get; }
		/// <summary>Default culture for the client requests.</summary>
		CultureInfo DefaultCultureInfo { get; }

		/// <summary>Wird der Server im Debug-Modus betrieben</summary>
		bool IsDebug { get; }
	} // interface IDEHttpServer

	#endregion

	#region -- interface IDEWebSocketProtocol -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEWebSocketProtocol
	{
		/// <summary></summary>
		/// <param name="webSocket"></param>
		/// <returns></returns>
		bool AcceptWebSocket(IDEWebSocketContext webSocket);
		/// <summary>Basepath for the protocol within the server.</summary>
		[DEListTypePropertyAttribute("@base")]
		string BasePath { get; }
		/// <summary>Name of the protocol.</summary>
		[DEListTypePropertyAttribute("@protocol")]
		string Protocol { get; }
		/// <summary></summary>
		[DEListTypePropertyAttribute("@security")]
    string SecurityToken { get; }
	} // interface IDEWebSocketProtocol

	#endregion

	#region -- interface IDECommonContext -----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDECommonContext
	{
		/// <summary>Get the parameter of the current command (includes http-request-header fields).</summary>
		/// <param name="parameterName"></param>
		/// <param name="@default"></param>
		/// <returns></returns>
		string GetProperty(string parameterName, string @default);
		/// <summary></summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="parameterName"></param>
		/// <param name="@default"></param>
		/// <returns></returns>
		T GetProperty<T>(string parameterName, T @default);
		/// <summary>Returns the available parameter</summary>
		string[] ParameterNames { get; }
		/// <summary>Returns the available parameter</summary>
		string[] HeaderNames { get; }

		/// <summary>Accepted language</summary>
		CultureInfo CultureInfo { get; }

		/// <summary>Access to the user context.</summary>
		T GetUser<T>() where T : class;

		/// <summary>Access to the http-server.</summary>
		IDEHttpServer Http { get; }

		/// <summary>The full request path.</summary>
		string AbsolutePath { get; }
	} // interface IDECommonContext

	#endregion

	#region -- interface IDEWebSocketContext --------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEWebSocketContext : IDECommonContext
	{
		WebSocket WebSocket { get; }
	} // interface IDEWebSocketContext

	#endregion

	#region -- interface IDEHttpContext -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEHttpContext : IDECommonContext
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

		/// <summary>Check, if the user has the access token.</summary>
		/// <param name="securityToken">The access token.</param>
		void DemandToken(string securityToken);
		/// <summary></summary>
		/// <param name="securityToken"></param>
		/// <returns></returns>
		bool TryDemandToken(string securityToken);
		/// <summary></summary>
		/// <param name="securityText"></param>
		/// <returns></returns>
		HttpResponseException CreateAuthorizationException(string securityText);

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
		/// <returns></returns>
		Stream GetOutputStream(string contentType, long contentLength = -1, bool? compress = null);
		/// <summary></summary>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		/// <param name="contentLength"></param>
		/// <returns></returns>
		TextWriter GetOutputTextWriter(string contentType, Encoding encoding = null, long contentLength=-1);
		/// <summary>Sends a redirect.</summary>
		/// <param name="url"></param>
		void Redirect(string url);
		/// <summary>Is there a call of GetOutputStream, GetOutputTextWriter or Redirect.</summary>
		bool IsOutputStarted { get; }

		/// <summary>Current node, on which the request is executed.</summary>
		IDEConfigItem CurrentNode { get; }

		/// <summary>Current path of the uri, relative to the current node.</summary>
		string RelativeSubPath { get; }
		/// <summary>Name of the current path position.</summary>
		string RelativeSubName { get; }
	} // interface IDEHttpContext

	#endregion

	#region -- class LuaHttpTable -------------------------------------------------------

	internal sealed class LuaHttpTable : LuaTable
	{
		private IDEHttpContext context;
		private string contentType;
		private Stream streamOutput = null;
		private TextWriter textOutput = null;
		private Encoding encoding = null;

		public LuaHttpTable(IDEHttpContext context, string contentType)
		{
			this.context = context;
			this.contentType = contentType;
		} // ctor

		public void Dispose()
		{
			Procs.FreeAndNil(ref streamOutput);
			Procs.FreeAndNil(ref textOutput);
		} // proc Dispose

		[LuaMember("print")]
		private void LuaPrint(params object[] values)
		{
			string text;
			if (values == null || values.Length == 0)
				return;

			if (values.Length == 1)
				if (values[0] is string)
					text = (string)values[0];
				else
					text = Procs.ChangeType<string>(values[0]);
			else
				text = String.Join(" ", values.Select(c => Procs.ChangeType<string>(c)));

			if (textOutput != null)
				textOutput.WriteLine(text);
			else if (streamOutput != null)
			{
				var b = encoding.GetBytes(text);
				streamOutput.Write(b, 0, b.Length);
			}
			else
				throw new ArgumentException("out is not open.");
		} // proc OnPrint

		private void PrepareOutput()
		{
			if (textOutput != null || streamOutput != null)
				throw new ArgumentException("out is open.");

			if (encoding == null)
				encoding = context.InputEncoding ?? context.Http.Encoding;
		} // proc PrepareOutput

		[LuaMember("otext")]
		public void OpenText(string sContentType, Encoding encoding = null)
		{
			PrepareOutput();
			textOutput = context.GetOutputTextWriter(contentType, encoding);
		} // proc OpenText

		[LuaMember("obinary")]
		private void OpenBinary(string sContentType, Encoding encoding = null)
		{
			PrepareOutput();
			streamOutput = context.GetOutputStream(contentType);
		} // proc OpenBinary

		protected override object OnIndex(object key)
			=> base.OnIndex(key) ?? (context.CurrentNode as LuaTable)?.GetValue(key);

		[LuaMember("ContentType")]
		public string ContentType { get { return contentType; } set { } }
		[LuaMember("Context")]
		public IDEHttpContext Context { get { return context; } set { } }
		[LuaMember("out")]
		public object Output { get { return (object)streamOutput ?? textOutput; } set { } }
	} // class LuaHttpTable

	#endregion

	#region -- class HttpResponseHelper -------------------------------------------------

	public static class HttpResponseHelper
	{
		public const long CacheSize = 2L << 20;

		#region -- ParseHtml --------------------------------------------------------------

		private static string ParseHtml(TextReader tr, long capacity, out bool isPlainHtml)
		{
			char[] readBuffer = new char[4096];

			var sbLua = new StringBuilder(unchecked((int)capacity));
			var sbHtml = new StringBuilder();
			var sbCmd = new StringBuilder();
			int readed;
			int state = 0;
			bool inCommand = false;

			isPlainHtml = true;

			do
			{
				// read content
				readed = tr.Read(readBuffer, 0, readBuffer.Length);

				// parse the html content for inline lua
				for (var i = 0; i < readed; i++)
				{
					char c = readBuffer[i];

					switch (state)
					{
						#region -- Basis --
						case 0: // Basis
							if (c == '<') // open bracket
							{
								state = 1;
							}
							else
								sbHtml.Append(c);
							break;

						case 1: // check type of bracket
							if (c == '!') // comment?
								state = 10;
							else if (c == '%') // command
								state = 20;
							else
							{
								sbHtml.Append('<');
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
								sbHtml.Append("<!");
								state = 0;
								goto case 0;
							}
							break;
						case 11:
							if (c == '-')
								state = 12;
							else
							{
								sbHtml.Append("<!-");
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
							inCommand = true;
							sbCmd.Length = 0;
							state = 21;
							break;
						case 21:
							if (c == '%')
								state = 22;
							else
								sbCmd.Append(c);
							break;
						case 22:
							if (c == '>')
							{
								state = 0;

								isPlainHtml = false;
								inCommand = false;
								GenerateHtmlBlock(sbLua, sbHtml);
								sbLua.Append(sbCmd).AppendLine(); // append the script

								sbCmd.Length = 0;
							}
							else
							{
								state = 21;
								sbCmd.Append('%');
								goto case 21;
							}
							break;
						#endregion
						default:
							throw new InvalidOperationException();
					}
				}
			} while (readed > 0);

			// Prüfe einen offnen Commandblock
			if (inCommand)
				throw new ArgumentException("Unexpected eof.");

			if (sbHtml.Length > 0)
			{
				if (isPlainHtml)
					sbLua.Append(sbHtml);
				else
					GenerateHtmlBlock(sbLua, sbHtml);
			}

			return sbLua.ToString();
		} // proc Parse

		private static void GenerateHtmlBlock(StringBuilder sbLua, StringBuilder sbHtml)
		{
			if (sbHtml.Length > 0)
			{
				sbLua.Append("print(\"");

				for (int i = 0; i < sbHtml.Length; i++)
				{
					char c = sbHtml[i];
					if (c == '\r')
						sbLua.Append("\\r");
					else if (c == '\n')
						sbLua.Append("\\n");
					else if (c == '\t')
						sbLua.Append("\\t");
					else if (c == '"')
						sbLua.Append("\\\"");
					else
						sbLua.Append(c);
				}

				sbLua.AppendLine("\");");
			}
			sbHtml.Length = 0;
		} // proc GenerateHtmlBlock

		#endregion

		#region -- SetLastModified, SetXXXXFileName ---------------------------------------

		/// <summary>Sets the output last modified date</summary>
		/// <param name="http"></param>
		/// <param name="lastModified"></param>
		/// <returns></returns>
		public static IDEHttpContext SetLastModified(this IDEHttpContext http, DateTime lastModified)
		{
			if (lastModified.Kind != DateTimeKind.Utc)
				lastModified = lastModified.ToUniversalTime();

			http.OutputHeaders[HttpRequestHeader.LastModified] = lastModified.ToString("R", CultureInfo.InvariantCulture);
      return http;
		} // proc SetLastModified

		/// <summary>Sets the content disposition to the given filename.</summary>
		/// <param name="http"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static IDEHttpContext SetInlineFileName(this IDEHttpContext http, string fileName)
		{
			http.OutputHeaders["Content-Disposition"] = $"inline; filename = \"{fileName}\"";
			return http;
		} // proc SetAttachment

		/// <summary>Sets the content disposition to the given filename.</summary>
		/// <param name="http"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static IDEHttpContext SetAttachment(this IDEHttpContext http, string fileName)
		{
			http.OutputHeaders["Content-Disposition"] = $"attachment; filename = \"{fileName}\"";
			return http;
		} // proc SetAttachment

		#endregion

		#region -- WriteText, WriteBytes, WriteStream -------------------------------------

		/// <summary>Writes the text to the output.</summary>
		/// <param name="http"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteText(this IDEHttpContext http, string value, string contentType = MimeTypes.Text.Plain, Encoding encoding = null)
		{
			if (encoding == null)
				encoding = http.InputEncoding ?? http.Http.Encoding;
			WriteBytes(http, encoding.GetBytes(value), contentType);
		} // proc WriteText

		/// <summary>Writes the bytes to the output.</summary>
		/// <param name="http"></param>
		/// <param name="value"></param>
		/// <param name="contentType"></param>
		public static void WriteBytes(this IDEHttpContext http, byte[] value, string contentType = MimeTypes.Application.OctetStream)
		{
			using (var dst = http.GetOutputStream(contentType, value.Length))
				dst.Write(value, 0, value.Length);
		} // proc WriteText

		/// <summary>Writes the stream to the output.</summary>
		/// <param name="http"></param>
		/// <param name="stream"></param>
		/// <param name="contentType"></param>
		public static void WriteStream(this IDEHttpContext http, Stream stream, string contentType = MimeTypes.Application.OctetStream)
		{
			var length = stream.CanSeek ? stream.Length : -1L;
			using (var dst = http.GetOutputStream(contentType, length))
				stream.CopyTo(dst);
		} // proc WriteStream

		#endregion

		#region -- WriteFile, WriteResource, WriteContent ---------------------------------

		/// <summary>Writes the file to the output.</summary>
		/// <param name="http"></param>
		/// <param name="fileName"></param>
		/// <param name="contentType"></param>
		public static void WriteFile(this IDEHttpContext http, string fileName, string contentType = null)
			=> WriteFile(http, new FileInfo(fileName), contentType);

		/// <summary>Writes the file to the output.</summary>
		/// <param name="http"></param>
		/// <param name="fi"></param>
		/// <param name="contentType"></param>
		public static void WriteFile(this IDEHttpContext http, FileInfo fi, string contentType = null)
		{
			// set last modified
			SetLastModified(http, fi.LastWriteTimeUtc);
			// set the filename
			SetInlineFileName(http, fi.Name);

			// fint the correct content type
			if (contentType == null)
				contentType = http.Http.GetContentType(fi.Extension);

			// write the content
			WriteContent(http,
				() => new FileStream(fi.FullName, FileMode.Open, FileAccess.Read),
				fi.DirectoryName + "\\[" + fi.Length + "," + fi.LastWriteTimeUtc.ToString("R") + "]\\" + fi.Name,
				contentType);
    } // func WriteFile

		/// <summary></summary>
		/// <param name="http"></param>
		/// <param name="type"></param>
		/// <param name="resourceName"></param>
		/// <param name="contentType"></param>
		public static void WriteResource(this IDEHttpContext http, Type type, string resourceName, string contentType = null)
		{
			if (String.IsNullOrEmpty(resourceName))
				throw new ArgumentNullException("resourceName");

			// Öffne die Resource
			WriteResource(http, type.Assembly, type.Namespace + '.' + resourceName, contentType);
		} // proc WriteResource

		/// <summary></summary>
		/// <param name="http"></param>
		/// <param name="assembly"></param>
		/// <param name="resourceName"></param>
		/// <param name="contentType"></param>
		public static void WriteResource(this IDEHttpContext http, Assembly assembly, string resourceName, string contentType = null)
		{
			// Ermittle den ContentType
			if (contentType == null)
				contentType = http.Http.GetContentType(Path.GetExtension(contentType));
			
			WriteContent(http,
				() =>
				{
					var src = assembly.GetManifestResourceStream(resourceName);
					if (src == null)
						throw new ArgumentException(String.Format("Resource '{0}' not found.", resourceName));
					return src;
				},
				assembly.FullName.Replace(" ", "") + "\\" + resourceName, contentType);
		} // proc WriteResource

		private static object CreateScript(IDEHttpContext http, string cacheId, Func<TextReader> createSource)
		{
			var p = cacheId.LastIndexOf('\\');
			var luaEngine = http.Http.GetService<IDELuaEngine>(true);
			return luaEngine.CreateScript(
				createSource,
				p >= 0 ? cacheId.Substring(p + 1) : "content.lua",
				http.Http.IsDebug
			);
		} // func CreateScript

		public static void WriteContent(this IDEHttpContext http, Func<Stream> createSource, string cacheId, string contentType)
		{
			if (cacheId == null)
				throw new ArgumentNullException("cacheId");
			if (contentType == null)
				throw new ArgumentNullException("contentType");

			var o = http.Http.GetWebCache(cacheId);
			// create the item
			if (o == null)
			{
				using (var src = createSource())
				{
					var isLua = contentType == MimeTypes.Text.Lua;
					var isHtml = contentType == MimeTypes.Text.Html;
          var cacheItem = isLua || isHtml || (src.CanSeek && src.Length < CacheSize);
					if (cacheItem)
					{
						var isText = contentType.StartsWith("text/");
						if (isLua)
							o = CreateScript(http, cacheId, () => Procs.OpenStreamReader(src, Encoding.Default));
						else if (isHtml)
						{
							bool isPlainText;
							var content = ParseHtml(Procs.OpenStreamReader(src, Encoding.Default), src.CanSeek ? src.Length : 1024, out isPlainText);
							o = isPlainText ? content : CreateScript(http, cacheId, () => new StringReader(content));
						}
						else if (isText)
						{
							using (var tr = Procs.OpenStreamReader(src, Encoding.Default))
								o = tr.ReadToEnd();
						}
						else
							o = src.ReadInArray();

						// write the cache item
						http.Http.UpdateWebCache(cacheId, o);
					}
					else // write data without cache
					{
						WriteStream(http, src, contentType);
						return;
					}
				}
			}

			// write the item to the output
			if (o == null)
				throw new ArgumentNullException("output", "No valid output.");
			else if (o is ILuaScript)
			{
				var c = (ILuaScript)o;
				var r = c.Run(new LuaHttpTable(http, contentType));

				if (!http.IsOutputStarted && r.Count >0)
					WriteObject(http, r[0], r.GetValueOrDefault(1, MimeTypes.Text.Html));
			}
			else if (o is byte[])
				WriteBytes(http, (byte[])o, contentType);
			else if (o is string)
				WriteText(http, (string)o, contentType, http.Http.Encoding);
			else
				throw new ArgumentException($"Invalid cache item. Type '{o.GetType()}' is not supported.");
		} // func GetContent

		#endregion

		#region -- WriteXXXX --------------------------------------------------------------

		/// <summary>Writes the text as an html page.</summary>
		/// <param name="http"></param>
		/// <param name="value"></param>
		public static void WriteTextAsHtml(this IDEHttpContext http, string value, string title = "page")
		{
			WriteText(http,
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

		public static void WriteXml(this IDEHttpContext http, XElement value, string contentType = MimeTypes.Text.Xml)
		{
			WriteXml(http,
				new XDocument(
					new XDeclaration("1.0", http.Http.Encoding.WebName, "yes"),
					value
				), contentType);
		} // proc WriteXml

		public static void WriteXml(this IDEHttpContext http, XDocument value, string contentType = MimeTypes.Text.Xml)
		{
			using (var tw = http.GetOutputTextWriter(contentType, http.Http.Encoding, -1))
				value.Save(tw);
		} // proc WriteXml

		public static void WriteDataReader(this IDEHttpContext http, IDataReader value)
		{
			throw new NotImplementedException();
		} // proc WriteDataReader

		public static void WriteDataRecord(this IDEHttpContext http, IDataRecord value)
		{
			throw new NotImplementedException();
		} // proc WriteDataRecord

		public static void WriteObject(this IDEHttpContext http, object value, string contentType = null)
		{
			if (value == null)
				throw new ArgumentNullException("value");
			else if (value is XElement)
				WriteXml(http, (XElement)value, contentType);
			else if (value is XDocument)
				WriteXml(http, (XDocument)value, contentType);
			else if (value is IDataReader)
				WriteDataReader(http, (IDataReader)value);
			else if (value is IDataRecord)
				WriteDataRecord(http, (IDataRecord)value);
			else if (value is string)
				WriteText(http, (string)value, contentType);
			else if (value is Stream)
				WriteStream(http, (Stream)value, contentType);
			else if (value is byte[])
				WriteBytes(http, (byte[])value, contentType);
			else
				throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Can not send return value of type '{0}'.", value.GetType().FullName));
		} // proc WriteObject

		#endregion

		#region -- WriteSafeCall ----------------------------------------------------------

		public static void WriteSafeCall(this IDEHttpContext r, XElement x, string sSuccessMessage = null)
		{
			if (x == null)
				x = new XElement("return");

			x.SetAttributeValue("status", "ok");
			if (!String.IsNullOrEmpty(sSuccessMessage))
				x.SetAttributeValue("text", sSuccessMessage);

			WriteXml(r, x);
		} // proc WriteSafeCall

		public static void WriteSafeCall(this IDEHttpContext r, string sErrorMessage)
		{
			WriteXml(r,
				new XElement("return",
					new XAttribute("status", "error"),
					new XAttribute("text", sErrorMessage)
				)
			);
		} // proc WriteSafeCall

		public static void WriteSafeCall(this IDEHttpContext r, Exception e)
		{
			WriteSafeCall(r, e.Message);
		} // proc WriteSafeCall

		#endregion
	} // class HttpResponseHelper

	#endregion

	#region -- class HttpResponseException ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
	public class HttpResponseException : Exception
	{
		private HttpStatusCode code;

		/// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
		/// <param name="code">Http-Fehlercode</param>
		/// <param name="message">Nachricht zu diesem Fehlercode</param>
		/// <param name="innerException">Optionale </param>
		public HttpResponseException(HttpStatusCode code, string message, Exception innerException = null)
			: base(message, innerException)
		{
			this.code = code;
		} // ctor

		/// <summary>Code der übermittelt werden soll.</summary>
		public HttpStatusCode Code { get { return code; } }
	} // class HttpResponseException

	#endregion
}
