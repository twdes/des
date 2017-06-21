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
		/// <param name="scriptId">Id des Scripts</param>
		/// <param name="table">Table, mit dem das Skript verbunden wird.</param>
		/// <param name="autoRun">Automatisch ausführen, wenn es geändert wurde</param>
		/// <returns>Verbindung-Script zu Global</returns>
		ILuaAttachedScript AttachScript(string scriptId, LuaTable table, bool autoRun = false);
		/// <summary>Erzeugt ein privates Script.</summary>
		/// <param name="code">Scriptcode</param>
		/// <param name="name">Name des Scripts</param>
		/// <param name="parameter">Argumente für das Script</param>
		/// <returns>Erzeugte Script</returns>
		ILuaScript CreateScript(Func<TextReader> code, string name, params KeyValuePair<string, Type>[] parameter);

		/// <summary>Access to the internal Lua-Script-Engine.</summary>
		Lua Lua { get; }
		/// <summary>Is debugging active.</summary>
		bool IsDebugAllowed { get; }
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
		/// <param name="throwExceptions"></param>
		void Run(bool throwExceptions = false);

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
		/// <param name="throwExceptions"></param>
		/// <param name="arguments"></param>
		/// <returns></returns>
		LuaResult Run(LuaTable table, bool throwExceptions, params object[] arguments);
		/// <summary>Gibt Zugriff auf den Chunk.</summary>
		LuaChunk Chunk { get; }
	} // interface ILuaScript

	#endregion

	#region -- class LuaStackTraceFrame -------------------------------------------------

	public sealed class LuaStackTraceItem
	{
		public string ScriptId;
		public ILuaScript Script;
		public int Line;
	} // class TraceStackTraceItem

	#endregion

	#region -- class TraceStackTrace -----------------------------------------------------

	public sealed class TraceStackTrace
	{
		
	} // class TraceStackTrace

	#endregion
}
