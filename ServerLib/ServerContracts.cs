using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TecWare.DE.Server
{
	#region -- interface IDEAuthentificatedUser -----------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Authentifizierte Nutzer in dem alle Informationen für die aktuelle 
	/// Abfrage gespeichert werden können. Zusätzliche Dienste können via
	/// GetService erfragt werden.</summary>
	public interface IDEAuthentificatedUser : IServiceProvider, IPrincipal, IDisposable
	{
	} // interface IDEAuthentificatedUser

	#endregion

	#region -- interface IDEUser --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Definiert die Darstellung des Nutzers, der im Dienst registriert wird.</summary>
	public interface IDEUser
	{
		/// <summary>Erzeugt einen authentifizierten Nutzer.</summary>
		/// <param name="identity">Übergibt die originale Identität der Anmeldung mit dessen Hilfe die Security-Tokens geprüft werden können.</param>
		/// <returns>Context für diesen Nutzer.</returns>
		IDEAuthentificatedUser Authentificate(IIdentity identity);

		/// <summary>Name des Nutzers</summary>
		string Name { get; }
	} // interface IDEUser

	#endregion

	#region -- interface IDEBaseLog -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEBaseLog
	{
		/// <summary>Gesamtanzahl der Log-Dateien.</summary>
		int TotalLogCount { get; set; }
	} // interface IDEBaseLog

	#endregion

	#region -- interface IDEServerQueue -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Der Service bietet einen Hintergrund-Thread für die Bearbeitung
	/// nicht Zeitkritischer aufgaben an.</summary>
	public interface IDEServerQueue
	{
		/// <summary>Registriert eine Procedure (ohne Parameter), die nur dann
		/// ausgeführt wird, wenn der Hintergrund-Thread keine sonstigen Aufgaben
		/// hat. Die minimale Zeitspanne zwischen zwei aufeinander Folgenden
		/// aufrufen Beträgt 1s.</summary>
		/// <param name="action">Procedure z.B. vom Type Action.</param>
		void RegisterIdle(Action action);
		/// <summary>Entfernt den Idle-Command.</summary>
		/// <param name="action"></param>
		void UnregisterIdle(Action action);

		/// <summary>Legt eine Aufgabe zur Verarbeitung in die Warteschlange.
		/// Die Aufgabe wird so schnell wie möglich ausgeführt.</summary>
		/// <param name="action"></param>
		void EnqueueCommand(Action action);
		/// <summary>Fügt eine Action in die Schlange ein, die nach einem bestimmten Zeitpunkt ausgeführt werden soll</summary>
		/// <param name="action">Action die asugeführt werden soll.</param>
		/// <param name="iWait">Millisekunden, die mindestens gewartet werden sollen.</param>
		void EnqueueCommand(Action action, int iWait);
		/// <summary>Bricht eine eingesteuerte Action in der Warteschlange ab.</summary>
		/// <param name="proc"></param>
		void CancelCommand(Action proc);

		/// <summary>Gibt zurück, ob der Hintergrund-Thread läuft und Aufgaben
		/// entgegen nimmt. Sollte im Ablauf <c>true</c> sein. Kritisch ist
		/// die Shutdown-Phase, in der keine Aufgaben mehr in den Hg-Thread 
		/// eingegliedert werden dürfen.</summary>
		bool IsQueueRunning { get; }
		/// <summary>Ist die aktuelle Thread-Id ungleich der Hg-Thread-Id, dann
		/// gibt diese Eigenschaft <c>true</c> zurück.</summary>
		bool IsQueueRequired { get; }
	} // interface IDEServerQueue
	#endregion

	#region -- interface IDEServer ------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Gibt Zugriff auf den Service.</summary>
	public interface IDEServer : IDEConfigItem
	{
		/// <summary>Schreibt in das Systemlog</summary>
		/// <param name="type">Typ</param>
		/// <param name="message">Nachricht</param>
		/// <param name="id"></param>
		/// <param name="category"></param>
		/// <param name="rawData"></param>
		void LogMsg(EventLogEntryType type, string message, int id = 0, short category = 0, byte[] rawData = null);
		/// <summary>Meldet einen Fehler in das Systemlog.</summary>
		/// <param name="e"></param>
		void LogMsg(Exception e);

		/// <summary>Versucht einen Knoten über die Extension zu laden.</summary>
		/// <param name="config">In diesen Knoten soll der neue Knoten eingefügt werden</param>
		/// <param name="element">Konfigurationselement, welches geladen werden soll.</param>
		/// <param name="currentNamespace">Aktueller Namespace. Kann null sein, damit nicht auf missing Extensions geprüft wird.</param>
		/// <returns>Wurde etwas geladen.</returns>
		bool LoadConfigExtension(IDEConfigLoading config, XElement element, string currentNamespace);

		/// <summary></summary>
		/// <param name="item"></param>
		/// <param name="event"></param>
		/// <param name="index"></param>
		/// <param name="values"></param>
		void AppendNewEvent(DEConfigItem item, string @event, string index, XElement values);

		/// <summary>Registriert einen Nutzer innerhalb des HttpServers</summary>
		/// <param name="user">Nutzer</param>
		void RegisterUser(IDEUser user);
		/// <summary>Entfernt den Nutzer aus dem HttpServer.</summary>
		/// <param name="user"></param>
		void UnregisterUser(IDEUser user);
		/// <summary>Ermittelt das Server-Principal für den angegebenen Nutzer.</summary>
		/// <param name="user"></param>
		/// <returns></returns>
		IDEAuthentificatedUser AuthentificateUser(IIdentity user);
		/// <summary>Erzeugt aus der Token-Zeichenfolge eine Tokenliste.</summary>
		/// <param name="securityTokens">Token-Zeichenfolge</param>
		/// <returns>Security-Token-Array</returns>
		string[] BuildSecurityTokens(string securityTokens);

		/// <summary>Gibt das Verzeichnis für die Loginformationen zurück.</summary>
		string LogPath { get; }
		/// <summary>Basiskonfigurationsdatei, die geladen wurde.</summary>

		/// <summary>Version der SecurityTokens</summary>
		int SecurityGroupsVersion { get; }
	} // interface IDEServer

	#endregion

	#region -- DEServerBaseLog ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Ermöglicht den Zugriff auf die Basis-Logdatei</summary>
	public class DEServerBaseLog { }

	#endregion
}
