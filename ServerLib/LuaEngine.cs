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
	#region -- interface IDELuaEngine -------------------------------------------------

	/// <summary>Script engine of the data exchange server.</summary>
	public interface IDELuaEngine : IServiceProvider
	{
		/// <summary>Attach a global script to an environment/configuration item.</summary>
		/// <param name="scriptId">Id of the global script</param>
		/// <param name="table">Environment to attach.</param>
		/// <param name="autoRun">Run the script automaticly after it was changed.</param>
		/// <returns>Script attachment</returns>
		ILuaAttachedScript AttachScript(string scriptId, LuaTable table, bool autoRun = false);
		/// <summary>Create a private script.</summary>
		/// <param name="code">Code of the script</param>
		/// <param name="scriptBase">Name of the script.</param>
		/// <param name="parameters">Script arguments.</param>
		/// <returns>Created script.</returns>
		ILuaScript CreateScript(Func<TextReader> code, string scriptBase, params KeyValuePair<string, Type>[] parameters);

		/// <summary>Create a private script.</summary>
		/// <param name="code">Code of the script</param>
		/// <param name="scriptBase">Name of the script.</param>
		/// <param name="parameters">Script arguments.</param>
		/// <returns>Created script.</returns>
		ILuaScript CreateScript(ILuaLexer code, string scriptBase, params KeyValuePair<string, Type>[] parameters);

		/// <summary>Access to the internal Lua-Script-Engine.</summary>
		Lua Lua { get; }
		/// <summary>Is debugging active.</summary>
		bool IsDebugAllowed { get; }
	} // interface IDELuaEngine

	#endregion

	#region -- interface ILuaAttachedScript -------------------------------------------

	/// <summary>Connection between an defined script and an execution environment.</summary>
	public interface ILuaAttachedScript : IDisposable
	{
		/// <summary>Raised, when script was changed. With cancel stop the compile process.</summary>
		event CancelEventHandler ScriptChanged;
		/// <summary>Raised, after the script was compiled and executed successful.</summary>
		event EventHandler ScriptCompiled;

		/// <summary>Run the script on the attached environment.</summary>
		/// <param name="throwExceptions">Throw exceptions.</param>
		void Run(bool throwExceptions = false);

		/// <summary>Run the script automaticly after it was changed.</summary>
		bool AutoRun { get; set; }

		/// <summary>Id of the script.</summary>
		string ScriptId { get; }
		/// <summary>Is the script ready to run.</summary>
		bool IsCompiled { get; }
		/// <summary>Is the script changed since the last run..</summary>
		bool NeedToRun { get; }
	} // interface ILuaAttachedScript

	#endregion

	#region -- interface ILuaScript ---------------------------------------------------

	/// <summary>Script that is defined within the service.</summary>
	public interface ILuaScript : IDisposable
	{
		/// <summary>Executes the script.</summary>
		/// <param name="table">Environment to execute on.</param>
		/// <param name="throwExceptions">Throw exceptions.</param>
		/// <param name="arguments">Arguments of the script.</param>
		/// <returns></returns>
		LuaResult Run(LuaTable table, bool throwExceptions, params object[] arguments);

		/// <summary>Compiled chunk of the script.</summary>
		LuaChunk Chunk { get; }
		/// <summary>Return the full file name of the script (if there is one).</summary>
		string ScriptBase { get; }
	} // interface ILuaScript

	#endregion

	#region -- class LuaStackTraceFrame -----------------------------------------------

	/// <summary>Stack trace item</summary>
	public sealed class LuaStackTraceItem
	{
		/// <summary>Id of the script.</summary>
		public string ScriptId;
		/// <summary>Current code line.</summary>
		public int Line;
	} // class TraceStackTraceItem

	#endregion

	#region -- class TraceStackTrace --------------------------------------------------

	/// <summary>Stacktrace for the script position.</summary>
	public sealed class TraceStackTrace
	{

	} // class TraceStackTrace

	#endregion
}
