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
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.IO
{
	#region -- class FtpItemAttributesConverter ---------------------------------------

	/// <summary>Ftp attributes converter</summary>
	public class FtpItemAtributesConverter : TypeConverter
	{
		#region -- ConvertTo ----------------------------------------------------------

		private string ConvertToString(int attributes)
		{
			var ret = new char[10] { '-', '-', '-', '-', '-', '-', '-', '-', '-', '-' };

			if ((attributes & (int)FtpItemAttributes.Directory) != 0)
				ret[0] = 'd';

			for (var i = 0; i < 3; i++)
			{
				var m = (2 - i) * 3;

				if ((attributes & (4 << m)) != 0)
					ret[i * 3 + 1] = 'r';
				if ((attributes & (2 << m)) != 0)
					ret[i * 3 + 2] = 'w';

				if ((attributes & (1 << m)) != 0)
				{
					char c;
					if (i == 0)
					{
						if ((attributes & (int)FtpItemAttributes.SetUidMode) != 0)
							c = 's';
						else
							c = 'x';
					}
					else if (i == 1)
					{
						if ((attributes & (int)FtpItemAttributes.SetGidMode) != 0)
							c = 's';
						else
							c = 'x';
					}
					else
					{
						if ((attributes & (int)FtpItemAttributes.StickyBit) != 0)
							c = 't';
						else
							c = 'x';
					}
					ret[i * 3 + 3] = c;
				}
			}

			return new string(ret);
		} // func ConvertToString

		/// <summary>Konvertiert eine FtpAttributes-Enum-Instanz in einen String ("-rwx-r-xr-x" o.ä.) oder eine Ganzzahl (755 o.ä)</summary>
		/// <param name="context">Context</param>
		/// <param name="culture">Culture</param>
		/// <param name="value">Die zu konvertierende Instanz</param>
		/// <param name="destinationType">Der Zieltyp (string, int)</param>
		/// <returns>String / Int</returns>
		public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (value is FtpItemAttributes attributes)
			{
				if (destinationType == typeof(string))
					return ConvertToString((int)value);
				else
					return base.ConvertTo(context, culture, value, destinationType);
			}
			else
				throw new ArgumentException("Only FtpItemAttributes can be converted", nameof(value));
		} // func ConvertTo

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="destinationType"></param>
		/// <returns></returns>
		public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
		{
			if (destinationType == typeof(string))
				return true;
			else
				return base.CanConvertTo(context, destinationType);
		} // func CanConvertTo

		#endregion

		#region -- ConvertFrom --------------------------------------------------------

		private FtpItemAttributes ConvertFromInt(int value)
		{
			if (value >= 0 && value <= 0x1FFF)
				return (FtpItemAttributes)value;
			else
				throw new FormatException("Invalid Unix-Attributes");
		} // func ConvertFromInt

		private FtpItemAttributes InternalConvertFromString(string value)
		{
			if (String.IsNullOrEmpty(value) || value.Length != 10)
				throw new FormatException("Invalid Unix-Attributes (len)");

			var ret = FtpItemAttributes.None;

			// Verzeichnis?
			if (value[0] == 'd')
				ret |= FtpItemAttributes.Directory;

			// Gruppenattribute (Idx=0 User, 1 Group, 2 Other)
			for (var i = 0; i < 3; i++)
			{
				var m = (2 - i) * 3;

				// Normalen Schreibrechte
				if (value[i * 3 + 1] == 'r')
					ret |= (FtpItemAttributes)(4 << m);
				if (value[i * 3 + 2] == 'w')
					ret |= (FtpItemAttributes)(2 << m);

				var c = value[i * 3 + 3];
				if (c == 'x' || c == 's')
				{
					ret |= (FtpItemAttributes)(1 << m);
					if (c == 's' || c == 't')
					{
						if (i == 0)
							ret |= FtpItemAttributes.SetUidMode;
						else if (i == 1)
							ret |= FtpItemAttributes.SetGidMode;
						else
							ret |= FtpItemAttributes.StickyBit;
					}
				}
			}

			return ret;
		} // func InternalConvertFromString

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="culture"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
				return ConvertFromInt(Convert.ToInt32(value));
			else if (value is string t)
				return InternalConvertFromString(t);
			else
				return base.ConvertFrom(context, culture, value);
		} //  func ConvertFrom

		/// <summary></summary>
		/// <param name="context"></param>
		/// <param name="sourceType"></param>
		/// <returns></returns>
		public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
			=> base.CanConvertFrom(context, sourceType);

		#endregion
	} // func class FtpItemAtributesConverter

	#endregion

	#region -- enum FtpItemAttributes -------------------------------------------------

	/// <summary>Enumeration for the Ftp/Unix-rights.</summary>
	[Flags, TypeConverter(typeof(FtpItemAtributesConverter))]
	public enum FtpItemAttributes
	{
		/// <summary>Es handelt sich um ein Verzeichnis.</summary>
		Directory = 0x1000,  // 010000
		/// <summary>Das Programm wird so gestartet, als ob es der Besitzer der Datei starten würde.</summary>
		SetUidMode = 0x800, // 04000
		/// <summary>Das Programm wird so gestartet, als ob ein Gruppenmitglied die Datei aufrufen würde.</summary>
		SetGidMode = 0x400, // 02000
		/// <summary>Alle Dateien, die in das Verzeichnis geschrieben werden, gehören automatisch der Gruppe, der auch das Verzeichnis gehört</summary>
		GroupOwner = 0x400, // 02000
		/// <summary>Nur der Besitzer der Dateien kann diese löschen, auch wenn andere Benutzer Schreibrechte auf das Verzeichnis haben.</summary>
		StickyBit = 0x200, // 01000
		/// <summary>Leserecht für den Besitzer</summary>
		ReadOwner = 0x100, //  0400
		/// <summary>Schreibrecht für den Besitzer</summary>
		WriteOwner = 0x080, //  0200
		/// <summary>Ausführungsrecht für den Besitzer</summary>
		ExecOwner = 0x040, //  0100
		/// <summary>Leserecht für die Gruppe</summary>
		ReadGroup = 0x020, //  0040
		/// <summary>Schreibrecht für die Gruppe</summary>
		WriteGroup = 0x010, //  0020
		/// <summary>Ausführungsrecht für die Gruppe</summary>
		ExecGroup = 0x008, //  0010
		/// <summary>Leserecht für alle anderen</summary>
		ReadOther = 0x004, //  0004
		/// <summary>Schreibrecht für alle anderen</summary>
		WriteOther = 0x002, //  0002
		/// <summary>Ausführungsrecht für alle anderen</summary>
		ExecOther = 0x001, //  0001
		/// <summary>Keine Rechte für Niemanden</summary>
		None = 0
	} // enum FtpItemAttributes

	#endregion

	#region -- class FtpItem ----------------------------------------------------------

	/// <summary>Base item, for ftp items.</summary>
	public abstract class FtpItem
	{
		private readonly FtpClient client;
		private readonly string relativePath;
		private readonly string name;
		private readonly DateTime lastWrite;
		private readonly FtpItemAttributes attributes;

		/// <summary></summary>
		/// <param name="client"></param>
		/// <param name="path"></param>
		/// <param name="name"></param>
		/// <param name="lastWrite"></param>
		/// <param name="attributes"></param>
		protected FtpItem(FtpClient client, string path, string name, DateTime lastWrite, FtpItemAttributes attributes)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));

			var p = name.LastIndexOf('/');
			this.name = p == -1 ? name : name.Substring(p + 1);
			if (path != null)
				relativePath = path + '/' + name;
			else
				relativePath = name;

			this.lastWrite = lastWrite;
			this.attributes = attributes;
		} // ctor

		/// <summary></summary>
		/// <returns></returns>
		public override string ToString()
			=> name;

		/// <summary>Delete the item.</summary>
		/// <returns></returns>
		public bool Delete()
			=> client.Delete(relativePath);

		/// <summary>Access the ftp client.</summary>
		public FtpClient Client => client;
		/// <summary>Name of the file.</summary>
		public string Name => name;
		/// <summary>Relative path to the root (includes name).</summary>
		public string RelativePath => relativePath;
		/// <summary></summary>
		public DateTime LastWriteTime => lastWrite;
		/// <summary></summary>
		public FtpItemAttributes Attributes => attributes;

		/// <summary>Full uri</summary>
		public Uri FullUri => client.CreateFullUri(relativePath);
	} // class FtpItem

	#endregion

	#region -- class FtpDirectory -----------------------------------------------------

	/// <summary></summary>
	public sealed class FtpDirectory : FtpItem
	{
		internal FtpDirectory(FtpClient client, string path, string name, DateTime lastWrite, FtpItemAttributes attributes)
			: base(client, path, name, lastWrite, attributes)
		{
		} // ctor

		/// <summary>Delete the ftp-directory</summary>
		/// <param name="recursive">Remove all items in the directory.</param>
		public bool Delete(bool recursive)
			=> Client.Delete(RelativePath, recursive);

		/// <summary>Get child items.</summary>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <returns></returns>
		public IEnumerable<FtpItem> EnumerateItems(string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly)
			=> Client.Enumerate<FtpItem>(RelativePath, searchPattern, searchOption);

		/// <summary>Get files.</summary>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <returns></returns>
		public IEnumerable<FtpFile> EnumerateFiles(string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly)
			=> Client.Enumerate<FtpFile>(RelativePath, searchPattern, searchOption);

		/// <summary>Get sub directories.</summary>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <returns></returns>
		public IEnumerable<FtpDirectory> EnumerateDirectories(string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly)
			=> Client.Enumerate<FtpDirectory>(RelativePath, searchPattern, searchOption);
	} // class FtpDirectory

	#endregion

	#region -- class FtpFile ----------------------------------------------------------

	/// <summary>Ftp file representaition.</summary>
	public sealed class FtpFile : FtpItem
	{
		private readonly long size;

		internal FtpFile(FtpClient client, string path, string name, long size, DateTime lastWrite, FtpItemAttributes attributes)
			: base(client, path, name, lastWrite, attributes)
		{
			this.size = size;
		} // ctor

		/// <summary>Open a file to read content.</summary>
		/// <returns></returns>
		public Stream Download()
			=> Client.Download(RelativePath);

		/// <summary></summary>
		/// <returns></returns>
		public byte[] DownloadData()
			=> Client.DownloadData(RelativePath);

		/// <summary></summary>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public string DownloadString(Encoding encoding = null)
			=> Client.DownloadString(RelativePath, encoding);

		/// <summary></summary>
		public string Extension => Path.GetExtension(Name);
		/// <summary>Size of the ftp file.</summary>
		public long Size => size;
	} // class FtpFile

	#endregion

	#region -- class FtpClient --------------------------------------------------------

	/// <summary>Ftp client based on FtpWebRequest</summary>
	public sealed class FtpClient
	{
		private static readonly Regex ftpDateRegex = new Regex(@"\d{4}(-\d{2}){2}", RegexOptions.Compiled);
		private static readonly Regex reqTime = new Regex(@"\d{2}(:\d{2})+", RegexOptions.Compiled);

		private readonly Uri baseUri;
		private readonly bool enableSsl;

		private ICredentials credentials = null;

		#region -- Ctor/Dtor ----------------------------------------------------------

		/// <summary>Create a new ftp client from uri.</summary>
		/// <param name="uri"></param>
		public FtpClient(Uri uri)
		{
			var isFtps = uri.Scheme == "ftps";
			if (!isFtps && uri.Scheme != "ftp")
				throw new ArgumentException("ftp|ftps expected.");

			// basic information
			baseUri = new Uri("ftp://" + uri.Host + (uri.Port > 0  ? ":" + uri.Port.ToString() : "") + uri.AbsolutePath);
			enableSsl = isFtps;

			// unpack credentials
			credentials = CreateCredentials(uri.UserInfo);
		} // ctor

		private static ICredentials CreateCredentials(string userInfo)
		{
			if (String.IsNullOrEmpty(userInfo))
				return null;
			else
			{
				var p = userInfo.IndexOf(':');
				if (p == -1)
					return new NetworkCredential(userInfo, String.Empty);
				else
					return new NetworkCredential(WebUtility.UrlDecode(userInfo.Substring(0, p)), WebUtility.UrlDecode(userInfo.Substring(p + 1)));
			}
		} // func CreateCredentials
		
		/// <summary></summary>
		/// <param name="userName"></param>
		/// <param name="password"></param>
		public void SetLogin(string userName, string password)
			=> credentials = new NetworkCredential(userName, password);

		#endregion

		#region -- Primitives ---------------------------------------------------------

		internal Uri CreateFullUri(string path)
			=> String.IsNullOrEmpty(path) ? baseUri : new Uri(baseUri, path);

		private FtpWebRequest CreateRequest(string method, string path)
		{
			var ftp = (FtpWebRequest)WebRequest.Create(CreateFullUri(path));

			// todo: gefährlich, globale server einstellung definieren
			if (enableSsl)
				ServicePointManager.ServerCertificateValidationCallback = checkServerCertificate;

			ftp.Method = method;
			ftp.EnableSsl = enableSsl;
			if (credentials != null)
				ftp.Credentials = credentials;
			return ftp;
		} // func CreateRequest

		private FtpWebResponse OpenRequest(string method, string path)
			=> (FtpWebResponse)CreateRequest(method, path).GetResponse();

		private bool checkServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
			=> sslPolicyErrors == SslPolicyErrors.None || CheckServerCertificate(certificate);

		private bool CheckServerCertificate(X509Certificate certificate)
		{
			var subject = certificate.Subject;
			return subject.Contains("CN=*.de-nserver.de,");
		} // func CheckServerCertificate

		#endregion

		#region -- List Directory -----------------------------------------------------

		// Liest den nächsten Block mit Daten
		private static string ParseListColumn(string line, ref int pos)
		{
			// Ignore trailing spaces
			while (pos < line.Length && line[pos] == ' ')
				pos++;

			// Collect content
			var start = pos;
			while (pos < line.Length && line[pos] != ' ')
				pos++;

			return line.Substring(start, pos - start).Trim();
		} // func ParseListColumn

		private static int FillFtpYear(int day, int month, ref DateTime now)
			=> month > now.Month || (month == now.Month && day > now.Day) ? now.Year - 1 : now.Year;

		private static DateTime ParseFtpDateTime(string line, ref int pos)
		{
			DateTime lastWrite;
			var firstPart = ParseListColumn(line, ref pos);

			if (ftpDateRegex.IsMatch(firstPart)) // Date/Time 
			{
				var timePart = ParseListColumn(line, ref pos);
				if (!DateTime.TryParse(firstPart + " " + timePart, out lastWrite))
					lastWrite = DateTime.MinValue;
			}
			else // special unix format
			{
				var now = DateTime.Now;

				// First is the month
				var month = Array.FindIndex(DateTimeFormatInfo.InvariantInfo.AbbreviatedMonthNames, cur => String.Compare(cur, firstPart, StringComparison.OrdinalIgnoreCase) == 0) + 1;

				// Then the day
				if (!Int32.TryParse(ParseListColumn(line, ref pos), out var day))
					day = DateTime.Now.Day;

				// Time
				firstPart = ParseListColumn(line, ref pos);

				// Create the lastwrite
				int year;
				if (reqTime.IsMatch(firstPart))
				{
					year = FillFtpYear(day, month, ref now);

					if (DateTime.TryParse(firstPart, out lastWrite))
						lastWrite = new DateTime(year, month, day, lastWrite.Hour, lastWrite.Minute, lastWrite.Second, lastWrite.Millisecond);
					else
						lastWrite = new DateTime(year, month, day);
				}
				else
				{
					if (!Int32.TryParse(firstPart, out year))
						year = FillFtpYear(day, month, ref now);
					lastWrite = new DateTime(year, month, day);
				}
			}

			return lastWrite;
		} // func ParseFtpDateTime

		/// <summary></summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="path"></param>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public IEnumerable<T> Enumerate<T>(string path = null, string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly, Encoding encoding = null)
			where T : FtpItem
		{
			var subFolders = new Stack<string>();
			subFolders.Push(path);

			var filterExpr = Procs.GetFilerFunction(searchPattern, true);
			var attributeConverter = new FtpItemAtributesConverter();

			var isDirectoriesAllowed = typeof(T).IsAssignableFrom(typeof(FtpDirectory));
			var isFilesAllowed = typeof(T).IsAssignableFrom(typeof(FtpFile));

			while (subFolders.Count > 0)
			{
				var currentPath = subFolders.Pop();

				using (var response = OpenRequest(WebRequestMethods.Ftp.ListDirectoryDetails, currentPath))
				using (var sr = new StreamReader(response.GetResponseStream(), encoding ?? Encoding.UTF8))
				{
					int pos;
					string line;
					while ((line = sr.ReadLine()) != null)
					{
						line = line.Trim();

						// Rights (fix Länge 10 Zeichen)
						var attributes = (FtpItemAttributes)attributeConverter.ConvertFromString(line.Substring(0, 10));

						pos = 10;
						ParseListColumn(line, ref pos); // Number of sub directories
						var user = ParseListColumn(line, ref pos);
						var group = ParseListColumn(line, ref pos);

						// Size
						if (!Int64.TryParse(ParseListColumn(line, ref pos), out var size))
							size = -1;

						// Date z.B. yyyy-mm-dd hh:mm  oder mmm dd {yyyy | hh:mm}
						var lastWrite = ParseFtpDateTime(line, ref pos);

						// Filename
						var name = line.Substring(pos).Trim();
						if (name == "." || name == "..")
							continue;

						// return result
						if ((attributes & FtpItemAttributes.Directory) != 0)
						{
							if (isDirectoriesAllowed && filterExpr(name))
								yield return (T)(object)new FtpDirectory(this, currentPath, name, lastWrite, attributes);

							if (searchOption == SearchOption.AllDirectories)
								subFolders.Push(String.IsNullOrEmpty(currentPath) ? name : currentPath + '/' + name);
						}
						else if (isFilesAllowed && filterExpr(name))
							yield return (T)(object)new FtpFile(this, currentPath, name, size, lastWrite, attributes);
					}
				}
			}
		} // proc Enumerate

		/// <summary></summary>
		/// <param name="path"></param>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public FtpItem[] List(string path = null, string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly, Encoding encoding = null)
			=> Enumerate<FtpItem>(path, searchPattern, searchOption, encoding).ToArray();

		/// <summary></summary>
		/// <param name="path"></param>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public FtpFile[] ListFiles(string path = null, string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly, Encoding encoding = null)
			=> Enumerate<FtpFile>(path, searchPattern, searchOption, encoding).ToArray();

		/// <summary></summary>
		/// <param name="path"></param>
		/// <param name="searchPattern"></param>
		/// <param name="searchOption"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public FtpDirectory[] ListDirectories(string path = null, string searchPattern = null, SearchOption searchOption = SearchOption.TopDirectoryOnly, Encoding encoding = null)
			=> Enumerate<FtpDirectory>(path, searchPattern, searchOption, encoding).ToArray();

		#endregion

		#region -- Exists -------------------------------------------------------------

		/// <summary>Prüft, ob sich die Datei im aktuellen Verzeichnis befindet</summary>
		/// <param name="path">Datei die gesucht wird.</param>
		/// <returns><c>true</c>, wenn die Datei auf dem Server vorhanden ist.</returns>
		public bool FileExists(string path)
		{
			try
			{
				using (var response = OpenRequest(WebRequestMethods.Ftp.GetDateTimestamp, path))
					response.Close();
				return true;
			}
			catch (WebException e)
			{
				var code = ((FtpWebResponse)e.Response).StatusCode;
				if (code == FtpStatusCode.ActionNotTakenFileUnavailableOrBusy || code == FtpStatusCode.ActionNotTakenFileUnavailable)
					return false;
				else
					throw;
			}
		} // func FileExists

		/// <summary>Prüft, ob sich das Verzeichnis im aktuellen Verzeichnis befindet</summary>
		/// <param name="path">Verzeichnis das gesucht wird.</param>
		/// <returns><c>true</c>, wenn das Verzeichnis auf dem Server vorhanden ist.</returns>
		public bool DirectoryExists(string path)
		{
			try
			{
				using (var response = OpenRequest(WebRequestMethods.Ftp.PrintWorkingDirectory, path))
					response.Close();
				return true;
			}
			catch (WebException e)
			{
				var code = ((FtpWebResponse)e.Response).StatusCode;
				if (code == FtpStatusCode.ActionNotTakenFileUnavailableOrBusy || code == FtpStatusCode.ActionNotTakenFileUnavailable)
					return false;
				else
					throw;
			}
		} // func DirectoryExists

		#endregion

		#region -- Download, Upload ---------------------------------------------------

		/// <summary></summary>
		/// <param name="path"></param>
		/// <returns></returns>
		public Stream Download(string path)
		{
			throw new NotImplementedException();
		} // func Download

		/// <summary></summary>
		/// <param name="path"></param>
		/// <returns></returns>
		public byte[] DownloadData(string path)
		{
			using (var response = OpenRequest(WebRequestMethods.Ftp.DownloadFile, path))
			using (var src = response.GetResponseStream())
				return src.ReadInArray();
		} // proc Download

		/// <summary></summary>
		/// <param name="path"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public string DownloadString(string path, Encoding encoding = null)
		{
			using (var response = OpenRequest(WebRequestMethods.Ftp.DownloadFile, path))
			using (var src = response.GetResponseStream())
			using (var tr = new StreamReader(src, encoding ?? Encoding.UTF8, true))
				return tr.ReadToEnd();
		} // func DownloadString

		/// <summary>Lädt eine Datei auf den Server.</summary>
		/// <param name="path">ID der Datei</param>
		/// <param name="proc">Platzhalter</param>
		public void Upload(string path, Action<Stream> proc)
		{
			var request = CreateRequest(WebRequestMethods.Ftp.UploadFile, path);
			using (var dst = request.GetRequestStream())
			{
				proc(dst);
				dst.Close();
			}

			using (var r = request.GetResponse())
				r.Close();
		} // proc Upload

		#endregion

		#region -- Delete -------------------------------------------------------------

		private bool InternalDeleteItem(bool isFile, string path)
		{
			try
			{
				using(var response = OpenRequest(isFile ? WebRequestMethods.Ftp.DeleteFile : WebRequestMethods.Ftp.RemoveDirectory, path))
				{
					var isOk = response.StatusCode == FtpStatusCode.FileActionOK;
					response.Close();
					return isOk;
				}
			}
			catch (WebException e)
			{
				if (((FtpWebResponse)e.Response).StatusCode == FtpStatusCode.ActionNotTakenFilenameNotAllowed)
					return false;
				else
					throw;
			}
		} // func InternalDeleteItem
		/// <summary></summary>
		/// <param name="path"></param>
		/// <param name="recursive"></param>
		/// <returns></returns>
		public bool Delete(string path, bool recursive = false)
		{
			if (FileExists(path))
				return InternalDeleteItem(true, path);
			else if (DirectoryExists(path))
				return InternalDeleteItem(false, path);
			else
				return false;
		} // proc Delete

		#endregion

		#region -- MakeDirectory ------------------------------------------------------

		private bool InternalMakeDirectory(string path)
		{
			try
			{
				using (var response = OpenRequest(WebRequestMethods.Ftp.MakeDirectory, path))
				{

					var isOk = response.StatusCode == FtpStatusCode.PathnameCreated;
					response.Close();
					return isOk;
				}
			}
			catch (WebException e)
			{
				var code = ((FtpWebResponse)e.Response).StatusCode;
				if (code == FtpStatusCode.ActionNotTakenFileUnavailableOrBusy || code == FtpStatusCode.ActionNotTakenFileUnavailable)
					return false;
				else
					throw;
			}
		} // func InternalMakeDirectory

		/// <summary>Erstellt ein Verzeichnis auf dem Remote-Server, auch über mehrere neue Ebenen.</summary>
		/// <param name="path">Das zu erstellende Verzeichnis</param>
		public void MakeDirectory(string path)
		{
			if (DirectoryExists(path))
				return;

			var directoryParts = path.Split('/');

			// Gehe, vom obersteren zum untersten Verzeichnis durch und versuche sie anzulegen
			var j = directoryParts.Length;
			while (!InternalMakeDirectory(String.Join("/", directoryParts, 0, j)) && j > 0)
				j--;

			// Lege die Verzeichnisse von untern nach oben an
			j++;
			while (j <= directoryParts.Length)
			{
				var directory = String.Join("/", directoryParts, 0, j);
				if (InternalMakeDirectory(directory))
					j++;
				else
					throw new IOException(String.Format("Can not create remote directory '{0}'.", directory));
			}
		} // proc MakeDirectory

		#endregion

		#region -- Rename -------------------------------------------------------------

		/// <summary></summary>
		/// <param name="oldPath"></param>
		/// <param name="newPath"></param>
		public void RenameFile(string oldPath, string newPath)
		{
			var request = CreateRequest(WebRequestMethods.Ftp.Rename, oldPath);
			request.RenameTo = newPath;
			using (var response = request.GetResponse())
				response.Close();
		} // proc RenameFile

		#endregion

		/// <summary>Use credentials</summary>
		public bool HasCredentials => credentials != null;
		/// <summary></summary>
		public bool IsSsl => enableSsl;
	} // class DEFtpClient

	#endregion
}
