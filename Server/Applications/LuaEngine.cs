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
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- class LuaEngine ----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Service for Debugging and running lua scripts.</summary>
	internal sealed class LuaEngine : DEConfigLogItem, IDEWebSocketProtocol, IDELuaEngine
	{
		#region -- class LuaScript --------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Loaded script.</summary>
		internal abstract class LuaScript : IDisposable
		{
			private readonly LuaEngine engine;
			private readonly LoggerProxy log;
			private readonly string scriptId;

			private bool compiledWithDebugger;
			private readonly object chunkLock = new object();
			private LuaChunk chunk;

			#region -- Ctor/Dtor ------------------------------------------------------------

			protected LuaScript(LuaEngine engine, string scriptId, bool compileWithDebugger)
			{
				this.engine = engine;
				this.log = LoggerProxy.Create(engine.Log, scriptId);
				this.scriptId = scriptId;

				this.chunk = null;
				this.compiledWithDebugger = compileWithDebugger;

				// attach the script to the engine
				engine.AddScript(this);

				log.Info("Hinzugefügt.");
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

					// Remove the script from the engine
					engine.RemoveScript(this);
					log.Info("Entfernt.");

					Procs.FreeAndNil(ref chunk);
				}
			} // proc Dispose

			#endregion

			#region -- Compile --------------------------------------------------------------

			protected virtual void Compile(Func<TextReader> open, KeyValuePair<string, Type>[] args)
			{
				lock (chunkLock)
				{
					// clear the current chunk
					Procs.FreeAndNil(ref chunk);

					// recompile the script
					using (var tr = open())
						chunk = Lua.CompileChunk(tr, scriptId, compiledWithDebugger ? engine.debugOptions : null, args);
				}
			} // proc Compile

			public void LogLuaException(Exception e)
			{
				// unwind target exceptions
				if (e is TargetInvocationException)
				{
					if (e.InnerException != null)
					{
						LogLuaException(e.InnerException);
						return;
					}
				}

				// log exception
				var ep = e as LuaParseException;
				if (ep != null)
				{
					Log.Except("{0} ({3} at {1}, {2})", ep.Message, ep.Line, ep.Column, ep.FileName);
				}
				else
				{
					var er = e as LuaRuntimeException;
					if (er != null)
					{
						Log.Except(er);
					}
					else
						Log.Except("Compile failed.", e);
				}
			} // proc LogLuaException

			#endregion

			/// <summary>Engine</summary>
			public LuaEngine Engine => engine;
			/// <summary>Log for script related messages.</summary>
			public LoggerProxy Log => log;
			/// <summary>Access to the assigned Lua-Engine.</summary>
			public virtual Lua Lua => engine.Lua;

			/// <summary>Unique id of the script.</summary>
			public string ScriptId => scriptId;
			/// <summary>Filename of the scipt.</summary>
			public abstract string ScriptBase { get; }

			/// <summary>Is the script debugable.</summary>
			public bool Debug => compiledWithDebugger;

			/// <summary>Access to the chunk.</summary>
			public LuaChunk Chunk { get { lock (this) return chunk; } }
		} // class LuaScript

		#endregion

		#region -- class LuaFileScript ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Script that is based on file.</summary>
		private sealed class LuaFileScript : LuaScript
		{
			private FileInfo fileSource;    // File source of the script
			private Encoding encoding;      // Encoding style of the file
			private DateTime compiledStamp; // Last time compiled

			public LuaFileScript(LuaEngine engine, string scriptId, FileInfo fileSource, Encoding encoding, bool compileWithDebugger)
				: base(engine, scriptId, compileWithDebugger)
			{
				this.fileSource = fileSource;
				this.encoding = encoding ?? Encoding.Default;
				this.compiledStamp = DateTime.MinValue;

				// compile an add
				Compile();
			} // ctor

			protected override void Compile(Func<TextReader> open, KeyValuePair<string, Type>[] args)
			{
				// Re-create the script
				try
				{
					base.Compile(open, args);
				}
				catch (Exception e)
				{
					LogLuaException(e);
				}

				// Notify that the script is changed
				foreach (var c in Engine.GetAttachedGlobals(ScriptId))
				{
					try { c.OnScriptChanged(); }
					catch (Exception e) { Log.Except("Attach to failed.", e); }
				}
			} // proc Compile

			public void Compile()
			{
				Compile(() => new StreamReader(fileSource.FullName, encoding), null);
			} // proc Compile

			public void SetDebugMode(bool compileWithDebug)
			{
				// todo:
			} // proc SetDebugMode

			/// <summary>Name of the file</summary>
			public override string ScriptBase => fileSource.FullName;
			/// <summary>Encoding of the file.</summary>
			public Encoding Encoding { get { return encoding; } set { encoding = value; } }
		} // class LuaFileScript

		#endregion

		#region -- class LuaMemoryScript --------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>In memory script, that is not based on a file.</summary>
		private sealed class LuaMemoryScript : LuaScript, ILuaScript
		{
			private readonly string name;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public LuaMemoryScript(LuaEngine engine, Func<TextReader> code, string name, KeyValuePair<string, Type>[] args)
				: base(engine, Guid.NewGuid().ToString("D"), false)
			{
				this.name = name;

				try
				{
					Compile(code, args);
				}
				catch (Exception e)
				{
					LogLuaException(e);
				}
			} // ctor

			#endregion

			public LuaResult Run(LuaTable table, bool throwExceptions, params object[] args)
			{
				try
				{
					return Chunk.Run(table, args);
				}
				catch (Exception e)
				{
					LogLuaException(e);
					if (throwExceptions)
						throw;
					return LuaResult.Empty;
				}
			} // func Run

			public override string ScriptBase => name;
		} // class LuaMemoryScript

		#endregion

		#region -- class LuaAttachedGlobal ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Verbindung zwischen den Skripten</summary>
		internal sealed class LuaAttachedGlobal : ILuaAttachedScript
		{
			public event CancelEventHandler ScriptChanged;
			public event EventHandler ScriptCompiled;

			private readonly LuaEngine engine;
			private readonly LoggerProxy log;
			private readonly string scriptId;
			private LuaTable table;
			private bool autoRun;
			private bool needToRun;

			private object scriptLock = new object();
			private LuaScript currentScript = null;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public LuaAttachedGlobal(LuaEngine engine, string scriptId, LuaTable table, bool autoRun)
			{
				this.engine = engine;
				this.log = LoggerProxy.Create(GetHostLog(table) ?? engine.Log, scriptId);
				this.scriptId = scriptId;
				this.table = table;
				this.autoRun = autoRun;
				this.needToRun = autoRun;

				// Run the script
				if (needToRun && IsCompiled)
					Run(false);
			} // ctor

			public void Dispose()
			{
				engine.RemoveAttachedGlobal(this);
				currentScript = null;
			} // proc Dispose

			private static ILogger GetHostLog(LuaTable table)
			{
				// Check for a logger
				var sp = table.GetMemberValue("host") as IServiceProvider;
				if (sp != null)
					return sp.GetService<ILogger>(false);

				return null;
			} // func LogMsg

			#endregion

			#region -- Run, ResetScript -----------------------------------------------------

			public void ResetScript()
			{
				lock (scriptLock)
					currentScript = null;
			} // proc ResetScript

			public void Run(bool throwExceptions)
			{
				lock (scriptLock)
				using (var m = log.CreateScope(LogMsgType.Information, true, true))
						try
						{
							if (GetScript(throwExceptions) == null)
							{
								m.SetType(LogMsgType.Warning)
									.WriteLine("Script not found.");
								return;
							}

							currentScript.Chunk.Run(table);
							m.WriteLine("Executed.");

							needToRun = false;
							OnScriptCompiled();
						}
						catch (Exception e)
						{
							if (throwExceptions)
								throw;

							m.WriteException(e);
						}
			} // proc Run

			private LuaScript GetScript(bool throwExceptions)
			{
				if (currentScript == null)
				{
					currentScript = engine.FindScript(scriptId);
					if (currentScript == null && throwExceptions)
						throw new ArgumentException(String.Format("Skript '{0}' nicht gefunden.", scriptId));
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
					log.Info("Waiting for execution.");
				}
				else
					Run(false);
			} // proc OnScriptChanged

			public void OnScriptCompiled()
			{
				if (ScriptCompiled != null)
					ScriptCompiled(this, EventArgs.Empty);
			} // proc OnScriptCompiled

			#endregion

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

		#region -- class LuaEngineTraceLineDebugger ---------------------------------------

		private sealed class LuaEngineTraceLineDebugger : LuaTraceLineDebugger
		{
			#region -- class LuaTraceLineDebugInfo ------------------------------------------

			///////////////////////////////////////////////////////////////////////////////
			/// <summary></summary>
			private sealed class LuaTraceLineDebugInfo : ILuaDebugInfo
			{
				private readonly string chunkName;
				private readonly string sourceFile;
				private readonly int line;

				public LuaTraceLineDebugInfo(LuaTraceLineExceptionEventArgs e, LuaScript script)
				{
					this.chunkName = e.SourceName;
					this.sourceFile = script?.ScriptBase ?? chunkName;
					this.line = e.SourceLine;
				} // ctor

				public string ChunkName => chunkName;
				public int Column => 0;
				public string FileName => sourceFile;
				public int Line => line;
			} // class LuaTraceLineDebugInfo

			#endregion

			private readonly LuaEngine engine;

			public LuaEngineTraceLineDebugger(LuaEngine engine)
			{
				this.engine = engine;
			} // ctor

			protected override void OnExceptionUnwind(LuaTraceLineExceptionEventArgs e)
			{
				var luaFrames = new List<LuaStackFrame>();
				var offsetForRecalc = 0;
				LuaExceptionData currentData = null;

				// get default exception data
				if (e.Exception.Data[LuaRuntimeException.ExceptionDataKey] is LuaExceptionData)
				{
					currentData = LuaExceptionData.GetData(e.Exception);
					offsetForRecalc = currentData.Count;
					luaFrames.AddRange(currentData);
				}
				else
					currentData = LuaExceptionData.GetData(e.Exception, resolveStackTrace: false);

				// re-trace the stack frame
				var trace = new StackTrace(e.Exception, true);
				for (var i = offsetForRecalc; i < trace.FrameCount - 1; i++)
					luaFrames.Add(LuaExceptionData.GetStackFrame(trace.GetFrame(i)));

				// add trace point
				luaFrames.Add(new LuaStackFrame(trace.GetFrame(trace.FrameCount - 1), new LuaTraceLineDebugInfo(e, engine.FindScript(e.SourceName))));

				currentData.UpdateStackTrace(luaFrames.ToArray());
			} // proc OnExceptionUnwind

			protected override void OnFrameEnter(LuaTraceLineEventArgs e)
			{
			} // proc OnFrameEnter

			protected override void OnTracePoint(LuaTraceLineEventArgs e)
			{
			} // proc OnTracePoint

			protected override void OnFrameExit()
			{
			} // proc OnFrameExit
		} // class LuaEngineTraceLineDebugger

		#endregion
		
		#region -- class LuaDebugSession --------------------------------------------------

		/// <summary>Debug session and scope for the commands.</summary>
		private sealed class LuaDebugSession : LuaTable, IDEDebugContext, IDisposable
		{
			private readonly LuaEngine engine; // lua engine
			private readonly LoggerProxy log; // log for the debugging

			private readonly IDEWebSocketScope context; // context of the web-socket, will will not set this context (own context management)
			private DECommonScope currentScope = null; // current context for the code execution

			private readonly CancellationToken cancellationToken;
			private DEConfigItem currentItem = null; // current node, that is used as parent
			
			public LuaDebugSession(LuaEngine engine, IDEWebSocketScope context, CancellationToken cancellationToken)
			{
				this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
				this.log = LoggerProxy.Create(engine.Log, "Debug Session");
				this.context = context ?? throw new ArgumentNullException(nameof(context));

				// initial context
				this.cancellationToken = cancellationToken;
				currentScope = CreateCommonScopeAsync(context.User, null).AwaitTask();
				
				UseNode("/");

				Info("Debug session established.");
			} // ctor

			public void Dispose()
			{
				Info("Debug session closed.");
				
				// Dispose the execution scope
				currentScope?.Dispose();
			} // proc Dispose

			public async Task ExecuteAsync()
			{
				// runs the initialization code
				var initFunc = engine.DebugEnvironment.GetValue("InitSession", true);
				if (initFunc != null)
				{
					await Task.Run(() =>
					{
						using (currentScope.Use())
							Lua.RtInvoke(initFunc, this);
					});
				}

				// start message loop
				var recvOffset = 0;
				var recvBuffer = new byte[1 << 20]; // 1MB buffer
				try
				{
					while (Socket.State == WebSocketState.Open)
					{
						var recvRest = recvBuffer.Length - recvOffset;

						// buffer is too small, close connection
						if (recvRest == 0)
							break;

						// get bytes
						var r = await Socket.ReceiveAsync(new ArraySegment<byte>(recvBuffer, recvOffset, recvRest), cancellationToken);
						if (r.MessageType == WebSocketMessageType.Text) // get only text messages
						{
							recvOffset += r.Count;
							if (r.EndOfMessage) // eom
							{
								// first decode message
								try
								{
									var xMessage = XElement.Parse(Encoding.UTF8.GetString(recvBuffer, 0, recvOffset));
									try
									{
										await ProcessMessage(xMessage);
									}
									catch (Exception e)
									{
										var token = xMessage.GetAttribute("token", 0);
										if (token > 0) // answer requested
											await SendAnswerAsync(xMessage, CreateException(new XElement("exception", new XAttribute("token", token)), e));
										else
											throw; // fall to next
									}
								}
								catch (Exception e)
								{
									log.Except(e);
								}

								recvOffset = 0;
							}
						}
					}
				}
				catch (WebSocketException e)
				{
					if (e.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
						log.Except("Debug session closed.", e);
				}
				catch (Exception e)
				{
					log.Except("Debug session failed.", e);
				}
			} // proc Execute

			private XElement CreateException(XElement x, Exception e)
			{
				if (e is AggregateException aggE)
				{
					var enumerator = aggE.InnerExceptions.GetEnumerator();
					if (enumerator.MoveNext())
					{
						CreateException(x, enumerator.Current);
						while (enumerator.MoveNext())
							x.Add(CreateException(new XElement("innerException"), enumerator.Current));
					}
					else
						x.Add(new XAttribute("message", e.Message));
				}
				else
				{
					x.Add(new XAttribute("message", e.Message));
					x.Add(new XAttribute("type", LuaType.GetType(e.GetType()).AliasOrFullName));
					var data = LuaExceptionData.GetData(e);
					x.Add(new XElement("stackTrace", data == null ? e.StackTrace : data.StackTrace));

					if (e.InnerException != null)
						x.Add(CreateException(new XElement("innerException"), e.InnerException));
				}

				return x;
			} // proc CreateException

			private XElement CreateMember(object member, object value, Type type = null)
			{
				var x = new XElement("v",
					member is int ? new XAttribute("i", member) : new XAttribute("n", member.ToString()),
					new XAttribute("t", LuaType.GetType(type ?? (value != null ? value.GetType() : typeof(object))).AliasOrFullName)
				);

				if (value != null)
					x.Add(new XText(Procs.ChangeType<string>(value)));

				return x;
			} // func CreateMember

			private async Task ProcessMessage(XElement x)
			{
				Debug.Print("[Server] Receive Message: {0}", x.GetAttribute("token", 0));

				var command = x.Name;

				if (command == "execute")
					await SendAnswerAsync(x, await ExecuteAsync(x));
				else if (command == "use")
					await SendAnswerAsync(x, UseNode(x));
				else if (command == "member")
					await SendAnswerAsync(x, GetMember(x));
				else if (command == "list")
					await SendAnswerAsync(x, GetNodeList(x));
				else if (command == "scopeBegin")
					await SendAnswerAsync(x, await BeginScopeAsync(x));
				else if (command == "scopeRollback")
					await SendAnswerAsync(x, await RollbackScopeAsync(x));
				else if (command == "scopeCommit")
					await SendAnswerAsync(x, await CommitScopeAsync(x));
				else // always, answer commands with a token
					throw new ArgumentException($"Unknown command '{command}'.");
			} // proc ProcessMessage

			public async Task SendAnswerAsync(XElement xMessage, XElement xAnswer)
			{
				// update token
				if (xMessage != null && xAnswer.Attribute("token") == null)
				{
					var token = xMessage.GetAttribute("token", 0);
					if (token > 0)
						xAnswer.Add(new XAttribute("token", token));
				}

				Debug.Print("[Server] Send Message: {0}", xAnswer.GetAttribute("token", 0));

				// encode and send
				var buf = Encoding.UTF8.GetBytes(xAnswer.ToString(SaveOptions.None));
				await Socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, cancellationToken);
			} // proc SendAnswerAsync

			private void Notify(XElement xNotify)
			{
				Debug.Print("[Server] Send Notify: {0} => {1}", xNotify.Name, xNotify.ToString());

				// encode and send
				var buf = Encoding.UTF8.GetBytes(xNotify.ToString(SaveOptions.None));
				Socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, cancellationToken)
					.ContinueWith(
						t => log.Except(t.Exception),
						TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.RunContinuationsAsynchronously
					);
			} // proc otify

			private void Info(string message)
				=> log.Info(message);

			protected override object OnIndex(object key)
				=> base.OnIndex(key) ?? 
					engine.DebugEnvironment.GetValue(key, true) ??  
					currentItem.GetValue(key);

			void IDEDebugContext.OnMessage(LogMsgType type, string message)
				=> Notify(new XElement("log", new XAttribute("type", type.ToString()[0]), new XText(message)));

			#region -- Execute --------------------------------------------------------------

			private async Task<XElement> ExecuteAsync(XElement xMessage)
			{
				var r = await Task.Run(
					() =>
					{
						// compile the chunk
						var chunk = engine.Lua.CompileChunk(xMessage.Value, "remote.lua", null);
						// run the chunk on the node
						using (currentScope?.Use())
							return chunk.Run(this);
					}
				);

				// return the result
				var xAnswer = new XElement("return");
				for (var i = 0; i < r.Count; i++)
					xAnswer.Add(CreateMember(i, r[i]));

				return xAnswer;
			} // func Execute

			#endregion

			#region -- GlobalVars -----------------------------------------------------------

			private XElement GetMember(XElement xMessage)
			{
				var xAnswer = new XElement("return");

				foreach (var c in currentItem)
					xAnswer.Add(CreateMember(c.Key, c.Value));

				return xAnswer;
			} // func GlobalVars

			#endregion

			#region -- UseNode --------------------------------------------------------------

			private XElement UseNode(XElement xMessage)
			{
				var p = xMessage.GetAttribute("node", String.Empty);
				if (!String.IsNullOrEmpty(p))
					UseNode(p);
				return new XElement("return", new XAttribute("node", currentItem.ConfigPath));
			} // proc UseNode 

			private void UseNode(string path)
			{
				if (!path.StartsWith("/"))
					throw new ArgumentException("Invalid path format.");

				UseNode((DEConfigItem)engine.Server, path, 1);
			} // proc UseItem

			private void UseNode(DEConfigItem current, string path, int offset)
			{
				if (offset >= path.Length)
				{
					currentItem = current;
					return;
				}
				else
				{
					var pos = path.IndexOf('/', offset);
					if (pos == offset)
						throw new ArgumentException("Invalid path format.");
					if (pos == -1)
						pos = path.Length;

					if (pos - offset == 0) // end
						this.currentItem = current;
					else // find node
					{
						var currentName = path.Substring(offset, pos - offset);
						var newCurrent = current.UnsafeFind(currentName);
						if (newCurrent == null)
							throw new ArgumentException("Invalid path.");

						UseNode(newCurrent, path, pos + 1);
					}
				}
			} // proc UseNode

			#endregion

			#region -- List -----------------------------------------------------------------

			private IEnumerable<XElement> GetNodeList(DEConfigItem current, bool recusive)
			{
				using (currentItem.EnterReadLock())
				{
					foreach (var c in current.UnsafeChildren)
					{
						var x = new XElement("n",
							new XAttribute("name", c.Name),
							new XAttribute("displayName", c.DisplayName)
						);

						if (recusive)
							x.Add(GetNodeList(c, true));

						yield return x;
					}
				}
			} // func GetNodeList

			private XElement GetNodeList(XElement xMessage)
			{
				return new XElement("return",
					GetNodeList(currentItem, xMessage.GetAttribute("r", false))
				);
			} // func GetNodeList

			#endregion

			#region -- Scope Commands ---------------------------------------------------

			private async Task<DECommonScope> CreateCommonScopeAsync(IDEAuthentificatedUser user, IIdentity userIdentity)
			{
				DECommonScope newScope;
				if (user == null)
				{
					newScope = new DECommonScope(engine, userIdentity != null);
					if (userIdentity != null)
						await newScope.AuthentificateUserAsync(userIdentity);
				}
				else
				{
					newScope = new DECommonScope(engine, user);
				}

				// init scope
				newScope.RegisterService(typeof(IDEDebugContext), this);

				return newScope;
			} // proc CreateCommonScopeAsync

			private XElement CreateScopeReturn(DECommonScope scope)
			{
				var x = new XElement("return");
				if (scope.User != null)
					x.Add(new XAttribute("user", scope.User.Identity.Name));
				return x;
			} // func CreateScopeReturn

			private async Task<XElement> BeginScopeAsync(XElement xMessage)
			{
				// rollback curent scope
				if (currentScope != null && !currentScope.IsCommited.HasValue)
					await currentScope.DisposeAsync();

				currentScope = await CreateCommonScopeAsync(context.User, null);

				return CreateScopeReturn(currentScope);
			} // func BeginScopeAsync
			
			private async Task<XElement> CommitScopeAsync(XElement xMessage)
			{
				await currentScope.CommitAsync();
				return await BeginScopeAsync(xMessage);
			} // func CommitScopeAsync

			private Task<XElement> RollbackScopeAsync(XElement xMessage)
				=> BeginScopeAsync(xMessage);

			#endregion

			[LuaMember(nameof(CurrentNode))]
			public DEConfigItem CurrentNode => currentItem;

			public WebSocket Socket => context.WebSocket;
		} // class LuaDebugSession

		#endregion

		private SimpleConfigItemProperty<int> propertyScriptCount = null;

		private readonly Lua lua = new Lua();               // Global scripting engine
		private readonly DEList<LuaScript> scripts;         // Liste aller geladene Scripts
		private readonly DEList<LuaAttachedGlobal> globals; // List of all attachments

		private readonly LuaEngineTraceLineDebugger debugHook;  // Trace Line Debugger interface
		private readonly LuaCompileOptions debugOptions;        // options for the debugging of scripts
		private bool debugSocketRegistered = false;
		private readonly LuaTable debugEnvironment;

		#region -- Ctor/Dtor/Configuration ------------------------------------------------

		public LuaEngine(IServiceProvider sp, string name)
			: base(sp, name)
		{
			// create the state
			this.propertyScriptCount = new SimpleConfigItemProperty<int>(this, "tw_luaengine_scripts", "Skripte", "Lua-Engine", "Anzahl der aktiven Scripte.", "{0:N0}", 0);

			// create the lists
			this.scripts = new DEList<LuaScript>(this, "tw_scripts", "Scriptlist");
			this.globals = new DEList<LuaAttachedGlobal>(this, "tw_globals", "Attached scripts");

			// Register the service
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDELuaEngine), this);

			// register context extensions
			LuaType.RegisterTypeExtension(typeof(HttpResponseHelper));

			// create the debug options
			debugHook = new LuaEngineTraceLineDebugger(this);
			debugOptions = new LuaCompileOptions()
			{
				DebugEngine = debugHook
			};
			this.debugEnvironment = new LuaTable();

			// update lua runtime
			sp.GetService<DEServer>(true).UpdateLuaRuntime(lua);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					Procs.FreeAndNil(ref propertyScriptCount);

					this.GetService<IServiceContainer>(false)?.RemoveService(typeof(IDELuaEngine));
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

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			// register the debug listener
			var http = this.GetService<IDEHttpServer>(true);
			if (!debugSocketRegistered)
			{
				http.RegisterWebSocketProtocol(this);
				debugSocketRegistered = true;
			}
		} // proc 

		private void LoadScript(XConfigNode cur, LuaFileScript[] scriptRemove)
		{
			// Id des Scripts
			var scriptId = cur.GetAttribute<string>("id");
			if (String.IsNullOrEmpty(scriptId))
				throw new ArgumentNullException("@id", "ScriptId is expected.");

			// Read filename
			var fileName = cur.GetAttribute<string>("filename");
			if (String.IsNullOrEmpty(fileName))
				throw new ArgumentNullException("@filename", "Filename is empty.");

			// Read parameter
			var forceDebugMode = cur.GetAttribute<bool>("debug");
			var encoding = cur.GetAttribute<Encoding>("encoding");

			var script = FindScript(scriptId);
			var fileScript = script as LuaFileScript;

			if (fileScript == null) // script noch nicht vorhanden --> also legen wir es mal an
			{
				if (script != null)
					throw new ArgumentException(String.Format("Script '{0}' already exists.", scriptId));
				var fi = new FileInfo(fileName);
				if (!fi.Exists)
					throw new ArgumentException(String.Format("File '{0}' not found.", fi.FullName));

				new LuaFileScript(this, scriptId, fi, encoding, forceDebugMode);
			}
			else
			{
				fileScript.Encoding = encoding;
				fileScript.SetDebugMode(forceDebugMode);

				scriptRemove[Array.IndexOf(scriptRemove, fileScript)] = null;

				fileScript.Log.Info("Refreshed.");
			}
		} // LoadScript

		#endregion

		#region -- Script Verwaltung ------------------------------------------------------

		private void AddScript(LuaScript script)
		{
			lock (scripts)
			{
				if (FindScript(script.ScriptId) != null)
					throw new IndexOutOfRangeException(String.Format("ScriptId '{0}' is not unique.", script.ScriptId));

				scripts.Add(script);
			}
		} // proc AddScript

		private void RemoveScript(LuaScript script)
		{
			lock (scripts)
				scripts.Remove(script);
		} // proc RemoveScript

		private LuaScript FindScript(string scriptId)
		{
			lock (scripts)
			{
				var index = scripts.FindIndex(s => String.Compare(s.ScriptId, scriptId, StringComparison.OrdinalIgnoreCase) == 0);
				return index >= 0 ? scripts[index] : null;
			}
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

		#region -- IDEWebSocketProtocol ---------------------------------------------------

		async Task IDEWebSocketProtocol.ExecuteWebSocketAsync(IDEWebSocketScope webSocket)
		{
			if (!IsDebugAllowed)
			{
				await webSocket.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Debugging is not active.", CancellationToken.None); // close and dispose socket
				return;
			}

			using (var session = new LuaDebugSession(this, webSocket, CancellationToken.None))
				await session.ExecuteAsync();
		} // func ExecuteWebSocketAsync

		string IDEWebSocketProtocol.BasePath => String.Empty;
		string IDEWebSocketProtocol.Protocol => "dedbg";

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

		public ILuaScript CreateScript(Func<TextReader> code, string name, params KeyValuePair<string, Type>[] parameters)
		   => new LuaMemoryScript(this, code, name, parameters);
			
		public Lua Lua => lua;

		#endregion

		[LuaMember("DebugEnv")]
		public LuaTable DebugEnvironment => debugEnvironment;

		public bool IsDebugAllowed => Config.GetAttribute("allowDebug", false);

		public override string Icon => "/images/lua16.png";
	} // class LuaEngine

	#endregion
}
