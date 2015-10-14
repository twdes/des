using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- class LuaEngine ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Bündelt die Verwaltung und Ausführung von Scripts</summary>
	internal sealed class LuaEngine : DEConfigLogItem, IDELuaEngine
	{
		#region -- class LuaScript --------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Loaded script.</summary>
		internal abstract class LuaScript : IDisposable
		{
			private LuaEngine engine;
			private string scriptId;
			private LuaChunk compiled;
			private LuaCompileOptions debug;

			#region -- Ctor/Dtor ------------------------------------------------------------

			protected LuaScript(LuaEngine engine, string sScriptId, bool forceDebugMode)
			{
				this.engine = engine;
				this.scriptId = sScriptId;
				this.compiled = null;
				this.Debug = forceDebugMode;
			} // ctor

			~LuaScript()
			{
				Dispose(false);
			} // dtor

			public void Dispose()
			{
				GC.SuppressFinalize(this);
				Dispose(true);
			} // proc Dispose

			protected virtual void Dispose(bool disposing)
			{
				if (disposing)
				{
					foreach (var g in engine.GetAttachedGlobals(scriptId))
						g.ResetScript();

					// Entferne das Script
					engine.RemoveScript(this);

					Procs.FreeAndNil(ref compiled);
				}
			} // proc Dispose

			#endregion

			#region -- Compile --------------------------------------------------------------

			protected virtual void Compile(Func<TextReader> open, string sName, KeyValuePair<string, Type>[] args)
			{
				lock (this)
				{
					Procs.FreeAndNil(ref compiled);

					using (var tr = open())
						compiled = Lua.CompileChunk(tr, sName, debug, args);
				}
			} // proc Compile

			#endregion

			public void LogMsg(LogMsgType type, string sMessage)
			{
				engine.Log.LogMsg(type, "[{0}] {1}", ScriptId, sMessage);
			} // proc LogMsg

			public LuaEngine Engine => engine;

			public string ScriptId => scriptId;
			public abstract string ScriptBase { get; }

			public bool Debug { get { return debug != null; } set { debug = value ? LuaDeskop.StackTraceCompileOptions : null; } }
			public LuaChunk Chunk { get { lock (this) return compiled; } }
			public virtual Lua Lua => engine.Lua;
		} // class LuaScript

		#endregion

		#region -- class LuaFileScript ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Script auf der Basis einer Datei.</summary>
		private sealed class LuaFileScript : LuaScript
		{
			private FileInfo fileSource;    // Quelle des Scripts
			private Encoding encoding;      // Welche Encodierung hat das Script
			private DateTime compiledStamp; // Zeitstempel, wenn das Script zuletzt kompiliert wurde

			public LuaFileScript(LuaEngine engine, string scriptId, FileInfo fileSource, Encoding encoding, bool forceDebugMode)
				: base(engine, scriptId, forceDebugMode)
			{
				this.fileSource = fileSource;
				this.encoding = encoding ?? Encoding.Default;
				this.compiledStamp = DateTime.MinValue;

				Compile();
			} // ctor

			protected override void Compile(Func<TextReader> open, string name, KeyValuePair<string, Type>[] args)
			{
				// Erzeuge das Script neu
				try
				{
					base.Compile(open, name, args);
				}
				catch (Exception e)
				{
					LogMsg(LogMsgType.Error, e.GetMessageString());
				}

				// Führe das Script erneut auf den Globals aus
				foreach (var c in Engine.GetAttachedGlobals(ScriptId))
					c.OnScriptChanged();
			} // proc Compile

			public void Compile()
			{
				Compile(() => new StreamReader(fileSource.FullName, encoding), fileSource.FullName, null);
			} // proc Compile

			public override string ScriptBase => fileSource.FullName;
			public Encoding Encoding { get { return encoding; } set { encoding = value; } }
		} // class LuaFileScript

		#endregion

		#region -- class LuaMemoryScript --------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Script, welches nicht auf einer Basis auf der Datei erzeugt wurde.</summary>
		private sealed class LuaMemoryScript : LuaScript, ILuaScript
		{
			private Lua lua = null;
			private string name;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public LuaMemoryScript(LuaEngine engine, Func<TextReader> code, string name, bool forceDebugMode, KeyValuePair<string, Type>[] args)
				: base(engine, Guid.NewGuid().ToString("D"), forceDebugMode)
			{
				this.name = name;

				if (forceDebugMode)
					lua = new Lua();
				try
				{
					Compile(code, name, args);
				}
				catch
				{
					if (lua != null)
						lua.Dispose();
					throw;
				}
			} // ctor

			protected override void Dispose(bool disposing)
			{
				try
				{

					base.Dispose(disposing);
				}
				finally
				{
					if (disposing)
						Procs.FreeAndNil(ref lua);
				}
			} // proc Dispose

			#endregion

			public LuaResult Run(LuaTable table, params object[] args)
			{
				return Chunk.Run(table, args);
			} // func Run

			public override string ScriptBase => name;
			public override Lua Lua => lua ?? base.Lua;
		} // class LuaMemoryScript

		#endregion

		#region -- class LuaAttachedGlobal ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Verbindung zwischen den Skripten</summary>
		internal sealed class LuaAttachedGlobal : ILuaAttachedScript
		{
			public event CancelEventHandler ScriptChanged;
			public event EventHandler ScriptCompiled;

			private LuaEngine engine;
			private string scriptId;
			private LuaTable table;
			private bool autoRun;
			private bool needToRun;

			private object scriptLock = new object();
			private LuaScript currentScript = null;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public LuaAttachedGlobal(LuaEngine engine, string scriptId, LuaTable table, bool autoRun)
			{
				this.engine = engine;
				this.scriptId = scriptId;
				this.table = table;
				this.autoRun = autoRun;
				this.needToRun = autoRun;

				// Ausführen des Skripts
				if (needToRun && IsCompiled)
					Run(false);
			} // ctor

			public void Dispose()
			{
				engine.RemoveAttachedGlobal(this);
				currentScript = null;
			} // proc Dispose

			#endregion

			public void ResetScript()
			{
				lock (scriptLock)
					currentScript = null;
			} // proc ResetScript

			public void Run(bool throwExceptions)
			{
				lock (scriptLock)
					try
					{
						if (GetScript(throwExceptions) == null)
						{
							LogMsg(LogMsgType.Information, "Skript nicht gefunden.");
							return;
						}

						LogMsg(LogMsgType.Information, "Ausgeführt.");
						currentScript.Chunk.Run(table);
						needToRun = false;
						OnScriptCompiled();
					}
					catch (Exception e)
					{
						if (throwExceptions)
							throw;
						LogMsg(LogMsgType.Error, e.GetMessageString());
					}
			} // proc Run

			private LuaScript GetScript(bool throwExceptions)
			{
				if (currentScript == null)
				{
					currentScript = engine.FindScript(scriptId);
					if (currentScript == null && throwExceptions)
						throw new ArgumentException(String.Format("[{0}]: Skript nicht gefunden.", scriptId));
				}
				return currentScript;
			} // func GetScript

			public void OnScriptChanged()
			{
				var e = new CancelEventArgs(!autoRun);
				if (ScriptChanged != null)
					ScriptChanged(this, e);

				if (e.Cancel || !IsCompiled)
				{
					needToRun = true;
					LogMsg(LogMsgType.Information, "Wartet auf ausführung.");
				}
				else
					Run(false);
			} // proc OnScriptChanged

			public void OnScriptCompiled()
			{
				if (ScriptCompiled != null)
					ScriptCompiled(this, EventArgs.Empty);
			} // proc OnScriptCompiled

			private void LogMsg(LogMsgType type, string sMessage)
			{
				// Log auf Global anzeigen
				dynamic host = table.GetMemberValue("host");
				if (host != null)
					host.Log.LogMsg(type, "[Script: {0}] {1}", scriptId, sMessage);

				// Log auf Script anzeigen
				if (currentScript != null)
					currentScript.LogMsg(type, sMessage);
			} // func LogMsg

			public string ScriptId => scriptId;
			public LuaTable LuaTable => table;

			public bool IsCompiled
			{
				get
				{
					LuaScript s = GetScript(false);
					return s == null ? false : s.Chunk != null;
				}
			} // prop IsCompiled

			public bool NeedToRun => needToRun;
			public bool AutoRun { get { return autoRun; } set { autoRun = value; } }
		} // class LuaAttachedGlobal

		#endregion

		private SimpleConfigItemProperty<int> propertyScriptCount = null;
		private readonly Lua lua = new Lua();                                              // Globale Scripting Engine
		private readonly List<LuaScript> scripts = new List<LuaScript>();                  // Liste aller geladene Scripts
		private readonly List<LuaAttachedGlobal> globals = new List<LuaAttachedGlobal>();  // Alle Verbindungen

		#region -- Ctor/Dtor/Configuration ------------------------------------------------

		public LuaEngine(IServiceProvider sp, string name)
			: base(sp, name)
		{
			// Füge die States hinzu
			propertyScriptCount = new SimpleConfigItemProperty<int>(this, "tw_luaengine_scripts", "Skripte", "Lua-Engine", "Anzahl der aktiven Scripte.", "{0:N0}", 0);

			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDELuaEngine), this);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					Procs.FreeAndNil(ref propertyScriptCount);

					var sc = this.GetService<IServiceContainer>(true);
					sc.RemoveService(typeof(IDELuaEngine));
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// Lade die Scripte
			var i = 0;
			var scriptRemove = (from s in scripts where s is LuaFileScript select (LuaFileScript)s).ToArray();
			var luaScriptDefinition = Server.Configuration[xnLuaScript];
      foreach (var cur in config.ConfigNew.Elements(xnLuaScript))
			{
				try
				{
					var configNode = new XConfigNode(luaScriptDefinition, cur);
					LoadScript(configNode, scriptRemove);
				}
				catch (Exception e)
				{
					Log.LogMsg(LogMsgType.Error, String.Format("script[{0}]: {1}", i, e.GetMessageString()));
				}
				i++;
			}

			// Lösche die Scripte
			for (i = 0; i < scriptRemove.Length; i++)
			{
				if (scriptRemove[i] != null)
				{
					try { scriptRemove[i].Dispose(); }
					catch (Exception e) { Log.LogMsg(LogMsgType.Error, e.Message); }
				}
			}
		} // proc OnBeginReadConfiguration

		private void LoadScript(XConfigNode cur, LuaFileScript[] scriptRemove)
		{
			// Id des Scripts
			var scriptId = cur.GetAttribute<string>("id");
			if (String.IsNullOrEmpty(scriptId))
				throw new ArgumentNullException("@id", "Id nicht gefunden.");

			// Lese den Dateinamen
			string sFileName = cur.GetAttribute<string>("filename");
			if (String.IsNullOrEmpty(sFileName))
				throw new ArgumentNullException("@filename", "Dateiname nicht gefunden.");

			// Lese die Parameter der Scriptdatei
			var forceDebugMode = cur.GetAttribute<bool>("debug");
			var encoding = cur.GetAttribute<Encoding>("encoding");

			LuaScript script = FindScript(scriptId);
			LuaFileScript fileScript = script as LuaFileScript;

			if (fileScript == null) // script noch nicht vorhanden --> also legen wir es mal an
			{
				if (script != null)
					throw new ArgumentException(String.Format("Script '{0}' schon vorhanden.", scriptId));
				FileInfo fi = new FileInfo(sFileName);
				if (!fi.Exists)
					throw new ArgumentException(String.Format("Datei '{0}' nicht gefunden.", fi.FullName));

				AddScript(new LuaFileScript(this, scriptId, fi, encoding, forceDebugMode));
			}
			else
			{
				fileScript.Encoding = encoding;
				fileScript.Debug = forceDebugMode;
				scriptRemove[Array.IndexOf(scriptRemove, fileScript)] = null;
				fileScript.LogMsg(LogMsgType.Information, "Aktualisiert.");
			}
		} // LoadScript

		#endregion

		#region -- Script Verwaltung ------------------------------------------------------

		private void AddScript(LuaScript script)
		{
			lock (scripts)
				scripts.Add(script);
			script.LogMsg(LogMsgType.Information, "Hinzugefügt.");
		} // proc AddScript

		private void RemoveScript(LuaScript script)
		{
			lock (scripts)
			{
				script.LogMsg(LogMsgType.Information, "Entfernt.");
				scripts.Remove(script);
			}
		} // proc RemoveScript

		private LuaScript FindScript(string scriptId)
		{
			lock (scripts)
				return scripts.Find(c => String.Compare(c.ScriptId, scriptId, true) == 0);
		} // func FindScript

		private IEnumerable<LuaAttachedGlobal> GetAttachedGlobals(string scriptId)
		{
			lock (globals)
				foreach (LuaAttachedGlobal c in globals)
					if (String.Compare(c.ScriptId, scriptId, true) == 0)
						yield return c;
		} // func GetAttachedGlobals

		private void RemoveAttachedGlobal(LuaAttachedGlobal item)
		{
			lock (globals)
				globals.Remove(item);
		} // func RemoveAttachedGlobal

		#endregion

		#region -- IDELuaEngine -----------------------------------------------------------

		public ILuaAttachedScript AttachScript(string scriptId, LuaTable table, bool autoRun)
		{
			using (EnterReadLock())
			{
				// Gibt es die Verbindung schon
				lock (globals)
				{
					foreach (var c in globals)
						if (String.Compare(c.ScriptId, scriptId, true) == 0 && c.LuaTable == table)
							return c;
				}

				// Lege die Verbindung an
				var ag = new LuaAttachedGlobal(this, scriptId, table, autoRun);
				lock (globals)
					globals.Add(ag);
				return ag;
			}
		} // func AttachScript

		public ILuaScript CreateScript(Func<TextReader> code, string name, bool forceDebugMode, params KeyValuePair<string, Type>[] parameters)
		{
			var s = new LuaMemoryScript(this, code, name, forceDebugMode, parameters);
			AddScript(s);
			return s;
		} // func CreateScript

		public Lua Lua => lua;

		#endregion

		public override string Icon { get { return "/images/lua16.png"; } }
	} // class LuaEngine

	#endregion
}
