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
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;

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

	#region -- interface DEServerEvent --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public enum DEServerEvent
	{
		Shutdown,
		Reconfiguration
	} // DEServerEvent

	#endregion

	#region -- interface IDEServerQueue -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Der Service bietet einen Hintergrund-Thread für die Bearbeitung
	/// nicht Zeitkritischer aufgaben an.</summary>
	public interface IDEServerQueue
	{
		/// <summary>Registers a method, that will be processed during idle of the queuue thread.</summary>
		/// <param name="action">Action to run.</param>
		/// <param name="timebetween">Time between the calls, this time is not guaranteed. If the queue thread is under heavy presure it will take longer.</param>
		void RegisterIdle(Action action, int timebetween = 1000);
		/// <summary>Registers a method, that will be executed on an event of the server.</summary>
		/// <param name="action"></param>
		/// <param name="eventType"></param>
		void RegisterEvent(Action action, DEServerEvent eventType);
		/// <summary></summary>
		/// <param name="action"></param>
		/// <param name="timeEllapsed"></param>
		void RegisterCommand(Action action, int timeEllapsed = 0);
		/// <summary>Removes a command/idle/shutdown action.</summary>
		/// <param name="action"></param>
		void CancelCommand(Action action);

		/// <summary>Returns the factory for the queue thread. Every task gets executed in a single thread.</summary>
		TaskFactory Factory { get; }

		/// <summary>Get the state of the queue thread. <c>true</c>, means that task can be scheduled. In the 
		/// shutdown phase, no tasks can be added.</summary>
		bool IsQueueRunning { get; }
		/// <summary>Is the current thread Id equal to the queue thread id.</summary>
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
		IDEConfigurationService Configuration { get; }
		/// <summary></summary>
		IDEServerQueue Queue { get; }

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
