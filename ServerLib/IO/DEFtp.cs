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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.IO
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
	public sealed class DEFtpItem
	{
		private readonly DEFtpClient client;
		private readonly string relativePath;
		private readonly string name;

		public DEFtpItem(DEFtpClient client, string name)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));

			var p = name.LastIndexOf('/');
			relativePath = name;
			this.name = p == -1 ? name : name.Substring(p + 1);
		} // ctor

		public override string ToString()
			=> name;

		public byte[] DownloadAll()
			=> client.DownloadAll(relativePath);

		public string Name => name;
		public string RelativePath => relativePath;
	} // class DEFtpItem

	public sealed class DEFtpClient
	{
		private readonly Uri baseUri;
		private readonly bool enableSsl;

		private ICredentials credentials = null;

		public DEFtpClient(Uri baseUri, bool enableSsl = false)
		{
			this.baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
			this.enableSsl = enableSsl;
		} // ctor

		private Uri CreateFullUri(string path)
			=> String.IsNullOrEmpty(path) ? baseUri : new Uri(baseUri, path);

		private FtpWebResponse OpenRequest(string method, string path)
		{
			var ftp = (FtpWebRequest)WebRequest.Create(CreateFullUri(path));

			// todo: gefährlich, globale server einstellung definieren
			if (enableSsl)
				ServicePointManager.ServerCertificateValidationCallback = checkServerCertificate;

			ftp.Method = method;
			ftp.EnableSsl = enableSsl;
			if (credentials != null)
				ftp.Credentials = credentials;

			return (FtpWebResponse)ftp.GetResponse();
		} // func OpenRequest

		private bool checkServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
			=> sslPolicyErrors == SslPolicyErrors.None || CheckServerCertificate(certificate);

		private bool CheckServerCertificate(X509Certificate certificate)
		{
			var subject = certificate.Subject;
			return subject.Contains("CN=*.de-nserver.de,");
		} // func CheckServerCertificate

		public DEFtpItem[] List(string path)
		{
			using (var response = OpenRequest(WebRequestMethods.Ftp.ListDirectory, path))
			using (var tr = new StreamReader(response.GetResponseStream()))
				return Procs.SplitNewLines(tr.ReadToEnd()).Select(c => new DEFtpItem(this, c)).ToArray();
		} // func List

		public byte[] DownloadAll(string path)
		{
			using (var response = OpenRequest(WebRequestMethods.Ftp.DownloadFile, path))
			using (var src = response.GetResponseStream())
				return src.ReadInArray();
		} // proc Download

		public void SetLogin(string userName, string password)
			=> credentials = new NetworkCredential(userName, password);

		public bool HasCredentials => credentials != null;
	} // class DEFtpClient
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
