using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Xml.Linq;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Http
{
  #region -- class HttpRequestHelper --------------------------------------------------

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary>Enthält erweiterte Objekte für das erstellen von Http antworten.</summary>
  public static class HttpRequestHelper
  {
    public static void WriteXml(this HttpResponse r, XElement x)
    {
      r.WriteXml(x);
    } // proc WriteXml

    #region -- WriteSafeCall ----------------------------------------------------------

		public static void WriteSafeCall(this HttpResponse r, XElement x, string sSuccessMessage = null)
    {
      if (x == null)
        x = new XElement("return");

      x.SetAttributeValue("status", "ok");
      if (!String.IsNullOrEmpty(sSuccessMessage))
        x.SetAttributeValue("text", sSuccessMessage);

      WriteXml(r, x);
    } // proc WriteSafeCall

		public static void WriteSafeCall(this HttpResponse r, string sErrorMessage)
    {
      WriteXml(r,
        new XElement("return",
          new XAttribute("status", "error"),
          new XAttribute("text", sErrorMessage)
        )
      );
    } // proc WriteSafeCall

		public static void WriteSafeCall(this HttpResponse r, Exception e)
    {
      WriteSafeCall(r, e.Message);
    } // proc WriteSafeCall

    #endregion
  } // class HttpRequestHelper

  #endregion

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

      virtualBase = Config.GetAttribute("base", "/");
      priority = Config.GetAttribute("priority", priority);
    } // proc OnEndReadConfiguration

    /// <summary>Führt die Abfrage aus.</summary>
    /// <param name="r">Objekt, über das die Antwort gesendet werden soll.</param>
    /// <returns>Wurde Inhalt ausgeliefert.</returns>
    public abstract bool Request(HttpResponse r);

    protected override bool OnProcessRequest(HttpResponse r)
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

    public override bool Request(HttpResponse r)
    {
			// Erzeuge einen Dateinamen
			var relativePath = r.RelativePath.Replace('/', Path.DirectorySeparatorChar);
			var fileName = Path.GetFullPath(Path.Combine(directoryBase, relativePath));

			// Prüfe, das es keinen Ausbruch gibt
			if (!fileName.StartsWith(directoryBase, StringComparison.OrdinalIgnoreCase))
				return false;

			// Es gibt ein Verzeichnis
			if (Directory.Exists(fileName))
				fileName = Path.Combine(fileName, "Index.html");

			// Liefer den Datenstrom aus
			if (File.Exists(fileName))
			{
				r.WriteFile(fileName, r.GetParameter,
					(from x in Config.Elements(xnMimeDef)
					 where TestFilter(fileName, x.GetAttribute("filter", String.Empty))
					 select x.Value).FirstOrDefault()
				);
				return true;
			}
			else
				return false;
    } // func Request

		private static bool TestFilter(string sFileName, string sFilter)
		{
			return sFileName.EndsWith(sFileName, StringComparison.OrdinalIgnoreCase);
		} // func TestFilter

    public override string Icon { get { return "/images/http.file.png"; } }
  } // class HttpFileWorker

  #endregion

  #region -- class HttpResourceWorker -------------------------------------------------

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary>Liefert Dateien aus</summary>
  public class HttpResourceWorker : HttpWorker
  {
    private Assembly assembly = null;
    private string namespaceRoot = String.Empty;

    public HttpResourceWorker(IServiceProvider sp, string name)
      : base(sp, name)
    {
    } // ctor

    protected override void OnBeginReadConfiguration(IDEConfigLoading config)
    {
      base.OnBeginReadConfiguration(config);

      // Versuche das Assembly zu laden
      var assemblyName = config.ConfigNew.GetAttribute("assembly", String.Empty);
      if (!String.IsNullOrEmpty(assemblyName))
        assembly = Assembly.Load(assemblyName);

      // Lade den Namespace
			if (assembly == null)
				Log.LogMsg(LogMsgType.Error, "Assembly nicht geladen: '" + assemblyName + "'");
			else
			{
				namespaceRoot = config.ConfigNew.GetAttribute("namespace", String.Empty);
				if (!String.IsNullOrEmpty(namespaceRoot))
					namespaceRoot = namespaceRoot + ".";
			}
    } // proc OnBeginReadConfiguration

		public override bool Request(HttpResponse r)
		{
			if (assembly == null)
				return false;

			// Erzeuge den Resourcennamen
			var resourceName = namespaceRoot + r.RelativePath.Replace('/', '.');

			// Versuche die Resource zu öffnen
			Stream src = null;
			try
			{
				// Versuche die Resource zu laden
				var mri = assembly.GetManifestResourceInfo(resourceName);
				if (mri == null) // Versuche eine Index-Resource zu finden
				{
					string sTmp = resourceName + ".Index.html";
					mri = assembly.GetManifestResourceInfo(sTmp);
					if (mri != null)
						resourceName = sTmp;
				}

				// Es gibt nix zum Ausliefern
				if (mri == null)
					return false;

				// Liefere die Daten aus
				r.WriteResource(assembly, resourceName, r.GetParameter);
				return true;
			}
			finally
			{
				Procs.FreeAndNil(ref src);
			}
		} // func Request

    public override string Icon { get { return "/images/http.res.png"; } }
  } // class HttpResourceWorker

  #endregion

  #region -- class HttpResponseException ----------------------------------------------

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
  public class HttpResponseException : Exception
  {
    private HttpStatusCode code;

    /// <summary>Spezielle Exception die einen Http-Status-Code weitergeben kann.</summary>
    /// <param name="code">Http-Fehlercode</param>
    /// <param name="sMessage">Nachricht zu diesem Fehlercode</param>
    /// <param name="innerException">Optionale </param>
    public HttpResponseException(HttpStatusCode code, string sMessage, Exception innerException = null)
      : base(sMessage, innerException)
    {
      this.code = code;
    } // ctor

    /// <summary>Code der übermittelt werden soll.</summary>
    public HttpStatusCode Code { get { return code; } }
  } // class HttpResponseException

  #endregion
}