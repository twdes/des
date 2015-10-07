using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Http
{
	#region -- interface IDEHttpServer --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEHttpServer : IDEConfigItem
	{
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
		/// <summary>Wird der Server im Debug-Modus betrieben</summary>
		bool Debug { get; }
	} // interface IDEHttpServer

	#endregion

	#region -- class HttpResponse -------------------------------------------------------

	/// <summary></summary>
	/// <param name="parameterName"></param>
	/// <param name="default"></param>
	/// <returns></returns>
	public delegate object HttpGetParameterDelegate(string parameterName, string @default);

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Klasse die eine Http-Abfrage verwaltet.</summary>
	public sealed class HttpResponse : IDEConfigActionCaller, IDisposable
	{
		#region -- struct RelativeFrame ---------------------------------------------------

		private struct RelativeFrame
		{
			public int AbsolutePosition;
			public IServiceProvider Item;
		} // struct RelativeFrame

		#endregion

		#region -- LuaHttpTable -----------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class LuaHttpTable : LuaTable, IDisposable
		{
			private HttpResponse r;
			private DEConfigItem item;
			private Stream dst = null;
			private TextWriter tw = null;
			private Encoding encoding = null;

			public LuaHttpTable(DEConfigItem item, HttpResponse r)
			{
				this.item = item;
				this.r = r;
			} // ctor

			public void Dispose()
			{
				Procs.FreeAndNil(ref dst);
				Procs.FreeAndNil(ref tw);
			} // proc Dispose

			[LuaMember("print")]
			protected void LuaPrint(string sText)
			{
				if (tw != null)
					tw.WriteLine(sText);
				else if (dst != null)
				{
					byte[] b = encoding.GetBytes(sText);
					dst.Write(b, 0, b.Length);
				}
				else
					throw new ArgumentException("out is not opened.");
			} // proc OnPrint

			[LuaMember("otext")]
			public void OpenText(string sContentType, Encoding encoding = null)
			{
				if (tw != null || dst != null)
					throw new ArgumentException("out is open.");

				if (encoding == null)
					encoding = r.server.Encoding;

				tw = r.GetOutputTextWriter(sContentType, encoding);
			} // proc OpenText

			[LuaMember("obinary")]
			private void OpenBinary(string sContentType, Encoding encoding = null)
			{
				if (tw != null || dst != null)
					throw new ArgumentException("out is open.");

				if (encoding == null)
					encoding = r.server.Encoding;

				dst = r.GetOutputStream(sContentType, encoding);
			} // proc OpenBinary

			protected override object OnIndex(object key)
			{
				return base.OnIndex(key) ?? item.GetValue(key);
			} // func OnIndex

			[LuaMember("r")]
			public HttpResponse Response { get { return r; } set { } }
			[LuaMember("out")]
			public object Output { get { return (object)dst ?? tw; } set { } }
		} // class LuaHttpTable

		#endregion

		private IDEHttpServer server;
		private HttpListenerContext ctx;
		private IDEAuthentificatedUser user;
		private Stack<RelativeFrame> relativeStack = new Stack<RelativeFrame>();
		private IServiceProvider currentRelativeNode = null;
		private string currentRelativePath = null;
		private string absolutePath;

		private CultureInfo ciClient = null;
		private Stopwatch sw = null;
		private LogMessageScopeProxy log = null;

		#region -- Ctor/Dtor --------------------------------------------------------------

		/// <summary>Erzeugt, die Abfrage.</summary>
		/// <param name="server">Zugriff auf den Http-Server</param>
		/// <param name="ctx">HttpListerContext</param>
		/// <param name="absolutePath">Absoluter Pfad, der angegeben wurde</param>
		public HttpResponse(IDEHttpServer server, HttpListenerContext ctx, string absolutePath)
		{
			this.server = server;
			this.ctx = ctx;
			this.absolutePath = absolutePath;

			PrepareHeader();

			sw = new Stopwatch();
			sw.Start();

			AuthentificateUser();
		} // ctor

		private void PrepareHeader()
		{
			ctx.Response.Headers["Server"] = "DES";
		} // proc PrepareHeader

		/// <summary>Zertöre die Antwort</summary>
		public void Dispose()
		{
			Log(l => l.WriteLine("=== Dauer = {0:N0}ms, {1:N0}ticks ===", sw.ElapsedMilliseconds, sw.ElapsedTicks));

			if (log != null)
				log.AutoFlush();

			// Zerstöre den Log-Eintrag
			Procs.FreeAndNil(ref log);
			// Zerstöre Nutzer Context
			Procs.FreeAndNil(ref user);

			// Schließe den Kontext
			try { ctx.Response.Close(); }
			catch { }
		} // proc Dispose

		#endregion

		#region -- Log --------------------------------------------------------------------

		/// <summary>Starte die Aufzeichnung der Verbindung</summary>
		public void LogStart()
		{
			if (log != null)
				return; // Log schon gestartet

			log = server.LogProxy().GetScope(LogMsgType.Information, false);
			log.WriteLine("{0}: {1}", ctx.Request.HttpMethod, ctx.Request.Url);
			log.WriteLine();
			log.WriteLine("UrlReferrer: {0}", ctx.Request.UrlReferrer);
			log.WriteLine("UserAgent: {0}", ctx.Request.UserAgent);
			log.WriteLine("UserHostAddress: {0}", ctx.Request.UserHostAddress);
			log.WriteLine("Content-Type: {0}", ctx.Request.ContentType);
			log.WriteLine();
			log.WriteLine("Header:");
			var headers = ctx.Request.Headers;
			foreach (var k in headers.AllKeys)
				log.WriteLine("  {0}: {1}", k, headers[k]);
			log.WriteLine();
		} // proc LogStart

		/// <summary>Stopped die Aufzeichnung der Verbindung</summary>
		public void LogStop()
		{
			Procs.FreeAndNil(ref log);
		} // proc LogStop

		/// <summary>Schreibt eine Meldung ins Log</summary>
		/// <param name="action">Code, für die Log-Meldung</param>
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
			if (String.IsNullOrEmpty(securityToken))
				return;
			return;

			if (User == null)
				throw new HttpResponseException(HttpStatusCode.Unauthorized, "Anonymous hat keine Berechtigung.");
			else if (!user.IsInRole(securityToken))
				throw new HttpResponseException(HttpStatusCode.Forbidden, String.Format("Nutzer {0} darf nicht auf Token {1} zugreifen.", user.Identity.Name, securityToken));
		} // proc DemandToken

		/// <summary>Darf der Nutzer, den entsprechenden Token verwenden.</summary>
		/// <param name="securityToken">Zu prüfender Token.</param>
		/// <returns><c>true</c>, wenn der Token erlaubt ist.</returns>
		public bool TryDemandToken(string securityToken)
		{
			if (String.IsNullOrEmpty(securityToken))
				return true;

			return user != null && user.IsInRole(securityToken);
		} // proc TryDemandToken

		/// <summary>Wandelt die angegebene Id in den entsprechenden Nutzer um. Hat die Verbindung keinen Nutzer, so wird nichts gemacht.</summary>
		private void AuthentificateUser()
		{
			if (ctx.User != null)
			{
				user = Server.Server.AuthentificateUser(ctx.User.Identity);
				if (user == null)
					throw new HttpResponseException(HttpStatusCode.Unauthorized, String.Format("Authentifizierung fehlgeschlagen für {0}.", ctx.User.Identity.Name));
			}
		} // proc AuthentificateUser

		#endregion

		#region -- Relative Path ----------------------------------------------------------

		/// <summary>Wechselt in das virtuelle Verzeichnis</summary>
		/// <param name="subPath"></param>
		public void PushPath(IServiceProvider sp, string subPath)
		{
#if DEBUG
			if (sp == null || subPath == null)
				throw new ArgumentNullException();
#endif

			// Prüfe den Pfad für diesen Nutzer
			DEConfigItem item = sp as DEConfigItem;
			if (item != null)
				DemandToken(item.SecurityToken);

			// Normalisiere den SubPfad
			subPath = NormalizePath(subPath);

			// Prüfe die Position
			if (!ExistsPath(subPath))
				throw new ArgumentException("Ungültiger Pfadangabe.");

			// Erzeuge einen neuen Frame
			RelativeFrame f;
			GetCurrentFrame(out f);
			relativeStack.Push(new RelativeFrame { AbsolutePosition = subPath.Length == 0 ? f.AbsolutePosition : f.AbsolutePosition + subPath.Length + 1, Item = sp });

			// Lösche Cache
			RelativeCacheClear();
		} // proc PushPath

		/// <summary>Setzt den relativen Bezug zurück.</summary>
		public void PopPath()
		{
			if (relativeStack.Count > 0)
			{
				relativeStack.Pop();
				RelativeCacheClear();
			}
		} // proc PopPath

		public bool ExistsPath(string sSubPath)
		{
			sSubPath = NormalizePath(sSubPath);
			return sSubPath.Length == 0 || RelativePath.StartsWith(sSubPath, StringComparison.OrdinalIgnoreCase);
		} // func ExistsPath

		private void GetCurrentFrame(out RelativeFrame frame)
		{
			if (relativeStack.Count == 0)
				frame = new RelativeFrame { AbsolutePosition = 1, Item = server };
			else
				frame = relativeStack.Peek();
		} // func GetCurrentFrame

		private void RelativeCacheClear()
		{
			currentRelativeNode = null;
			currentRelativePath = null;
		} // proc RelativeCacheClear

		private static string NormalizePath(string sSubPath)
		{
			if (sSubPath.Length > 0)
			{
				if (sSubPath[0] == '/')
					sSubPath = sSubPath.Substring(1);
				if (sSubPath.Length > 0 && sSubPath[sSubPath.Length - 1] == '/')
					sSubPath = sSubPath.Substring(0, sSubPath.Length - 1);
			}
			return sSubPath;
		} // func NormalizePath

		/// <summary>Gibt den zugeordneten Knoten zurück.</summary>
		public IServiceProvider RelativeNode
		{
			get
			{
				if (currentRelativeNode == null)
				{
					RelativeFrame f;
					GetCurrentFrame(out f);
					currentRelativeNode = f.Item;
				}
				return currentRelativeNode;
			}
		} // prop RelativeNode

		/// <summary>Gibt den Relativen Pfad zurück</summary>
		public string RelativePath
		{
			get
			{
				if (currentRelativePath == null)
				{
					RelativeFrame f;
					GetCurrentFrame(out f);
					currentRelativePath = f.AbsolutePosition >= absolutePath.Length ? String.Empty : absolutePath.Substring(f.AbsolutePosition);
				}
				return currentRelativePath;
			}
		} // prop RelativePath

		public string RelativePathName
		{
			get
			{
				if (RelativePath == null)
					return null;

				int iPos = RelativePath.IndexOf('/');
				if (iPos == -1)
					return RelativePath;
				else
					return RelativePath.Substring(0, iPos);
			}
		} // prop RelativePathName

		#endregion

		#region -- Parameter --------------------------------------------------------------

		/// <summary>Gibt einen Parameter zurück</summary>
		/// <param name="parameterName"></param>
		/// <param name="default"></param>
		/// <returns></returns>
		public string GetParameter(string parameterName, string @default)
		{
			var p = ctx.Request.QueryString;
			var value = p[parameterName];

			if (value == null)
			{
				foreach (var c in p.AllKeys)
					if (String.Compare(c, parameterName, true) == 0)
						return p[c];
			}

			return value ?? @default;
		} // func GetParameter

		/// <summary>Liste der Parameter</summary>
		public string[] ParameterNames
		{
			get { return ctx.Request.QueryString.AllKeys; }
		} // prop ParameterNames

		/// <summary>Anzahl der gefundenen Parameter</summary>
		public int ParameterCount => ctx.Request.QueryString.Count;
		/// <summary>Zeichenfolge der Anfrage</summary>
		public string QueryString => ctx.Request.Url.Query;

		#endregion

		#region -- Write ------------------------------------------------------------------

		public void WriteObject(object value, string contentType = null)
		{
			if (value is XElement)
				WriteXml((XElement)value);
			else if (value is XDocument)
				WriteXml((XDocument)value);
			else if (value is IDataReader)
				WriteDataReader((IDataReader)value);
			else if (value is IDataRecord)
				WriteDataRecord((IDataRecord)value);
			else if (value is string)
			{
				using (TextWriter tw = GetOutputTextWriter(String.IsNullOrEmpty(contentType) ? "text/plain" : contentType))
					tw.Write((string)value);
			}
			else if (value is Stream)
				WriteStream((Stream)value, null, contentType);
			else if (value is byte[])
			{
				using (MemoryStream src = new MemoryStream((byte[])value))
					WriteStream(src, null, contentType);
			}
			else
				throw new HttpResponseException(HttpStatusCode.BadRequest, "Rückgabe kann nicht gesendet werden.");
		} // proc Write

		public void WriteTextAsHtml(string text)
		{
			using (TextWriter tw = GetOutputTextWriter("text/html"))
			{
				tw.Write("<html><body><code>");
				HttpUtility.HtmlEncode(text, tw);
				tw.Write("</code></body></html>");
			}
		} // proc WriteText

		public void WriteXml(XElement value)
		{
			var doc = new XDocument(
				new XDeclaration("1.0", server.Encoding.WebName, "yes"),
				value
			);
			WriteXml(doc, true);
		} // proc Write

		public void WriteXml(XDocument value, bool lAllowJson = false)
		{
			using (TextWriter tw = GetOutputTextWriter("text/xml"))
				value.Save(tw);
		} // proc Write

		public void WriteFile(string sFileName, HttpGetParameterDelegate args = null, string sContentType = null)
		{
			if (String.IsNullOrEmpty(sFileName))
				throw new ArgumentNullException("fileName");

			// Ermittle den ContentType
			if (sContentType == null)
				sContentType = server.GetContentType(Path.GetExtension(sFileName));

			// Öffne den Datenstrom
			using (FileStream src = new FileStream(sFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
				WriteStream(src, args, sContentType, File.GetLastWriteTime(sFileName).ToString("o") + "\t" + Path.GetFullPath(sFileName));
		} // proc WriteFile

		public void WriteResource(Type type, string sResourceName, HttpGetParameterDelegate args = null, string sContentType = null)
		{
			if (String.IsNullOrEmpty(sResourceName))
				throw new ArgumentNullException("ResourceName");

			// Öffne die Resource
			WriteResource(type.Assembly, type.Namespace + '.' + sResourceName, args, sContentType);
		} // proc WriteResource

		public void WriteResource(Assembly assembly, string sResourceName, HttpGetParameterDelegate args = null, string sContentType = null)
		{
			using (Stream src = assembly.GetManifestResourceStream(sResourceName))
			{
				if (src == null)
					throw new ArgumentException(String.Format("'{0}' nicht gefunden.", sResourceName));

				// Ermittle den ContentType
				if (sContentType == null)
					sContentType = server.GetContentType(Path.GetExtension(sResourceName));

				WriteStream(src, args, sContentType, "\\\\" + assembly.FullName.Replace(" ", "") + "\\" + sResourceName);
			}
		} // proc WriteResource

		public void WriteFileOrResource(string sFileName, Type type, string sResourceName, HttpGetParameterDelegate args = null, string sContentType = null)
		{
			if (File.Exists(sFileName))
				WriteFile(sFileName, args, sContentType);
			else
				WriteResource(type, sResourceName, args, sContentType);
		} // proc WriteFileOrResource

		private StreamReader OpenStreamReader(Stream src)
		{
			// Ermittle die Codierung
			Encoding encSource = null;
			bool lDetectEncoding = false;
			if (src.CanSeek)
			{
				byte[] bPreamble = new byte[4];
				int iReaded = src.Read(bPreamble, 0, 4);

				if (iReaded >= 3 && bPreamble[0] == 0xEF && bPreamble[1] == 0xBB && bPreamble[2] == 0xBF) // UTF-8
					encSource = Encoding.UTF8;
				else if (iReaded == 4 && bPreamble[0] == 0x00 && bPreamble[1] == 0x00 && bPreamble[2] == 0xFE && bPreamble[3] == 0xFF) // UTF-32 EB
				{
					encSource = Encoding.UTF32; // ist zwar EL aber StreamReader sollte auf EB schalten
					lDetectEncoding = true;
				}
				else if (iReaded == 4 && bPreamble[0] == 0xFF && bPreamble[1] == 0xFE && bPreamble[2] == 0x00 && bPreamble[3] == 0x00) // UTF-32 EL
					encSource = Encoding.UTF32;
				else if (iReaded >= 2 && bPreamble[0] == 0xFE && bPreamble[1] == 0xFF) // UTF-16 EB
					encSource = Encoding.BigEndianUnicode;
				else if (iReaded >= 2 && bPreamble[0] == 0xFF && bPreamble[1] == 0xFE) // UTF-16 EL
					encSource = Encoding.Unicode;
				else
					encSource = Encoding.Default;

				src.Seek(-iReaded, SeekOrigin.Current);
			}
			else
			{
				encSource = Encoding.Default;
				lDetectEncoding = true;
			}

			// Öffne den StreamReader
			return new StreamReader(src, encSource, lDetectEncoding);
		} // func OpenStreamReader

		public void WriteStream(Stream src, HttpGetParameterDelegate args, string sContentType, string sCacheId = null)
		{
			if (String.IsNullOrEmpty(sContentType))
				throw new ArgumentNullException("contentype");

			// Frage den Cache ab
			object cache;
			if (sCacheId != null && (cache = server.GetWebCache(sCacheId)) != null)
			{
				if (cache is string) // Text, der gesendet wird
					WriteStreamText((string)cache, sContentType);
				else if (cache is byte[])
					WriteStreamBytes((byte[])cache, sContentType);
				else if (cache is ILuaScript)
					WriteStreamLua((ILuaScript)cache, sContentType);
				else
					throw new ArgumentException("Invalid cache-Eintrag.");
			}
			else // Führe den Eintrag direkt aus
			{
				if (sContentType == "text/lua")
				{
					using (TextReader tr = OpenStreamReader(src))
						WriteStreamLua(sCacheId, tr, GetLuaName(sCacheId) ?? "generic.lua", sContentType);
				}
				else if (sContentType == "text/html")
				{
					using (TextReader tr = OpenStreamReader(src))
					{
						bool lPlainHtml;
						string sScript = Parse(tr, src.CanSeek ? src.Length : 0, out lPlainHtml);

						if (lPlainHtml)
						{
							server.UpdateWebCache(sCacheId, sScript);
							WriteStreamText(sScript, sContentType);
						}
						else
							using (TextReader tr2 = new StringReader(sScript))
								WriteStreamLua(sCacheId, tr2, GetLuaName(sCacheId) ?? "generic.html", sContentType);
					}
				}
				else if (sContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
				{
					if (sCacheId != null && src.CanSeek && src.Length < 2 << 20)
					{
						string sContent;
						using (TextReader tr = OpenStreamReader(src))
							sContent = tr.ReadToEnd();
						server.UpdateWebCache(sCacheId, sContent);
						WriteStreamText(sContent, sContentType);
					}
					else
						using (TextReader tr = OpenStreamReader(src))
						using (TextWriter tw = GetOutputTextWriter(sContentType))
						{
							string sLine;
							while ((sLine = tr.ReadLine()) != null)
								tw.WriteLine(sLine);
						}
				}
				else if (sCacheId != null && src.CanSeek && src.Length < 2 << 20)
				{
					var bContent = src.ReadInArray();
					server.UpdateWebCache(sCacheId, bContent);
					WriteStreamBytes(bContent, sContentType);
				}
				else
				{
					using (Stream dst = GetOutputStream(sContentType))
					{
						if (src.CanSeek)
							ctx.Response.ContentLength64 = src.Length;

						src.CopyTo(dst, 4096);
					}
				}
			}
		} // func WriteStream

		private string GetLuaName(string sName)
		{
			int iPos = sName.IndexOf('\t');
			if (iPos >= 0)
				return sName.Substring(iPos + 1);
			else
				return sName;
		} // func GetLuaName

		private void WriteStreamText(string sText, string sContentType)
		{
			using (TextWriter tr = GetOutputTextWriter(sContentType))
				tr.Write(sText);
		} // proc WriteStreamText

		private void WriteStreamBytes(byte[] data, string sContentType)
		{
			using (Stream dst = GetOutputStream(sContentType))
				dst.Write(data, 0, data.Length);
		} // proc WriteStreamBytes

		private void WriteStreamLua(string sCacheId, TextReader trCode, string sName, string sContentType)
		{
			ILuaScript s = server.GetService<IDELuaEngine>(true).CreateScript(() => trCode, sName, server.Debug,
				new KeyValuePair<string, Type>("contenttype", typeof(string))
			);
			bool lCached = server.UpdateWebCache(sCacheId, s);
			try
			{
				WriteStreamLua(s, sContentType);
			}
			finally
			{
				if (!lCached)
					s.Dispose();
			}
		} // proc WriteStreamLua

		private void WriteStreamLua(ILuaScript script, string sContentType)
		{
			using (LuaHttpTable g = new LuaHttpTable(RelativeNode.GetService<DEConfigItem>(false), this))
			{
				if (sContentType != "text/lua" && sContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
				{
					// Erzeuge die Ausgabe
					g.OpenText(sContentType);
					script.Run(g, sContentType);
				}
				else
					script.Run(g, sContentType);
			}
		} // proc WriteStreamLua

		private XmlWriter CreateXmlWriter()
		{
			XmlWriterSettings settings = Procs.XmlWriterSettings;
			settings.NewLineHandling = NewLineHandling.None;
			return XmlWriter.Create(GetOutputTextWriter("text/xml"), settings);
		} // func CreateXmlWriter

		private string GetLuaCompatibleType(Type type)
		{
			LuaType t = LuaType.GetType(type);
			return t.AliasName ?? t.FullName;
		} // func GetLuaCompatibleType

		private void WriteDataRecord(IDataRecord r)
		{
			using (XmlWriter xml = CreateXmlWriter())
			{
				xml.WriteStartElement("datarecord");

				for (int i = 0; i < r.FieldCount; i++)
				{
					// erzgeuge <r n="a" t="int">12</r>		
					xml.WriteStartElement("r");
					xml.WriteAttributeString("n", r.GetName(i));
					xml.WriteAttributeString("t", GetLuaCompatibleType(r.GetFieldType(i)));
					if (!r.IsDBNull(i))
						xml.WriteValue(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture));
					xml.WriteEndElement();
				}

				xml.WriteEndElement();
			}
		} // proc WriteDataRecord

		private void WriteDataReader(IDataReader r)
		{
			using (XmlWriter xml = CreateXmlWriter())
			{
				xml.WriteStartElement("datareader");

				// Schreibe Schema
				DataTable dtShema;
				xml.WriteStartElement("schema");
				if (ctx.Request.Headers["ppsn-full-schema"] == Boolean.TrueString)
				{
					dtShema = r.GetSchemaTable();
					xml.WriteStartElement("extended");
					for (int j = 0; j < dtShema.Columns.Count; j++)
					{
						xml.WriteStartElement("r");
						xml.WriteAttributeString("n", dtShema.Columns[j].ColumnName);
						xml.WriteAttributeString("t", GetLuaCompatibleType(dtShema.Columns[j].DataType));
						xml.WriteEndElement();
					}
					xml.WriteEndElement();
				}
				else
					dtShema = null;

				for (int i = 0; i < r.FieldCount; i++)
				{
					xml.WriteStartElement("r");

					// Schreibe den Namen und Typen
					xml.WriteAttributeString("n", r.GetName(i));
					xml.WriteAttributeString("t", GetLuaCompatibleType(r.GetFieldType(i)));

					// Schreibe weitere Attribute
					if (dtShema != null)
					{
						DataRow rd = dtShema.Rows[i];
						for (int j = 0; j < dtShema.Columns.Count; j++)
						{
							xml.WriteStartElement("n");
							if (rd.IsNull(j))
								xml.WriteValue(Convert.ToString(rd[j], CultureInfo.InvariantCulture));
							xml.WriteEndElement();
						}
					}

					xml.WriteEndElement();
				}
				xml.WriteEndElement();

				// Schreibe die Daten
				while (r.Read())
				{
					xml.WriteStartElement("r");
					for (int i = 0; i < r.FieldCount; i++)
					{
						xml.WriteStartElement("c");
						object o = r.GetValue(i);
						if (o != DBNull.Value)
							xml.WriteValue(Convert.ToString(o, CultureInfo.InvariantCulture));
						xml.WriteEndElement();
					}
					xml.WriteEndElement();
				}

				xml.WriteEndElement();
			}
		} // proc WriteDataReader
		
		#endregion

		#region -- Parse ------------------------------------------------------------------

		private static string Parse(TextReader tr, long iCapacity, out bool lPlainHtml)
		{
			char[] cRead = new char[4096];

			StringBuilder sbLua = new StringBuilder(unchecked((int)iCapacity));
			StringBuilder sbHtml = new StringBuilder();
			StringBuilder sbCmd = new StringBuilder();
			int iReaded;
			int iState = 0;
			bool lInCommand = false;

			lPlainHtml = true;

			do
			{
				// Lese einen Abschnitt
				iReaded = tr.Read(cRead, 0, cRead.Length);

				// Parse den HtmlText
				for (int i = 0; i < iReaded; i++)
				{
					char c = cRead[i];

					switch (iState)
					{
						#region -- Basis --
						case 0: // Basis
							if (c == '<') // Öffnende Spitze Klammer
							{
								iState = 1;
							}
							else
								sbHtml.Append(c);
							break;

						case 1: // Prüfe den Typ
							if (c == '!') // Kommentar?
								iState = 10;
							else if (c == '%') // Befehl
								iState = 20;
							else
							{
								sbHtml.Append('<');
								iState = 0;
								goto case 0;
							}
							break;
						#endregion
						#region -- 10 - Kommentar --
						case 10:
							if (c == '-')
								iState = 11;
							else
							{
								sbHtml.Append("<!");
								iState = 0;
								goto case 0;
							}
							break;
						case 11:
							if (c == '-')
								iState = 12;
							else
							{
								sbHtml.Append("<!-");
								iState = 0;
								goto case 0;
							}
							break;
						case 12:
							if (c == '-')
								iState = 13;
							break;
						case 13:
							if (c == '-')
								iState = 14;
							else
								iState = 12;
							break;
						case 14:
							if (c == '>')
								iState = 0;
							else
								iState = 12;
							break;
						#endregion
						#region -- 20 - Befehl --
						case 20:
							lInCommand = true;
							sbCmd.Length = 0;
							iState = 21;
							break;
						case 21:
							if (c == '%')
								iState = 22;
							else
								sbCmd.Append(c);
							break;
						case 22:
							if (c == '>')
							{
								iState = 0;

								lPlainHtml = false;
								lInCommand = false;
								GenerateHtmlBlock(sbLua, sbHtml);
								sbLua.Append(sbCmd).AppendLine(); // Füge das Script einfach ein

								sbCmd.Length = 0;
							}
							else
							{
								iState = 21;
								sbCmd.Append('%');
								goto case 21;
							}
							break;
						#endregion
						default:
							throw new InvalidOperationException();
					}
				}
			} while (iReaded > 0);

			// Prüfe einen offnen Commandblock
			if (lInCommand)
				throw new ArgumentException("Befehl nicht sauber abgeschlossen.");

			if (sbHtml.Length > 0)
				if (lPlainHtml)
					sbLua.Append(sbHtml);
				else
					GenerateHtmlBlock(sbLua, sbHtml);

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

		#region -- Output -----------------------------------------------------------------

		/// <summary>Erzeugt den Datenstrom für die Rückgabe</summary>
		/// <param name="sContentType"></param>
		/// <returns></returns>
		public Stream GetOutputStream(string sContentType, Encoding encoding = null)
		{
			if (String.IsNullOrEmpty(sContentType))
				throw new ArgumentNullException("contenttype");

			bool lIsText = IsContentTypeText(sContentType);

			// Setze den Content-Type
			if (encoding != null)
				ctx.Response.ContentType = sContentType + "; charset=" + encoding.WebName;
			else
				ctx.Response.ContentType = sContentType;

			// Packe den Ausgabestrom
			if (lIsText)
			{
				string sAcceptEncoding = ctx.Request.Headers["Accept-Encoding"];
				if (sAcceptEncoding != null && sAcceptEncoding.IndexOf("gzip") >= 0)
				{
					ctx.Response.Headers["Content-Encoding"] = "gzip";
					return new GZipStream(ctx.Response.OutputStream, CompressionMode.Compress);
				}
				else
					return ctx.Response.OutputStream;
			}
			else
			{
				return ctx.Response.OutputStream;
			}
		} // func GetOutputStream

		/// <summary>Erzeugt einen Text-Datenstrom für die Rückgabe.</summary>
		/// <param name="sContentType"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public TextWriter GetOutputTextWriter(string sContentType, Encoding encoding = null)
		{
			if (encoding == null)
				encoding = server.Encoding;

			return new StreamWriter(GetOutputStream(sContentType, encoding), encoding);
		} // func GetOutputTextWriter

		#endregion

		#region -- Input ------------------------------------------------------------------

		/// <summary>Zugriff auf den Inputstream</summary>
		/// <returns></returns>
		public TextReader GetIntputTextReader()
		{
			return new StreamReader(ctx.Request.InputStream, InputEncoding);
		} // func GetIntputTextReader

		/// <summary>Datenstrom der Input-Daten</summary>
		public Stream InputStream { get { return ctx.Request.InputStream; } }
		/// <summary>Encodierung der Inputdaten</summary>
		public Encoding InputEncoding { get { return ctx.Request.ContentEncoding; } }
		/// <summary>Optional, länge des Input-Datenstroms</summary>
		public long InputLength { get { return ctx.Request.ContentLength64; } }

		#endregion

		/// <summary>Methode</summary>
		public string HttpMethod { get { return ctx.Request.HttpMethod; } }

		public T GetUser<T>()
			where T : class
		{
			var user = User;

			if (user == null)
				throw new HttpResponseException(HttpStatusCode.Unauthorized, "Authentifizierung notwendig.");

			T r = user as T;
			if (r == null)
				throw new ArgumentNullException("Falsches Nutzerformat.");

			return r;
		} // func GetUser


		/// <summary>Authentifizerter Nutzer</summary>
		public IDEAuthentificatedUser User { get { return user; } }

		/// <summary>Zugriff auf den Http-Server</summary>
		public IDEHttpServer Server { get { return server; } }

		/// <summary>Gibt den Absoluten Pfad zurück</summary>
		public string AbsoluteUrl { get { return absolutePath; } }

		/// <summary>Ermittle die Kultur</summary>
		public CultureInfo CultureInfo
		{
			get
			{
				if(ciClient == null)
					ciClient = GetCultureInfo(ctx.Request.Headers["Accept-Language"]);
				return ciClient;
			}
		} // prop CultureInfo

		/// <summary>Zugriff auf den Anfrage-Header</summary>
		public NameValueCollection RequestHeaders
		{
			get { return ctx.Request.Headers; }
		} // prop ResponseHeaders 

		/// <summary>Zugriff auf den Antwort-Header</summary>
		public WebHeaderCollection ResponseHeaders
		{
			get { return ctx.Response.Headers; }
		} // prop ResponseHeaders 

		/// <summary>Wurde eine Antwort generiert</summary>
		public bool IsResponseSended { get { return ctx.Response.ContentType != null; } }

		// -- Static ----------------------------------------------------------------------

		private static bool IsContentTypeText(string sContentType)
		{
			return sContentType.StartsWith("text", StringComparison.OrdinalIgnoreCase);
		} // func IsContentTypeText

		private static CultureInfo GetCultureInfo(string sAcceptLanguages)
		{
			try
			{
				if (String.IsNullOrEmpty(sAcceptLanguages))
					return CultureInfo.GetCultureInfo("de-DE");
				else
				{
					string[] languages = sAcceptLanguages.Split(',');
					return CultureInfo.GetCultureInfo(languages[0]);
				}
			}
			catch
			{
				return CultureInfo.GetCultureInfo("de-DE");
			}
		} // func GetCultureInfo
	} // class HttpResponse

	#endregion
}
