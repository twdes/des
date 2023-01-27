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
using System.CodeDom.Compiler;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Http
{
	#region -- interface IDEHttpPreAuthentificate -------------------------------------

	/// <summary></summary>
	public interface IDEHttpPreAuthentificate : IDisposable
	{
		/// <summary>User for authentification</summary>
		IDEAuthentificatedUser User { get; }
		/// <summary>Access token for the application.</summary>
		string AccessToken { get; }
	} // interface IDEHttpPreAuthentificate

	#endregion

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

		/// <summary>A incoming request gets the user context.</summary>
		/// <param name="user">User context, that is used for this authentification.</param>
		/// <param name="remoteHost">Access is expected from this ip. <c>null</c> for localhost only.</param>
		/// <returns></returns>
		IDEHttpPreAuthentificate PreAuthentificateByToken(IDEAuthentificatedUser user, IPAddress remoteHost = null);

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
		/// <summary>Get a mime type to the given extension.</summary>
		/// <param name="extension">file extension</param>
		/// <param name="mimeType"></param>
		/// <returns></returns>
		bool TryGetContentType(string extension, out string mimeType);

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
		/// <summary>Get a web uri, that can be used by the remote client.</summary>
		/// <param name="relativeUri"></param>
		Uri GetOrigin(Uri relativeUri);

		/// <summary>Is this a local request.</summary>
		bool IsLocal { get; }

		/// <summary>Orgination of the request.</summary>
		IPEndPoint RemoteEndPoint { get; }
		/// <summary>Get the full request path.</summary>
		string AbsolutePath { get; }

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

		/// <summary>Change into a virtual directory.</summary>
		/// <param name="sp">Service provider, that represents the sub-path.</param>
		/// <param name="subPath">Virtual sub-path, should not start with a slash.</param>
		/// <returns><c>true</c>, if the subpath was entered.</returns>
		bool TryEnterSubPath(IServiceProvider sp, string subPath);
		/// <summary>Exit the sub-path.</summary>
		/// <param name="sp">Service provider, that represents the sub-path.</param>
		void ExitSubPath(IServiceProvider sp);
		/// <summary>Relative origin to the current selected path.</summary>
		/// <param name="uri"></param>
		/// <returns></returns>
		Uri GetSubPathOrigin(Uri uri);

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

		/// <summary>Get a output stream, to send data to the client.</summary>
		/// <param name="contentType">MimeType for the content, add gzip to enforce compression.</param>
		/// <param name="contentLength">Expected length of the output data.</param>
		/// <returns></returns>
		Stream GetOutputStream(string contentType, long contentLength = -1);
		/// <summary></summary>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		/// <param name="contentLength"></param>
		/// <returns></returns>
		TextWriter GetOutputTextWriter(string contentType, Encoding encoding = null, long contentLength = -1);
		/// <summary>Sends a redirect.</summary>
		/// <param name="url"></param>
		/// <param name="statusDescription"></param>
		void Redirect(string url, string statusDescription = null);
		/// <summary>Set status code, and closes the output.</summary>
		/// <param name="statusCode"></param>
		/// <param name="statusDescription"></param>
		void SetStatus(HttpStatusCode statusCode, string statusDescription);

		/// <summary>Is there a call of GetOutputStream, GetOutputTextWriter or Redirect.</summary>
		bool IsOutputStarted { get; }
		/// <summary>Requested output encoding.</summary>
		Encoding OutputEncoding { get; }

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
	} // interface IDEWebRequestScope

	#endregion

	#region -- interface IHtmlScript --------------------------------------------------

	/// <summary>Html script interface</summary>
	public interface IHtmlScript
	{
        /// <summary>Return a value from the current scope.</summary>
        /// <param name="key"></param>
        /// <returns></returns>
        object GetValue(object key);
        
		/// <summary>Return a relative Uri.</summary>
        /// <param name="relativeUri"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        string GetUri(string relativeUri, LuaTable args = null);

		/// <summary>Print a formatted value.</summary>
		/// <param name="value"></param>
		/// <param name="fmt"></param>
		void PrintValue(object value, string fmt = null);
		/// <summary>Print variables or text to the output.</summary>
		/// <param name="values"></param>
		void Print(params object[] values);
		/// <summary>Print text to the output.</summary>
		/// <param name="text"></param>
		void Print(string text);

		/// <summary>Print an html tag.</summary>
		/// <param name="tagName"></param>
		/// <param name="attributes"><c>#</c> is sets the id, <c>:</c> sets the style, <c>.</c> set the class</param>
		/// <returns></returns>
		IDisposable PrintTag(string tagName, params object[] attributes);

        /// <summary>Print a template relative to the source.</summary>
        /// <param name="source"><c>FileInfo</c> or file string.</param>
        /// <param name="args"></param>
        void PrintTemplate(object source, LuaTable args = null);

        /// <summary>Ident the current output.</summary>
        /// <param name="indent"></param>
        /// <returns></returns>
        IDisposable Indent(int indent);

		/// <summary>Open text output.</summary>
		/// <param name="contentType"></param>
		/// <param name="encoding"></param>
		void OpenText(string contentType = null, Encoding encoding = null);
		/// <summary>Open binary output stream.</summary>
		/// <param name="contentType"></param>
		void OpenBinary(string contentType = null);

		/// <summary>Script scope.</summary>
		LuaTable Self { get; }
        /// <summary>Request context</summary>
        IDEWebRequestScope Context { get; }
        /// <summary>Script file name.</summary>
        string ScriptBase { get; }

        /// <summary>Content type of the output.</summary>
        string ContentType { get; }
		/// <summary>Curent output stream.</summary>
		object Output { get; }
    } // interface IHtmlScript

    #endregion

    #region -- class LuaHtmlTable -----------------------------------------------------

    internal sealed class LuaHtmlTable : LuaTable, IHtmlScript, IDisposable
	{
		private readonly ILuaScript script;
		private readonly IDEWebRequestScope context;

		private string contentType; // content type to emit

		// output streams
		private Stream streamOutput = null;
		private TextWriter textOutput = null;
		private Encoding encoding = null;

		private bool tagIsOpen = false;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public LuaHtmlTable(ILuaScript script, IDEWebRequestScope context, string contentType)
		{
			this.script = script ?? throw new ArgumentNullException(nameof(script));
			this.context = context ?? throw new ArgumentNullException(nameof(script));
			this.contentType = contentType;
		} // ctor

		public void Dispose()
		{
			if (textOutput is IndentedTextWriter tw)
			{
				textOutput.Flush();
				tw.Close();
			}

			Procs.FreeAndNil(ref streamOutput);
			Procs.FreeAndNil(ref textOutput);
		} // proc Dispose

		object IHtmlScript.GetValue(object key)
			=> GetValue(key);

		#endregion

		#region -- Print --------------------------------------------------------------

		#region -- class LuaTemplateTable ---------------------------------------------

		private sealed class LuaTemplateTable : LuaTable, IHtmlScript
        {
			private readonly LuaHtmlTable root;
			private readonly IHtmlScript parentScope;
			private readonly string scriptBase;
			private readonly LuaTable arguments;

			public LuaTemplateTable(LuaHtmlTable root, IHtmlScript parentScope, string scriptBase, LuaTable arguments)
			{
				this.root = root ?? throw new ArgumentNullException(nameof(root));
				this.parentScope = parentScope ?? throw new ArgumentNullException(nameof(parentScope));
				this.scriptBase = scriptBase ?? throw new ArgumentNullException(nameof(scriptBase));
				this.arguments = arguments ?? new LuaTable();
			} // ctor

			object IHtmlScript.GetValue(object key)
				=> GetValue(key);

			[LuaMember("printTemplate")]
			public void LuaTemplate(object source, LuaTable scope = null)
				=> root.LuaTemplate(root, this, source, scope);

            string IHtmlScript.GetUri(string relativeUri, LuaTable args)
				=> root.GetUri(relativeUri, args);

			void IHtmlScript.PrintValue(object value, string fmt)
				=> root.PrintValue(value, fmt);

			void IHtmlScript.Print(params object[] values)
				=> root.Print(values);

			void IHtmlScript.Print(string text)
				=> root.Print(text);

			IDisposable IHtmlScript.PrintTag(string tagName, params object[] attributes)
				=> root.PrintTag(tagName, attributes);

            void IHtmlScript.PrintTemplate(object source, LuaTable args)
                => root.LuaTemplate(root, this, source, args);

			IDisposable IHtmlScript.Indent(int indent)
				=> root.Indent(indent);

			void IHtmlScript.OpenText(string contentType, Encoding encoding)
				=> root.OpenText(contentType, encoding);
			
			void IHtmlScript.OpenBinary(string contentType)
				=> root.OpenBinary(contentType);

			string IHtmlScript.ContentType => root.ContentType;
			object IHtmlScript.Output => root.Output;

			protected override object OnIndex(object key)
				=> base.OnIndex(key) ?? arguments.GetValue(key) ?? parentScope.GetValue(key);

			public IDEWebRequestScope Context => root.Context;
			public string ScriptBase => scriptBase;

			[LuaMember]
			public LuaTable Self { get => this; set { } }
		} // class LuaTemplateTable

		#endregion

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

		private void PrintText(string text)
		{
			if (tagIsOpen)
			{
				tagIsOpen = false;
				PrintText(">");
			}

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

		[LuaMember("printValue")]
		public void PrintValue(object value, string fmt = null)
			=> PrintText(ConvertForHtml(value, fmt));

		[LuaMember("print")]
		public void Print(params object[] values)
		{
			if (values == null || values.Length == 0)
				return;

			var text = values.Length == 1
				? ConvertForHtml(values[0])
				: String.Join(" ", values.Select(ConvertForHtml));

			PrintText(text);
		} // proc LuaPrint

		void IHtmlScript.Print(string text)
			=> PrintText(text);

		private static void AppendTags(StringBuilder sb, string[] attributes, int attributeCount, string attributeName, string attributeSep)
		{
			if (attributeCount > 0)
			{
				sb.Append(' ')
					.Append(attributeName).Append('=').Append('"');
				for (var i = 0; i < attributeCount; i++)
				{
					if (i > 0)
						sb.Append(attributeSep);
					sb.Append(attributes[i]);
				}
				sb.Append('"');
			}
		} // proc AppendClassTags

		[LuaMember]
		public IDisposable PrintTag(string tagName, params object[] attributes)
		{
			var sb = new StringBuilder();

			sb.Append('<')
				.Append(tagName);

			if (attributes.Length > 0)
			{
				var classTags = new string[attributes.Length];
				var classTagCount = 0;
				var styleTags = new string[attributes.Length];
				var styleTagCount = 0;
				var idTag = (string)null;
				var otherTags = new PropertyValue[attributes.Length];
				var otherTagCount = 0;

				for (var i = 0; i < attributes.Length; i++)
				{
					ref var attr = ref attributes[i];
					if (attr is string s && s.Length > 0)
					{
						switch (s[0])
						{
							case '.':
								classTags[classTagCount++] = s.Substring(1);
								break;
							case ':':
								styleTags[styleTagCount++] = s.Substring(1);
								break;
							case '#':
								if (idTag == null)
									idTag = s.Substring(1);
								break;
						}
					}
					else if (attr is PropertyValue pv)
						otherTags[otherTagCount++] = pv;
				}

				if (idTag != null)
					sb.Append(' ').Append("id=").Append('"').TextToHtml(idTag).Append('"');

				AppendTags(sb, classTags, classTagCount, "class", " ");
				AppendTags(sb, styleTags, styleTagCount, "style", "; ");

				for (var i = 0; i < otherTagCount; i++)
					sb.Append(' ').Append(otherTags[i].Name).Append('=').Append('"').Append(otherTags[i].Value).Append('"');
			}
			PrintText(sb.ToString());
			tagIsOpen = true;
			return new DisposableScope(() => CloseTag(tagName));
		} // proc PrintTag

		private void CloseTag(string tagName)
		{
			if (tagIsOpen)
			{
                tagIsOpen = false;
                PrintText("/>");
			}
			else
				PrintText("</" + tagName + ">");
		} // proc CloseTag

		[LuaMember("indent")]
		public IDisposable Indent(int indent)
		{
			if (textOutput is IndentedTextWriter tw)
			{
				tw.Indent += indent;
				return new DisposableScope(() => tw.Indent -= indent);
			}
			else
				return null;
		} // func LuaIndent

		private void LuaTemplate(LuaHtmlTable root, IHtmlScript parentScope, object source, LuaTable arguments)
		{
			if (!(source is FileInfo fi)) // FileInfo for full names
				fi = new FileInfo(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentScope.ScriptBase), source.ToString())));

            var cacheId = fi.FullName + ";" + fi.LastWriteTimeUtc.ToString("o");

            var template = parentScope.Context.Http.GetWebCache(cacheId);
			if (template == null)
			{
				// parse template item
				using (var sr = new StreamReader(fi.FullName, Encoding.UTF8, true))
				using (var chars = new LuaCharLexer(fi.FullName, sr, LuaLexer.HtmlCharStreamLookAHead, leaveOpen: false))
				using (var code = LuaLexer.CreateHtml(chars))
				{
					template = Lua.IsConstantScript(code)
						? code.LookAhead.Value
						: (object)HttpResponseHelper.CreateScript(parentScope.Context, code, parentScope.ScriptBase);

					parentScope.Context.Http.UpdateWebCache(cacheId, template);
				}
			}

			if (template is string text)
				PrintText(text);
			else if (template is ILuaScript script)
			{
				var g = new LuaTemplateTable(root, parentScope, fi.FullName, arguments);
				script.Run(g, true);
			}
		} // proc LuaTemplate

		[LuaMember("printTemplate")]
		public void LuaTemplate(object source, LuaTable args = null)
			=> LuaTemplate(this, this, source, args);

		void IHtmlScript.PrintTemplate(object source, LuaTable args)
			=> LuaTemplate(this, this, source, args);

		#endregion

		[LuaMember]
		public string GetUri(string relativeUri, LuaTable args = null)
		{
			var sb = new StringBuilder(relativeUri);
			if (args != null)
				HttpStuff.MakeUriArguments(sb, false, args.ToProperties());
			return context.GetOrigin(new Uri(sb.ToString(), UriKind.Relative)).ToString();
		} // func GetUri

		string IHtmlScript.GetUri(string relativeUri, LuaTable args)
			=> GetUri(relativeUri, args);

		#region -- otext, obinary -----------------------------------------------------

		private void CheckOutputOpened()
		{
			if (textOutput != null || streamOutput != null)
				throw new ArgumentException("out is already open.");
		} // func CheckOutputOpened

		private void PrepareOutput()
		{
			CheckOutputOpened();

			if (encoding == null)
				encoding = context.OutputEncoding;
		} // proc PrepareOutput

		[LuaMember("otext")]
		public void OpenText(string contentType = null, Encoding encoding = null)
		{
			PrepareOutput();
			textOutput = new IndentedTextWriter(context.GetOutputTextWriter(contentType ?? this.contentType, encoding), "    ");
		} // proc OpenText

		[LuaMember("obinary")]
		public void OpenBinary(string contentType = null)
		{
			PrepareOutput();
			streamOutput = context.GetOutputStream(contentType ?? this.contentType);
		} // proc OpenBinary

		#endregion

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

		[LuaMember]
		public LuaTable Self { get => this; set { } }

		[LuaMember("Context")]
		public IDEWebRequestScope Context { get => context; set { } }
		[LuaMember("out")]
		public object Output { get => (object)streamOutput ?? textOutput; set { } }
		[LuaMember]
		public string ScriptBase { get => script.ScriptBase; set { } }
	} // class LuaHtmlTable

	#endregion

	#region -- class HttpResponseHelper -----------------------------------------------

	/// <summary>Helper for response creation.</summary>
	public static class HttpResponseHelper
	{
		/// <summary></summary>
		public const long CacheSize = 2L << 20;

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
			context.OutputHeaders["Content-Disposition"] = $"inline; filename = \"{Procs.FilterFileName(fileName)}\"";
			return context;
		} // proc SetAttachment

		/// <summary>Sets the content disposition to the given filename.</summary>
		/// <param name="context"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static IDEWebRequestScope SetAttachment(this IDEWebRequestScope context, string fileName)
		{
			context.OutputHeaders["Content-Disposition"] = $"attachment; filename = \"{Procs.FilterFileName(fileName)}\"";
			return context;
		} // proc SetAttachment

		#endregion

		#region -- Accept Type --------------------------------------------------------

		/// <summary>Test if the type is accepted.</summary>
		/// <param name="r"></param>
		/// <param name="contentType"></param>
		/// <returns></returns>
		public static bool AcceptType(this IDEWebRequestScope r, string contentType)
			=> r.AcceptedTypes == null || r.AcceptedTypes.Length == 0 ? true : Array.FindIndex(r.AcceptedTypes, c => c.StartsWith(contentType)) >= 0;

		#endregion

		#region -- WriteText, WriteBytes, WriteStream ---------------------------------

		private static void PrepareWriteText(IDEWebRequestScope context, ref string contentType, ref Encoding encoding)
		{
			if (encoding == null)
				encoding = context.OutputEncoding;
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
				{
					stream.CopyTo(dst);
					stream.Close();
				}
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
				throw new ArgumentNullException(nameof(resourceName));

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
				assembly.FullName.Replace(" ", "") + "\\" + resourceName, 
				contentType
			);
		} // proc WriteResource

		internal static ILuaScript CreateScript(IDEWebRequestScope context, ILuaLexer code, string scriptBase)
		{
			var luaEngine = context.Server.GetService<IDELuaEngine>(true);
			return luaEngine.CreateScript(
				code,
				scriptBase
			);
		} // func CreateScript

		private static string GetFileNameFromSource(Stream src)
			=> src is FileStream fs ? fs.Name : null;

		private static string GetFileNameFromCacheId(string cacheId)
		{
			if (String.IsNullOrEmpty(cacheId))
				return null;

			var idx = cacheId.LastIndexOf('\\');
			if (idx == -1)
				return cacheId;

			return cacheId.Substring(idx);
		} // func GetFileNameFromCacheId

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="createSource"></param>
		/// <param name="cacheId"></param>
		/// <param name="contentType"></param>
		/// <param name="defaultReadEncoding">Encoding to read text files, default is utf-8.</param>
		public static void WriteContent(this IDEWebRequestScope context, Func<Stream> createSource, string cacheId, string contentType, Encoding defaultReadEncoding = null)
		{
			if (cacheId == null)
				throw new ArgumentNullException(nameof(cacheId));
			if (contentType == null)
				throw new ArgumentNullException(nameof(contentType));

			// check for head request
			if (context.InputMethod == HttpMethod.Head.Method)
			{
				context.SetStatus(HttpStatusCode.OK, "Ok");
				return;
			}

			var http = context.Server.GetService<IDEHttpServer>();
			var o = http?.GetWebCache(cacheId);

			// create the item
			if (o == null)
			{
				using (var src = createSource())
				{
					if (src == null)
						throw new HttpResponseException(HttpStatusCode.NotFound, "Source is not created.");

					var isLua = contentType.StartsWith(MimeTypes.Text.Lua);
					var isHtml = contentType.StartsWith(MimeTypes.Text.Html);
					var cacheItem = isLua || isHtml || (src.CanSeek && src.Length < CacheSize);
					if (cacheItem)
					{
						var isText = contentType.StartsWith("text/");
						var scriptBase = GetFileNameFromSource(src) ?? GetFileNameFromCacheId(cacheId) ?? "dummy";
						if (isLua)
						{
							using (var code = LuaLexer.Create(scriptBase, new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true)))
								o = CreateScript(context, code, scriptBase);
						}
						else if (isHtml)
						{
							using (var chars = new LuaCharLexer(scriptBase, new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true), LuaLexer.HtmlCharStreamLookAHead, false))
							using (var code = LuaLexer.CreateHtml(chars,
								chars.CreateTokenAtStart(LuaToken.Identifier, "otext"),
								chars.CreateTokenAtStart(LuaToken.String, MimeTypes.Text.Html),
								chars.CreateTokenAtStart(LuaToken.Semicolon)
							))
							{
								o = Lua.IsConstantScript(code)
									? code.LookAhead.Value
									: (object)CreateScript(context, code, scriptBase);
							}
						}
						else if (isText)
						{
							using (var tr = new StreamReader(src, defaultReadEncoding ?? Encoding.UTF8, true))
								o = tr.ReadToEnd();
						}
						else
							o = src.ReadInArray() ?? Array.Empty<byte>();

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

						context.OutputHeaders.Set("Cache-Control", "max-age=10");

						using (context.Use())
						using (var g = new LuaHtmlTable(c, context, contentType))
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

		private static DEHttpTableFormat GetTableFormat(IDEWebRequestScope context, DEHttpTableFormat defaultTableFormat)
		{
			if (context.AcceptType(MimeTypes.Text.Lson)) // test if lson is givven
				return DEHttpTableFormat.Lson;
			else if (context.AcceptType(MimeTypes.Text.Json)) // test if json is givven
				return DEHttpTableFormat.Json;
			else
				return defaultTableFormat;
		} // func GetTableFormat

		/// <summary>Write Lua-Table to output.</summary>
		/// <param name="context"></param>
		/// <param name="table"></param>
		/// <param name="defaultTableFormat">Table format, if no accepted mime type exists.</param>
		public static void WriteLuaTable(this IDEWebRequestScope context, LuaTable table, DEHttpTableFormat defaultTableFormat = DEHttpTableFormat.Xml)
		{
			switch (GetTableFormat(context, defaultTableFormat))
			{
				case DEHttpTableFormat.Lson:
					WriteText(context, table.ToLson(false), MimeTypes.Text.Lson);
					break;
				case DEHttpTableFormat.Json:
					WriteText(context, table.ToJson(false), MimeTypes.Text.Json);
					break;
				case DEHttpTableFormat.Xml:
					WriteXml(context, new XDocument(Procs.ToXml(table)));
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(defaultTableFormat), defaultTableFormat, "Invalid table format.");
			}
		} // WriteLuaTable

		/// <summary>Write Lua-Table to output</summary>
		/// <param name="context"></param>
		/// <param name="table"></param>
		/// <param name="defaultTableFormat">Table format, if no accepted mime type exists.</param>
		public static Task WriteLuaTableAsync(this IDEWebRequestScope context, LuaTable table, DEHttpTableFormat defaultTableFormat = DEHttpTableFormat.Xml)
		{
			switch (GetTableFormat(context, defaultTableFormat))
			{
				case DEHttpTableFormat.Lson:
					return WriteTextAsync(context, table.ToLson(false), MimeTypes.Text.Lson);
				case DEHttpTableFormat.Json:
					return WriteTextAsync(context, table.ToJson(false), MimeTypes.Text.Json);
				case DEHttpTableFormat.Xml:
					return WriteXmlAsync(context, new XDocument(Procs.ToXml(table)));
				default:
					throw new ArgumentOutOfRangeException(nameof(defaultTableFormat), defaultTableFormat, "Invalid table format.");
			}
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

		/// <summary></summary>
		/// <param name="dst"></param>
		/// <param name="e"></param>
		public static void WriteBinaryException(Stream dst, Exception e)
		{
			var value = e.GetInnerException().Message;
			if (value.Length > 510)
				value = value.Substring(0, 510);
			else if (value.Length <= 10)
				value = $"Error Detected ({value})";
			var fixedBlock = new byte[1024];
			fixedBlock[0] = 0x23;
			fixedBlock[1] = 0x31;

			var byteCount = Encoding.Unicode.GetBytes(value, 0, value.Length, fixedBlock, 4);
			BitConverter.GetBytes((short)byteCount).CopyTo(fixedBlock, 2);

			dst.Write(fixedBlock, 0, fixedBlock.Length);
		} // func WriteBinaryException

		private static HttpResponseException ThrowUnknownObjectType(object value)
			=> new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Can not send return value of type '{0}'.", value.GetType().FullName));

		#endregion

		#region -- WriteSafeCall ------------------------------------------------------

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="x"></param>
		/// <param name="successMessage"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, XElement x, string successMessage = null)
			=> WriteXml(r, DEConfigItem.SetStatusAttributes(x ?? new XElement("return"), DEHttpReturnState.Ok, successMessage));

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="errorMessage"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, string errorMessage)
			=> WriteObject(r, DEConfigItem.CreateDefaultReturn(r, DEHttpReturnState.Error, errorMessage));

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="userError"></param>
		/// <param name="errorMessage"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, bool userError, string errorMessage)
			=> WriteObject(r, DEConfigItem.CreateDefaultReturn(r, userError ? DEHttpReturnState.User : DEHttpReturnState.Error, errorMessage));

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="e"></param>
		public static void WriteSafeCall(this IDEWebRequestScope r, Exception e)
		{
			if (e is ILuaUserRuntimeException userMessage)
				WriteSafeCall(r, true, userMessage.Message);
			else
				WriteSafeCall(r, false, e.Message);
		} // proc WriteSafeCall

		#endregion
	} // class HttpResponseHelper

	#endregion

	#region -- class HttpRequestHelper ------------------------------------------------

	/// <summary>Helper for input requests.</summary>
	public static class HttpRequestHelper
	{
		/// <summary>Parse input stream as xml element.</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public static XElement GetXml(this IDEWebRequestScope r)
		{
			using (var xml = XmlReader.Create(r.GetInputTextReader(), Procs.XmlReaderSettings))
				return XElement.Load(xml);
		} // func GetXml

		/// <summary>Parse input stream as xml element.</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public static Task<XElement> GetXmlAsync(this IDEWebRequestScope r)
			=> Task.Run(() => GetXml(r));

		/// <summary>Parse input stream as lua-table</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public static LuaTable GetTable(this IDEWebRequestScope r)
		{
			if (MediaTypeHeaderValue.TryParse(r.InputContentType, out var contentType))
			{
				if (contentType.MediaType == MimeTypes.Text.Xml)
					return Procs.CreateLuaTable(GetXml(r));
				else if (contentType.MediaType == MimeTypes.Text.Lson)
				{
					using (var tr = r.GetInputTextReader())
						return LuaTable.FromLson(tr);
				}
				else if (contentType.MediaType == MimeTypes.Text.Json)
				{
					using (var tr = r.GetInputTextReader())
						return LuaTable.FromJson(tr);
				}
				else
					throw new ArgumentOutOfRangeException(nameof(r.InputContentType), r.InputContentType, "InputContentType is neither xml nor lson.");
			}
			else
				throw new ArgumentException("InputContentType is missing.", nameof(r.InputContentType));
		} // func GetTable

		/// <summary>Parse input stream as lua-table</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public static Task<LuaTable> GetTableAsync(this IDEWebRequestScope r)
			=> Task.Run(() => GetTable(r));
	} // class HttpRequestHelper

	#endregion
}
