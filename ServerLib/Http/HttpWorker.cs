using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Xml.Linq;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server.Http
{
	#region -- class HttpWorker ---------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Verarbeitet eine Http-Anfrage</summary>
	public abstract class HttpWorker : DEConfigItem
	{
		private string virtualBase;
		private int priority = 100;

		public HttpWorker(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
		} // ctor

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			virtualBase = Config.GetAttribute("base", String.Empty);
			if (virtualBase.StartsWith("/"))
				virtualBase = virtualBase.Substring(1);
			priority = Config.GetAttribute("priority", priority);
		} // proc OnEndReadConfiguration

		private bool TestFilter(XElement x, string subPath)
		{
			var value = x.GetAttribute<string>("filter", null);
			if (value == null || value.Length == 0 || value == "*")
				return true;
			else
				return ProcsDE.IsFilterEqual(subPath, value);
		} // func TestFilter

		protected string GetFileContentType(string subPath)
			=> Config.Elements(xnMimeDef).Where(x => TestFilter(x, subPath)).Select(x => x.Value).FirstOrDefault();

		protected void DemandFile(IDEContext r, string subPath)
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
				throw r.CreateAuthorizationException(subPath);
		} // func DemandFile

		/// <summary>Does the specific address.</summary>
		/// <param name="r">Request context.</param>
		/// <returns><c>true</c>, if the request is processed.</returns>
		public abstract bool Request(IDEContext r);

		protected override bool OnProcessRequest(IDEContext r)
		{
			if (base.OnProcessRequest(r))
				return true;
			return Request(r);
		} // func OnProcessRequest

		/// <summary>Gibt die Wurzel innerhalb der virtuellen Seite, ab der diese Http-Worker gerufen werden soll.</summary>
		public virtual string VirtualRoot => virtualBase;
		/// <summary>Reihenfolge in der die Http-Worker abgerufen werden</summary>
		public virtual int Priority => priority;
	} // class HttpWorker

	#endregion

	#region -- class HttpFileWorker -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Liefert Dateien aus</summary>
	public class HttpFileWorker : HttpWorker
	{
		private static readonly XName xnMimeDef = Configuration.DEConfigurationConstants.MainNamespace + "mimeDef";
		private string directoryBase;

		public HttpFileWorker(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
		} // ctor

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			directoryBase = Config.GetAttribute("directory", String.Empty);
		} // proc OnEndReadConfiguration

		public override bool Request(IDEContext r)
		{
			// create the full file name
			var fileName = Path.GetFullPath(Path.Combine(directoryBase, ProcsDE.GetLocalPath(r.RelativeSubPath)));

			// Check for a directory escape
			if (!fileName.StartsWith(directoryBase, StringComparison.OrdinalIgnoreCase))
				return false;

			// is the filename a directory, add index.html
			if (Directory.Exists(fileName))
				fileName = Path.Combine(fileName, "Index.html");

			if (File.Exists(fileName))
			{
				// security
				DemandFile(r, fileName);
				// Send the file
				r.WriteFile(fileName, GetFileContentType(fileName));
				return true;
			}
			else
				return false;
		} // func Request

		private static bool TestFilter(string sFileName, string sFilter)
		{
			return sFileName.EndsWith(sFileName, StringComparison.OrdinalIgnoreCase);
		} // func TestFilter

		public override string Icon { get { return "/images/http.file16.png"; } }
	} // class HttpFileWorker

	#endregion

	#region -- class HttpResourceWorker -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Liefert Dateien aus</summary>
	public class HttpResourceWorker : HttpWorker
	{
		private Assembly assembly = null;
		private string namespaceRoot = String.Empty;
		private DateTime assemblyStamp = DateTime.MinValue;
		private string[] nonePresentAlternativeExtensions = null;
		private string[] alternativeRoots = null;

		public HttpResourceWorker(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

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
				namespaceRoot = namespaceRoot + ".";

			// load alternative
			nonePresentAlternativeExtensions = config.ConfigNew.GetAttribute("nonePresentAlternativeExtensions", String.Empty).Split( new char[] { ' ' },StringSplitOptions.RemoveEmptyEntries);
			if (nonePresentAlternativeExtensions != null && nonePresentAlternativeExtensions.Length == 0)
				nonePresentAlternativeExtensions = null;

			alternativeRoots = config.ConfigNew.Elements(xnAlternativeRoot).Where(x => !String.IsNullOrEmpty(x.Value)).Select(x => x.Value).ToArray();
			if (alternativeRoots != null && alternativeRoots.Length == 0)
				alternativeRoots = null;
		} // proc OnBeginReadConfiguration

		public override bool Request(IDEContext r)
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
				DemandFile(r, resourceName);
				// send the file
				r.SetLastModified(stamp)
					.WriteStream(src, GetFileContentType(resourceName) ?? r.Server.GetContentType(Path.GetExtension(resourceName)));
				return true;
			}
			finally
			{
				Procs.FreeAndNil(ref src);
			}
		} // func Request

		public override string Icon { get { return "/images/http.res16.png"; } }
	} // class HttpResourceWorker

	#endregion
}