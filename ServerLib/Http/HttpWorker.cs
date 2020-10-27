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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server.Http
{
	#region -- interface IHttpWorker --------------------------------------------------

	/// <summary>Do http-requests for a sub node.</summary>
	public interface IHttpWorker : IDEConfigItem
	{
		/// <summary>Do sub request.</summary>
		/// <param name="r"></param>
		/// <returns></returns>
		Task<bool> RequestAsync(IDEWebRequestScope r);

		/// <summary>Defines the root within the page, this root is relative to the parent node.</summary>
		string VirtualRoot { get; }
		/// <summary>Order the http-worker should be called.</summary>
		int Priority { get; }
	} // interface IHttpWorker

	#endregion

	#region -- class HttpWorker -------------------------------------------------------

	/// <summary>Verarbeitet eine Http-Anfrage</summary>
	public abstract class HttpWorker : DEConfigItem, IHttpWorker
	{
		private string virtualBase;
		private int priority = 100;

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="sName"></param>
		protected HttpWorker(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="config"></param>
		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			var cfg = ConfigNode;

			virtualBase = cfg.GetAttribute<string>("base") ?? String.Empty;
			if (virtualBase.StartsWith("/"))
				virtualBase = virtualBase.Substring(1);
			priority = cfg.GetAttribute<int>("priority");
		} // proc OnEndReadConfiguration

		private bool TestFilter(XElement x, string subPath)
		{
			var value = x.GetAttribute<string>("filter", null);
			return value == null || value.Length == 0 || value == "*" || Procs.IsFilterEqual(subPath, value);
		} // func TestFilter

		/// <summary>Get the content type for the file name.</summary>
		/// <param name="fileName">Filename</param>
		/// <returns><c>null</c>, if no content type for this file type was defined. Use Http to get default.</returns>
		public string GetFileContentType(string fileName)
			=> Config.Elements(xnMimeDef).Where(x => TestFilter(x, Path.GetFileName(fileName))).Select(x => x.Value).FirstOrDefault();

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="subPath"></param>
		protected void DemandFile(IDEWebRequestScope r, string subPath)
		{
			var tokens = Config.Elements(xnSecurityDef).Where(x => TestFilter(x, subPath)).Select(x => x.Value?.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault();
			if (tokens == null || tokens.Length == 0)
				return;

			var a = false;
			foreach (var c in tokens)
			{
				if (a |= r.TryDemandToken(c))
					break;
			}
			if (!a)
				throw r.CreateAuthorizationException($"Access denied: {subPath}");
		} // func DemandFile

		/// <summary>Does the specific address.</summary>
		/// <param name="r">Request context.</param>
		/// <returns><c>true</c>, if the request is processed.</returns>
		public abstract Task<bool> RequestAsync(IDEWebRequestScope r);
		
		/// <summary>Gibt die Wurzel innerhalb der virtuellen Seite, ab der diese Http-Worker gerufen werden soll.</summary>
		public virtual string VirtualRoot => virtualBase;
		/// <summary>Reihenfolge in der die Http-Worker abgerufen werden</summary>
		public virtual int Priority => priority;
	} // class HttpWorker

	#endregion

	#region -- class HttpFileWorker ---------------------------------------------------

	/// <summary>Liefert Dateien aus</summary>
	public class HttpFileWorker : HttpWorker
	{
		private DirectoryInfo directoryBase;
		private bool allowListing = false;
		private string filterPattern = "*";
		private Encoding defaultReadEncoding = Encoding.UTF8;

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		public HttpFileWorker(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="config"></param>
		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			var cfg = ConfigNode;

			directoryBase = cfg.GetAttribute<DirectoryInfo>("directory");
			allowListing = cfg.GetAttribute<bool>("allowListing");
			filterPattern = cfg.GetAttribute<string>("filter");
			defaultReadEncoding = cfg.GetAttribute<Encoding>("encoding");
		} // proc OnEndReadConfiguration

		/// <summary>Return all fiels from the base directory</summary>
		/// <returns></returns>
		[LuaMember]
		public IEnumerable<FileInfo> ListFiles(string relativePath = null)
		{
			var directory = new DirectoryInfo(Path.GetFullPath(Path.Combine(directoryBase.FullName, ProcsDE.GetLocalPath(relativePath))));
			if (directory.Exists)
				return directory.EnumerateFiles(filterPattern, SearchOption.TopDirectoryOnly);
			else
				return Array.Empty<FileInfo>();
		} // func ListFiles
		
		/// <summary>Return the item name</summary>
		/// <returns></returns>
		[LuaMember]
		public string GetListName()
			=> Name;

		/// <summary></summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public override async Task<bool> RequestAsync(IDEWebRequestScope r)
		{
			if (String.IsNullOrEmpty(filterPattern))
				return false;

			// create the full file name
			var useIndex = false;
			var fullDirectoryName = this.directoryBase.FullName;
			var fileName = Path.GetFullPath(Path.Combine(fullDirectoryName, ProcsDE.GetLocalPath(r.RelativeSubPath)));
			var directoryBaseOffset = fullDirectoryName[fullDirectoryName.Length-1] == Path.DirectorySeparatorChar ? fullDirectoryName.Length - 1 : fullDirectoryName.Length;

			// Check for a directory escape
			if (!fileName.StartsWith(directoryBase.FullName, StringComparison.OrdinalIgnoreCase) 
				|| (fileName.Length > directoryBaseOffset && fileName[directoryBaseOffset] != Path.DirectorySeparatorChar))
				return false;

			// is the filename a directory, add index.html
			if (Directory.Exists(fileName))
			{
				if (!String.IsNullOrEmpty(r.AbsolutePath) && r.AbsolutePath[r.AbsolutePath.Length - 1] != '/')
				{
					r.Redirect(r.AbsolutePath + '/');
					return true;
				}
				else
				{
					fileName = Path.Combine(fileName, ConfigNode.GetAttribute<string>("indexPage"));
					useIndex = true;
				}
			}
			else if (filterPattern != "*") // check for filter pattern
			{
				if (!Procs.IsFilterEqual(Path.GetFileName(fileName), filterPattern))
					return false; // pattern does not fit
			}

			if (File.Exists(fileName))
			{
				// security
				DemandFile(r, fileName);
				// Send the file
				await Task.Run(() => r.WriteFile(fileName, GetFileContentType(fileName), defaultReadEncoding));
				return true;
			}
			else if (useIndex && allowListing) // write index table
			{
				await Task.Run(() => r.WriteResource(typeof(HttpWorker), "Resources.Listing.html", "text/html"));
				return true;
			}
			else
				return false;
		} // func Request

		/// <summary>Base directory</summary>
		public DirectoryInfo DirectoryBase => directoryBase;
		/// <summary></summary>
		public override string Icon => "/images/http.file16.png";
	} // class HttpFileWorker

	#endregion

	#region -- class HttpResourceWorker -----------------------------------------------

	/// <summary>Liefert Dateien aus</summary>
	public class HttpResourceWorker : HttpWorker
	{
		private Assembly assembly = null;
		private string namespaceRoot = String.Empty;
		private DateTime assemblyStamp = DateTime.MinValue;
		private string[] nonePresentAlternativeExtensions = null;
		private string[] alternativeRoots = null;

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		public HttpResourceWorker(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		/// <summary></summary>
		/// <param name="config"></param>
		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// load assembly
			var assemblyName = config.ConfigNew.GetAttribute("assembly", String.Empty);
			if (String.IsNullOrEmpty(assemblyName))
				Log.LogMsg(LogMsgType.Error, "Assembly name missing.");
			else
			{
				try
				{
					assembly = Assembly.Load(assemblyName);
					assemblyStamp = File.GetLastWriteTimeUtc(assembly.Location);
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, String.Format("Assembly not loaded: {0}", assemblyName), e);
				}

			}

			// Load namespace
			namespaceRoot = config.ConfigNew.GetAttribute("namespace", String.Empty);
			if (String.IsNullOrEmpty(namespaceRoot))
			{
				namespaceRoot = null;
				Log.LogMsg(LogMsgType.Error, "No namespace configured.");
			}
			else
				namespaceRoot += ".";

			// load alternative
			nonePresentAlternativeExtensions = config.ConfigNew.GetStrings("nonePresentAlternativeExtensions", true);

			alternativeRoots = config.ConfigNew.Elements(xnAlternativeRoot).Where(x => !String.IsNullOrEmpty(x.Value)).Select(x => x.Value).ToArray();
			if (alternativeRoots != null && alternativeRoots.Length == 0)
				alternativeRoots = null;
		} // proc OnBeginReadConfiguration

		/// <summary></summary>
		/// <param name="r"></param>
		/// <returns></returns>
		public override async Task<bool> RequestAsync(IDEWebRequestScope r)
		{
			if (assembly == null || namespaceRoot == null)
				return false;

			// create the resource name
			var resourceName = namespaceRoot + r.RelativeSubPath.Replace('/', '.');

			var src = (Stream)null;
			try
			{
				DateTime stamp;

				// try to open the resource stream
				var forceAlternativeCheck = nonePresentAlternativeExtensions != null && nonePresentAlternativeExtensions.FirstOrDefault(c => resourceName.EndsWith(c, StringComparison.OrdinalIgnoreCase)) != null;
				src = assembly.GetManifestResourceStream(resourceName);
				if (src == null && !forceAlternativeCheck) // nothing...
					return false;

				// check if there is a newer file
				if (alternativeRoots != null)
				{
					var relativeFileName = ProcsDE.GetLocalPath(r.RelativeSubPath);
					var alternativeFile = (from c in alternativeRoots
										   let fi = new FileInfo(Path.Combine(c, relativeFileName))
										   where fi.Exists && (forceAlternativeCheck || fi.LastWriteTimeUtc > assemblyStamp)
										   orderby fi.LastWriteTimeUtc descending
										   select fi).FirstOrDefault();

					if (alternativeFile != null)
					{
						src?.Close();
						src = alternativeFile.OpenRead();
						stamp = alternativeFile.LastWriteTimeUtc;
					}
					else
					{
						stamp = assemblyStamp;
						if (forceAlternativeCheck && src == null)
							return false;
					}
				}
				else
				{
					stamp = assemblyStamp;
					if (forceAlternativeCheck && src == null)
						return false;
				}

				// security
				DemandFile(r, r.RelativeSubPath);
				
				// send the file
				await Task.Run(() =>
					r.SetLastModified(stamp)
						.WriteContent(
							() => src,
							assembly.FullName.Replace(" ", "") + "\\[" + assemblyStamp.ToString("R") + "]\\" + resourceName ,
							GetFileContentType(resourceName) ?? r.Http.GetContentType(Path.GetExtension(resourceName))
						)
				);
				return true;
			}
			finally
			{
				Procs.FreeAndNil(ref src);
			}
		} // func Request

		/// <summary></summary>
		public override string Icon => "/images/http.res16.png";
	} // class HttpResourceWorker

	#endregion
}