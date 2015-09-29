using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Neo.IronLua;

namespace TecWare.DE.Server
{
	#region -- interface IDELuaEngine ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Zugriff auf die LuaScript-Engine des DEServers</summary>
	public interface IDELuaEngine : IServiceProvider
	{
		/// <summary>Verbindet ein Script mit einer Lua-Instanz</summary>
		/// <param name="sScriptId">Id des Scripts</param>
		/// <param name="table">Table, mit dem das Skript verbunden wird.</param>
		/// <param name="lAutoRun">Automatisch ausführen, wenn es geändert wurde</param>
		/// <returns>Verbindung-Script zu Global</returns>
		ILuaAttachedScript AttachScript(string sScriptId, LuaTable table, bool lAutoRun = false);
		/// <summary>Erzeugt ein privates Script.</summary>
		/// <param name="code">Scriptcode</param>
		/// <param name="sName">Name des Scripts</param>
		/// <param name="lDebug">Sollen Debug-Informationen erzeugt werden.</param>
		/// <param name="args">Argumente für das Script</param>
		/// <returns>Erzeugte Script</returns>
		ILuaScript CreateScript(Func<TextReader> code, string sName, bool lDebug, params KeyValuePair<string, Type>[] args);

		/// <summary>Zugriff auf die interne Lua-Script-Engine.</summary>
		Lua Lua { get; }
	} // interface IDELuaEngine

	#endregion

	#region -- interface ILuaAttachedScript ---------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Verbindung zwischen eine Script-Global und einem Script.</summary>
	public interface ILuaAttachedScript : IDisposable
	{
		/// <summary>Wird ausgelöst, wenn das Script geändert wird. Cancel kann die den Compilierungsvorgang abbrechen.</summary>
		event CancelEventHandler ScriptChanged;
		/// <summary>Wird ausgelöst, wenn das Script kompiliert und ausgeführt wurde.</summary>
		event EventHandler ScriptCompiled;

		/// <summary>Führt das Script aus.</summary>
		/// <param name="lThrowExceptions"></param>
		void Run(bool lThrowExceptions = false);

		/// <summary>Soll das Script automatisch nach einem Reload ausgeführt werden.</summary>
		bool AutoRun { get; set; }

		/// <summary>Id des verbundenen Scripts.</summary>
		string ScriptId { get; }
		/// <summary>Kann das Script ausgeführt werden.</summary>
		bool IsCompiled { get; }
		/// <summary>Wurde das Script seit dem letzten ausführen neu erstellt.</summary>
		bool NeedToRun { get; }
	} // interface ILuaAttachedScript

	#endregion

	#region -- interface ILuaScript -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Script, welches nur über einen gewissen Zeitraum existieren soll.</summary>
	public interface ILuaScript : IDisposable
	{
		/// <summary>Führt das Script aus.</summary>
		/// <param name="table"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		LuaResult Run(LuaTable table, params object[] args);
		/// <summary>Bei temporären Scripten, die mit Debug-Informationen erzeugt werden, wird ggf. eine
		/// neue Engine erzeugt. Damit, sie vollständig freigegeben werden.</summary>
		Lua Lua { get; }
		/// <summary>Gibt Zugriff auf den Chunk.</summary>
		LuaChunk Chunk { get; }
	} // interface ILuaScript

	#endregion
}
