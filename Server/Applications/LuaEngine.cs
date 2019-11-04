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
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Data;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;
using static TecWare.DE.Server.Configuration.DEConfigurationConstants;

namespace TecWare.DE.Server
{
	#region -- class LuaEngine --------------------------------------------------------

	/// <summary>Service for Debugging and running lua scripts.</summary>
	internal sealed class LuaEngine : DEConfigLogItem, IDEWebSocketProtocol, IDELuaEngine
	{
		#region -- class LuaScript ----------------------------------------------------

		/// <summary>Loaded script.</summary>
		internal abstract class LuaScript : IDisposable
		{
			private readonly LuaEngine engine;
			private readonly LoggerProxy log;
			private readonly string scriptId;

			private bool compiledWithDebugger;
			private readonly object chunkLock = new object();
			private LuaChunk chunk;

			#region -- Ctor/Dtor ------------------------------------------------------

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

			#region -- Compile --------------------------------------------------------

			protected virtual void Compile(ILuaLexer code, KeyValuePair<string, Type>[] args)
			{
				lock (chunkLock)
				{
					// clear the current chunk
					Procs.FreeAndNil(ref chunk);

					// recompile the script
					chunk = Lua.CompileChunk(code, compiledWithDebugger ? engine.debugOptions : null, args);
				}
			} // proc Compile

			public void LogLuaException(Exception e)
			{
				// unwind target exceptions
				var ex = e.GetInnerException();

				// log exception
				switch (ex)
				{
					case LuaParseException ep:
						Log.Except("{0} ({3} at {1}, {2})", ep.Message, ep.Line, ep.Column, ep.FileName);
						break;
					case LuaRuntimeException er:
						Log.Except("{0} at {2}:{1}\n\n{3}", er.Message, er.Line, er.FileName, er.StackTrace);
						break;
					default:
						Log.Except("Compile failed.", ex);
						break;
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
			public bool Debug { get => compiledWithDebugger; protected set => compiledWithDebugger = value; }

			/// <summary>Does a chunk exists.</summary>
			public bool IsCompiled => Chunk != null;
			/// <summary>Access to the chunk.</summary>
			public LuaChunk Chunk { get { lock (chunkLock) return chunk; } }
		} // class LuaScript

		#endregion

		#region -- class LuaFileBasedScript -------------------------------------------

		/// <summary>Script that is based on file.</summary>
		private abstract class LuaFileBasedScript : LuaScript
		{
			private FileInfo fileSource;        // File source of the script
			private Encoding encoding;          // Encoding style of the file
			private DateTime compiledStamp;     // Last time compiled

			public LuaFileBasedScript(LuaEngine engine, string scriptId, FileInfo fileSource, Encoding encoding, bool compileWithDebugger)
				: base(engine, scriptId, compileWithDebugger)
			{
				this.encoding = encoding ?? Encoding.Default;

				SetFileSource(fileSource);
				SetDebugMode(compileWithDebugger);
			} // ctor

			protected sealed override void Compile(ILuaLexer code, KeyValuePair<string, Type>[] args) 
				=> base.Compile(code, args);

			protected void Compile(Func<TextReader> open, KeyValuePair<string, Type>[] args)
			{
				// Re-create the script
				using (var code = LuaLexer.Create(ScriptBase ?? ScriptId, open(), false))
					Compile(code, args);
				compiledStamp = DateTime.Now;
				OnCompiled();
			} // proc Compile

			protected virtual void OnCompiled() { }

			private DateTime GetScriptFileStampSecure()
			{
				try
				{
					fileSource.Refresh();
					return fileSource.LastWriteTime;
				}
				catch (IOException)
				{
					return DateTime.MinValue;
				}
			} // funcGetScriptFileStamp

			public void SetDebugMode(bool compileWithDebug)
			{
				Debug = compileWithDebug;
				compiledStamp = DateTime.MinValue;
			} // proc SetDebugMode

			public void SetFileSource(FileInfo fileSource)
			{
				if (!fileSource.Exists)
					throw new ArgumentException($"File '{fileSource.FullName}' not found.", nameof(fileSource));
				this.fileSource = fileSource;
				compiledStamp = DateTime.MinValue;
			} // proc SetFileSource

			/// <summary>FileInfo object of the source code.</summary>
			public FileInfo FileSource => fileSource;
			/// <summary>Full path to the script file</summary>
			public override string ScriptBase => fileSource.FullName;
			/// <summary>Encoding of the file.</summary>
			public Encoding Encoding { get => encoding; set => encoding = value; }
			/// <summary>Check the script file stamp.</summary>
			public bool IsOutDated => GetScriptFileStampSecure() > compiledStamp;
		} // class LuaFileBasedScript

		#endregion

		#region -- class LuaFileScript ------------------------------------------------

		/// <summary>Script that is based on file.</summary>
		private sealed class LuaFileScript : LuaFileBasedScript
		{
			public LuaFileScript(LuaEngine engine, string scriptId, FileInfo fileSource, Encoding encoding, bool compileWithDebugger)
				: base(engine, scriptId, fileSource, encoding, compileWithDebugger)
			{
				Compile();
			} // ctor

			public bool? Compile()
			{
				if (!IsCompiled || IsOutDated)
				{
					try
					{
						Compile(() => new StreamReader(FileSource.FullName, Encoding), null);
						return true;
					}
					catch (Exception e)
					{
						LogLuaException(e);
						return false;
					}
				}
				else
					return null;
			} // proc Compile
			protected override void OnCompiled()
			{
				base.OnCompiled();

				// Notify that the script is changed
				foreach (var c in Engine.GetAttachedGlobals(ScriptId))
				{
					try { c.OnScriptChanged(); }
					catch (Exception e) { Log.Except("Attach to failed.", e); }
				}
			} // proc OnCompiled
		} // class LuaFileScript

		#endregion

		#region -- class LuaTestScript ------------------------------------------------

		/// <summary>Script that is based on file.</summary>
		private sealed class LuaTestScript : LuaFileBasedScript
		{
			public LuaTestScript(LuaEngine engine, string scriptId, FileInfo fileSource, Encoding encoding)
				: base(engine, scriptId, fileSource, encoding, true)
			{
			} // ctor

			/// <summary>Recreate always the chunk</summary>
			public void Compile()
			{
				if (FileSource == null)
					throw new ArgumentNullException(nameof(FileSource), "FileSource is null, please check for compile errors.");
				Compile(() => new StreamReader(FileSource.FullName, Encoding), null);
			} // proc Compile
		} // class LuaTestScript

		#endregion

		#region -- class LuaMemoryScript ----------------------------------------------

		/// <summary>In memory script, that is not based on a file.</summary>
		private sealed class LuaMemoryScript : LuaScript, ILuaScript
		{
			private readonly string scriptBase;

			#region -- Ctor/Dtor ------------------------------------------------------

			public LuaMemoryScript(LuaEngine engine, ILuaLexer code, string scriptBase, KeyValuePair<string, Type>[] args)
				: base (engine, Guid.NewGuid().ToString("D"), true)
			{
				if (code.Current == null)
					code.Next();

				this.scriptBase = scriptBase ?? code.Current.Start.FileName;

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

			private string ChangeDirectory()
			{
				if (ScriptBase != null)
				{
					var currentDirectory = Path.GetDirectoryName(ScriptBase);
					var oldDirectory = Environment.CurrentDirectory;
					Environment.CurrentDirectory = currentDirectory;
					return oldDirectory;
				}
				else
					return null;
			} // func ChangeDirectory

			public LuaResult Run(LuaTable table, bool throwExceptions, params object[] args)
			{
				var oldDirectory = ChangeDirectory();
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
				finally
				{
					if (oldDirectory != null)
						Environment.CurrentDirectory = oldDirectory;
				}
			} // func Run

			public override string ScriptBase => scriptBase;
		} // class LuaMemoryScript

		#endregion

		#region -- class LuaAttachedGlobal --------------------------------------------

		/// <summary>Connection between scripts and environments.</summary>
		internal sealed class LuaAttachedGlobal : ILuaAttachedScript
		{
			public event CancelEventHandler ScriptChanged;
			public event EventHandler ScriptCompiled;

			private readonly LuaEngine engine;
			private readonly LoggerProxy log;
			private readonly string scriptId;
			private readonly LuaTable table;
			private bool autoRun;
			private bool needToRun;

			private readonly object scriptLock = new object();
			private LuaScript currentScript = null;

			#region -- Ctor/Dtor ------------------------------------------------------

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
				if (table.GetMemberValue("host") is IServiceProvider sp)
					return sp.GetService<ILogger>(false);

				return null;
			} // func LogMsg

			#endregion

			#region -- Run, ResetScript -----------------------------------------------

			public void ResetScript()
			{
				lock (scriptLock)
					currentScript = null;
			} // proc ResetScript

			public void Run(bool throwExceptions)
			{
				lock (scriptLock)
				{
					using (var m = log.CreateScope(LogMsgType.Information, true, true))
					{
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
					}
				}
			} // proc Run

			private LuaScript GetScript(bool throwExceptions)
			{
				if (currentScript == null)
				{
					currentScript = engine.FindScript(scriptId);
					if (currentScript == null && throwExceptions)
						throw new ArgumentOutOfRangeException(nameof(ScriptId), String.Format("Script '{0}' not found.", scriptId));
				}
				return currentScript;
			} // func GetScript

			public void OnScriptChanged()
			{
				var e = new CancelEventArgs(!autoRun);
				ScriptChanged?.Invoke(this, e);

				if (e.Cancel || !IsCompiled)
				{
					needToRun = true;
					log.Info("Waiting for execution.");
				}
				else
					Run(false);
			} // proc OnScriptChanged

			public void OnScriptCompiled()
				=> ScriptCompiled?.Invoke(this, EventArgs.Empty);

			#endregion

			public string ScriptId => scriptId;
			public LuaTable LuaTable => table;

			public bool IsCompiled => GetScript(false)?.IsCompiled ?? false;
			public bool NeedToRun => needToRun;
			public bool AutoRun { get { return autoRun; } set { autoRun = value; } }
		} // class LuaAttachedGlobal

		#endregion

		#region -- class LuaEngineTraceLineDebugger -----------------------------------

		private sealed class LuaEngineTraceLineDebugger : LuaTraceLineDebugger
		{
			#region -- class LuaTraceLineDebugInfo ------------------------------------

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
				=> LuaExceptionData.UnwindException(e.Exception, () => new LuaTraceLineDebugInfo(e, engine.FindScript(e.SourceName)));

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

		#region -- class LuaTestEnvironment -------------------------------------------

		private sealed class LuaTestEnvironment : LuaTable
		{
			#region -- class LuaTestFunctionSet ---------------------------------------

			private sealed class LuaTestFunctionSet : LuaTable
			{
				private readonly LuaTestEnvironment env;

				public LuaTestFunctionSet(LuaTestEnvironment env)
					=> this.env = env;

				protected override object OnIndex(object key)
					=> base.OnIndex(key) ?? env.OnIndexInternal(key);

				[LuaMember("UseNode")]
				private void SetCurrentNode(string path)
				{
					if (!path.StartsWith("/"))
						throw new ArgumentOutOfRangeException(nameof(path), path, "Relative path is not allowed.");

					env.currentItem = ProcsDE.UseNode((DEConfigItem)env.session.Engine.Server, path, 1);
				} // proc SetCurrentNode

				[LuaMember("AssertFail")]
				private void LuaAssertFail(string message)
					=> throw CreateAssertException(message);

				[LuaMember("AssertAreEqual")]
				private void LuaAssertAreEqual(object expected, object actual, string message)
				{
					if(expected == null)
					{
						if (actual != null && !actual.Equals(expected))
							throw CreateAssertException(message);
					}
					else if(!expected.Equals(actual))
						throw CreateAssertException(message);
				} // proc LuaAssertAreEqual

				[LuaMember("AssertAreNotEqual")]
				private void LuaAssertAreNotEqual(object notExpected, object actual, string message)
				{
					if (notExpected == null)
					{
						if (actual != null && !actual.Equals(notExpected))
							return;
						throw CreateAssertException(message);
					}
					else if (notExpected.Equals(actual))
						throw CreateAssertException(message);
				} // proc LuaAssertAreNotEqual
				[LuaMember("AssertIsNotNull")]
				private void LuaAssertIsNotNull(object value, string message)
				{
					if (value == null)
						throw CreateAssertException(message);
				} // proc LuaAssertIsNotNull

				[LuaMember("AssertIsNull")]
				private void LuaAssertIsNull(object value, string message)
				{
					if (value != null)
						throw CreateAssertException(message);
				} // proc LuaAssertIsNull

				[LuaMember("AssertIsFalse")]
				private void LuaAssertIsFalse(bool value, string message)
				{
					if (value)
						throw CreateAssertException(message);
				} // proc LuaAssertIsFalse

				[LuaMember("AssertIsTrue")]
				private void LuaAssertIsTrue(bool value, string message)
				{
					if (!value)
						throw CreateAssertException(message);
				} // proc LuaAssertIsTrue

				private Exception CreateAssertException(string message)
					=> throw new Exception(message ?? "Assert failed.");
			} // class LuaTestFunctionSet

			#endregion

			private readonly LuaDebugSession session;
			private readonly LuaTestFunctionSet functionSet;
			private DEConfigItem currentItem;

			public LuaTestEnvironment(LuaDebugSession session)
			{
				this.session = session;
				this.functionSet = new LuaTestFunctionSet(this);
				this.currentItem = (DEConfigItem)session.Engine.Server; // start with the same node
			} // ctor

			private object OnIndexInternal(object key)
				=> session.Engine.DebugEnvironment.GetValue(key, true) ?? currentItem.GetValue(key);

			protected override object OnIndex(object key)
				=> base.OnIndex(key) ?? functionSet.GetValue(key);
		} // class LuaTestEnvironment

		#endregion

		#region -- class LuaDebugSession ----------------------------------------------

		/// <summary>Debug session and scope for the commands.</summary>
		private sealed class LuaDebugSession : LuaTable, IDEDebugContext, IDisposable
		{
			#region -- class ReferenceEqualImplementation -----------------------------

			private sealed class ReferenceEqualImplementation : IEqualityComparer<object>
			{
				public new bool Equals(object x, object y)
					=> ReferenceEquals(x, y);

				public int GetHashCode(object obj)
					=> obj.GetHashCode(); // fail

				public static ReferenceEqualImplementation Instance { get; } = new ReferenceEqualImplementation();
			} // class ReferenceEqualImplementation

			#endregion

			private readonly LuaEngine engine; // lua engine
			private readonly LoggerProxy log; // log for the debugging

			private readonly IDEWebSocketScope context; // context of the web-socket, will will not set this context (own context management)
			private DECommonScope currentScope = null; // current context for the code execution

			private readonly CancellationToken cancellationToken;
			private DEConfigItem currentItem = null; // current node, that is used as parent

			#region -- Ctor/Dtor ------------------------------------------------------

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

			#endregion

			#region -- Execute Protocol -----------------------------------------------

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
					log.Except($"Debug session closed ({e.WebSocketErrorCode}).", e);
				}
				catch (Exception e)
				{
					log.Except("Debug session failed.", e);
				}
			} // proc Execute

			#endregion

			#region -- CreateException, CreateMember ----------------------------------
			
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
						x.Add(new XAttribute("message", Procs.RemoveInvalidXmlChars(e.Message)));
				}
				else
				{
					x.Add(new XAttribute("message", Procs.RemoveInvalidXmlChars(e.Message)));
					x.Add(new XAttribute("type", LuaType.GetType(e.GetType()).AliasOrFullName));
					var data = LuaExceptionData.GetData(e);
					x.Add(new XElement("stackTrace", Procs.RemoveInvalidXmlChars(data == null ? e.StackTrace : data.StackTrace)));

					if (e.InnerException != null)
						x.Add(CreateException(new XElement("innerException"), e.InnerException));
				}

				return x;
			} // proc CreateException

			private static object GetValueSafe(Func<object> getValue)
			{
				try
				{
					return getValue();
				}
				catch (Exception e)
				{
					return $"[{e.GetType().Name}] {e.Message}";
				}
			} // func GetValueSafe

			private XElement CreateMember(Stack<object> values, object member, Func<object> getValue, Type type = null, int maxLevel = Int32.MaxValue)
			{
				static bool TryGetTypedEnumerable(Type valueType, out Type enumerableType)
				{
					enumerableType = valueType.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
					if (enumerableType == null)
						return false;

					enumerableType = enumerableType.GetGenericArguments()[0];
					return true;
				} // func TryGetTypedEnumerable
				
				static XText GetTextMember(object value)
				{
					var t = Procs.ChangeType<string>(value);
					if (t.Length > 1024)
						t = t.Substring(0, 1021) + "...";
					return new XText(Procs.RemoveInvalidXmlChars(t, ' '));
				} // func GetTextMember

				var value = GetValueSafe(getValue);
				var valueExists = values.Contains(value, ReferenceEqualImplementation.Instance);
				values.Push(value);
				try
				{
					var displayType = LuaType.GetType(type ?? (value != null ? value.GetType() : typeof(object))).AliasOrFullName;

					var x = new XElement("v",
						member is int ? new XAttribute("i", member) : new XAttribute("n", member.ToString()),
						new XAttribute("t", displayType)
					);

					if (value == null)
					{ }
					else if (valueExists)
					{
						x.Add(new XAttribute("ct", "recursion"));
					}
					else if (value is LuaTable t)
					{
						if (values.Count >= maxLevel)
							x.Add(new XText("(table)"));
						else
						{
							x.Add(new XAttribute("ct", "table"));
							foreach (var key in ((IDictionary<object, object>)t).Keys)
								x.Add(CreateMember(values, key, () => t[key]));
						}
					}
					else if (value is string s)
					{
						x.Add(GetTextMember(s));
					}
					else if (value is byte[] b)
					{
						x.Add(new XText(
							b.Length > 300
								? Procs.ConvertToString(b, 0, 300) + "..."
								: Procs.ConvertToString(b)
						));
					}
					else if (value is IDataRow row)
					{
						if (values.Count >= maxLevel)
							x.Add(new XText("(row)"));
						else
						{
							x.Add(new XAttribute("ct", "row"));

							for (var i = 0; i < row.Columns.Count; i++)
								x.Add(CreateMember(values, row.Columns[i].Name, () => row[i], row.Columns[i].DataType));
						}
					}
					else if (value is IEnumerable<IDataRow> rows)
					{
						if (values.Count >= maxLevel)
							x.Add(new XText("(IEnumerable<IDataRow>)"));
						else
							CreateRowEnumerable(rows, x);
					}
					else if (values.Count == 1 && TryGetTypedEnumerable(value.GetType(), out var enumerableType))
					{
						if (Type.GetTypeCode(enumerableType) == TypeCode.Object)
							CreateTypedEnumerable((System.Collections.IEnumerable)value, enumerableType, x);
						else // return array as table
						{
							if (values.Count >= maxLevel)
								x.Add(new XText($"({LuaType.GetType(enumerableType).AliasOrFullName}[])"));
							else
							{
								x.Add(new XAttribute("ct", "table"));
								var rowCount = 0;
								foreach (var v in (System.Collections.IEnumerable)value)
								{
									if (rowCount >= 10)
										break;

									x.Add(CreateMember(values, rowCount++, () => v));
								}
							}
						}
					}
					else
						x.Add(GetTextMember(value));

					return x;
				}
				finally
				{
					values.Pop();
				}
			} // func CreateMember

			private static void CreateRowEnumerable(IEnumerable<IDataRow> rows, XElement x)
			{
				x.Add(new XAttribute("ct", "rows"));

				var rowCount = 0;
				var columnNames = (string[])null;
				var columns = (IReadOnlyList<IDataColumn>)null;
				foreach (var r in rows)
				{
					if (rowCount >= 10)
						break;

					if (columns == null)
					{
						columns = r.Columns;
						columnNames = new string[columns.Count];

						// emit columns
						var xFields = new XElement("f");
						x.Add(xFields);
						for (var i = 0; i < columns.Count; i++)
						{
							columnNames[i] = "c" + i.ToString();

							xFields.Add(new XElement(columnNames[i],
								new XAttribute("n", columns[i].Name),
								new XAttribute("t", LuaType.GetType(columns[i].DataType).AliasOrFullName)
							));
						}
					}

					var xRow = new XElement("r");
					x.Add(xRow);
					for (var i = 0; i < columns.Count; i++)
					{
						var v = r[i];
						if (v != null)
							xRow.Add(new XElement(columnNames[i], Procs.RemoveInvalidXmlChars(v.ChangeType<string>())));
					}

					rowCount++;
				}
			} // proc CreateRowEnumerable

			private static void CreateTypedEnumerable(System.Collections.IEnumerable values, Type type, XElement x)
			{
				// create the column descriptions
				var columns = new List<Tuple<string, string, Type, Func<object, object>>>();

				foreach (var m in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetField | BindingFlags.GetProperty))
				{
					if (m is FieldInfo fi)
					{
						columns.Add(new Tuple<string, string, Type, Func<object, object>>(
							"c" + columns.Count.ToString(),
							fi.Name,
							fi.FieldType,
							fi.GetValue
						));
					}
					else if (m is PropertyInfo pi)
					{
						columns.Add(new Tuple<string, string, Type, Func<object, object>>(
							"c" + columns.Count.ToString(),
							pi.Name,
							pi.PropertyType,
							pi.GetValue
						));
					}
				}

				// emit enum
				x.Add(new XAttribute("ct", "rows"));

				// emit fields
				x.Add(new XElement("f",
					from c in  columns
					select new XElement(c.Item1,
						new XAttribute("n", c.Item2),
						new XAttribute("t", LuaType.GetType(c.Item3).AliasOrFullName)
					)
				));

				// enumerate rows
				var rowCount = 0;
				foreach (var cur in values)
				{
					if (rowCount >= 10)
						break;

					var xRow = new XElement("r");
					x.Add(xRow);
					for (var i = 0; i < columns.Count; i++)
					{
						var v = GetValueSafe(() => columns[i].Item4(cur));
						if (v != null)
							xRow.Add(new XElement(columns[i].Item1, v.ChangeType<string>()));
					}

					rowCount++;
				}
			} // func CreateTypedEnumerable

			#endregion

			#region -- ProcessMessage -------------------------------------------------

			private async Task ProcessMessage(XElement x)
			{
				Debug.Print("[Server] Receive Message: {0}", x.GetAttribute("token", 0));

				var command = x.Name;

				if (command == "execute")
					await SendAnswerAsync(x, await ExecuteAsync(x));
				else if (command == "run")
					await SendAnswerAsync(x, await RunScriptAsync(x));
				else if (command == "recompile")
					await SendAnswerAsync(x, await RecompileAsync(x));
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
				UpdateAnswerToken(xMessage, xAnswer);

				Debug.Print("[Server] Send Message: {0}", xAnswer.GetAttribute("token", 0));

				// encode and send
				byte[] buf;
				try
				{
					buf = Encoding.UTF8.GetBytes(xAnswer.ToString(SaveOptions.None));
					if (buf.Length > 1 << 20)
						throw new ArgumentOutOfRangeException("Answer to big.");
				}
				catch (Exception e)
				{
					var x = CreateException(new XElement("exception"), e);
					UpdateAnswerToken(xMessage, x);
					buf = Encoding.UTF8.GetBytes(x.ToString(SaveOptions.None));
				}

				await Socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, cancellationToken);
			} // proc SendAnswerAsync

			private static void UpdateAnswerToken(XElement xMessage, XElement xAnswer)
			{
				if (xMessage != null && xAnswer.Attribute("token") == null)
				{
					var token = xMessage.GetAttribute("token", 0);
					if (token > 0)
						xAnswer.Add(new XAttribute("token", token));
				}
			} // proc UpdateAnswerToken

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

			#endregion

			#region -- Execute --------------------------------------------------------

			private async Task<XElement> ExecuteAsync(XElement xMessage)
			{
				return await Task.Run(
					() =>
					{
						var compileTime = 0L;
						var runTime = 0L;

						// compile the chunk
						LuaChunk chunk;
						var compileStopWatch = Stopwatch.StartNew();
						try
						{
							chunk = engine.Lua.CompileChunk(xMessage.Value, "remote.lua", engine.debugOptions);
						}
						finally
						{
							compileTime = compileStopWatch.ElapsedMilliseconds;
						}

						using (currentScope?.Use())
						{
							LuaResult r;

							// run the chunk on the node
							var runStopWatch = Stopwatch.StartNew();
							try
							{
								r = chunk.Run(this);
							}
							finally
							{
								runTime = runStopWatch.ElapsedMilliseconds;
							}

							// return the result
							var xAnswer = new XElement("return",
								new XAttribute("runTime", runTime),
								new XAttribute("compileTime", compileTime)
							);
							for (var i = 0; i < r.Count; i++)
								xAnswer.Add(CreateMember(new Stack<object>(), i, () => r[i]));

							return xAnswer;
						}
					}
				);
			} // func Execute

			#endregion

			#region -- RunScript ------------------------------------------------------

			#region -- class RunScriptClass -------------------------------------------

			private sealed class RunScriptClass
			{
				private readonly LuaDebugSession session;
				private readonly LuaTestScript[] scripts;
				private readonly Func<string, bool> methodFilter;

				public RunScriptClass(LuaDebugSession session, string scriptId, string methodName)
				{
					this.session = session;
					this.scripts = session.engine.FindScripts<LuaTestScript>(scriptId ?? throw new ArgumentNullException(nameof(scriptId)));
					this.methodFilter = Procs.GetFilerFunction(methodName, true);
				} // ctor

				private XElement ExecuteScript(LuaTestScript script)
				{
					var compileTime = -1L;
					var runTime = 0L;
					var xScriptResult = new XElement("script",
						new XAttribute("id", script.ScriptId)
					);

					XElement SetScriptFailed(Exception e)
					{
						xScriptResult.SetAttributeValue("success", false);
						session.Notify(xScriptResult);

						return session.CreateException(xScriptResult, e);
					} // proc SetScriptFailed

					// check the script, do we need a recompile
					if (!script.IsCompiled || script.IsOutDated)
					{
						var compileStopWatch = Stopwatch.StartNew();
						try
						{
							script.Compile();
						}
						catch (Exception e)
						{
							return SetScriptFailed(e.GetInnerException());
						}
						finally
						{
							compileTime = compileStopWatch.ElapsedMilliseconds;
						}
					}

					// execute the base script on a fresh environment
					var luaTestEnvironment = new LuaTestEnvironment(session);
					var runStopWatch = Stopwatch.StartNew();
					try
					{
						script.Chunk.Run(luaTestEnvironment);
					}
					catch (Exception e)
					{
						return SetScriptFailed(e.GetInnerException());
					}
					finally
					{
						runTime = runStopWatch.ElapsedMilliseconds;
					}

					xScriptResult.Add(
						new XAttribute("success", true),
						new XAttribute("compileTime", compileTime),
						new XAttribute("runTime", runTime)
					);
					session.Notify(xScriptResult);

					// run the functions
					foreach (var functionName in luaTestEnvironment.Members.Keys.Where(methodFilter))
					{
						var func = luaTestEnvironment[functionName];
						if (Lua.RtInvokeable(func))
						{
							var testTime = 0L;
							var testException = (Exception)null;
							var testStopWatch = Stopwatch.StartNew();
							try
							{
								Lua.RtInvoke(func);
							}
							catch (Exception e)
							{
								testException = e.GetInnerException();
							}
							finally
							{
								testTime = testStopWatch.ElapsedMilliseconds;
							}

							var xTest = new XElement("test",
								new XAttribute("name", functionName),
								new XAttribute("time", testTime),
								new XAttribute("success", testException == null)
							);

							// send test result, without exception
							if (testException != null)
								xTest.Add(new XAttribute("message", testException.Message));
							session.Notify(xTest);

							// prepare summary
							if (testException != null)
							{
								xTest.Attribute("message").Remove();
								xTest = session.CreateException(xTest, testException);
							}
							xScriptResult.Add(xTest);
						}
						else
							xScriptResult.Add(new XElement("ignore", new XAttribute("name", functionName)));
					}

					return xScriptResult;
				} // func ExecuteScript

				public XElement Execute()
				{
					var xReturn = new XElement("return");
					foreach (var script in scripts)
					{
						using (session.currentScope?.Use())
							xReturn.Add(ExecuteScript(script));

						// rollback scope before the next script
						session.BeginNewScopeAsync().AwaitTask();
					}
					return xReturn;
				} // proc Execute

				public XElement GetAnswer()
				{
					var xAnswer = new XElement("return");
					return xAnswer;
				} // func GetAnswer
			} // class RunScriptClass

			#endregion

			private async Task<XElement> RunScriptAsync(XElement xMessage)
			{
				// parse script
				var scriptId = xMessage.GetAttribute<string>("script", null);
				var methodName = xMessage.GetAttribute<string>("method", null);

				// collect scripts
				var runScript = new RunScriptClass(this, scriptId, methodName);

				// execute the scripts
				return await Task.Run(new Func<XElement>(runScript.Execute));
			} // func RunScriptAsync

			#endregion

			#region -- Recompile ------------------------------------------------------

			private async Task<XElement> RecompileAsync(XElement xMessage)
			{
				var r = await Task.Run(() =>
					{
						// use a different scope for compile
						// we do not want to change the current debug scope
						using (var scope = CreateDebugScope())
						using (scope.Use())
						{
							// invoke pre-compile script
							CallMemberDirect("OnBeforeCompile", new object[] { xMessage }, ignoreNilFunction: true);

							// recompile scripts
							var r = engine.Recompile().ToArray();

							// after compile
							CallMemberDirect("OnAfterCompile", new object[] { xMessage }, ignoreNilFunction: true);

							scope.CommitAsync().Wait();
							return r;
						}
					}
				);
				return new XElement("return",
					from c in r
					select new XElement("r",
						new XAttribute("id", c.scriptId),
						new XAttribute("failed", c.failed)
					)
				);
			} // func RecompileAsync

			#endregion

			#region -- GlobalVars -----------------------------------------------------

			private object GetMemberFromPath(string memberPath)
			{
				if (String.IsNullOrEmpty(memberPath))
					return currentItem;
				else
				{
					if (memberPath[0] != '[')
						memberPath = "." + memberPath;
					try
					{
						var func = engine.Lua.CreateLambda<Func<LuaTable,object>>("member", "return c" + memberPath, "c");
						var r = func(currentItem);
						if (r == null)
							throw new ArgumentNullException("r", $"'{memberPath}' has no result.");
						return r;
					}
					catch (Exception e)
					{
						return new LuaTable { ["$exception"] = $"[{e.GetType().Name}] {e.Message}" };
					}
				}
			} // func GetMemberFromPath

			private XElement GetMember(XElement xMessage)
			{
				var xAnswer = new XElement("return");

				var memberPath = xMessage.GetAttribute("p", null);
				var memberLevel = xMessage.GetAttribute("l", memberPath == null ? 4 : 0);

				var members = GetMemberFromPath(memberPath);
				if (members != null)
				{
					if (members is LuaTable t)
					{
						foreach (var c in t)
							xAnswer.Add(CreateMember(new Stack<object>(), c.Key, () => c.Value, maxLevel: memberLevel));
					}
					else
						xAnswer.Add(CreateMember(new Stack<object>(), "$0", () => members, maxLevel: memberLevel));
				}

				return xAnswer;
			} // func GlobalVars

			#endregion

			#region -- UseNode --------------------------------------------------------

			private XElement UseNode(XElement xMessage)
			{
				var p = xMessage.GetAttribute("node", String.Empty);
				if (!String.IsNullOrEmpty(p))
					UseNode(p);
				return new XElement("return", new XAttribute("node", currentItem.ConfigPath));
			} // proc UseNode

			private void UseNode(string path)
			{
				if (!path.StartsWith("/")) // relative path
					currentItem = ProcsDE.UseNode(currentItem, path, 0);
				else // absolute path
					currentItem = ProcsDE.UseNode((DEConfigItem)engine.Server, path, 1);
			} // proc UseItem

			#endregion

			#region -- List -----------------------------------------------------------

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

			#region -- Scope Commands -------------------------------------------------

			private DECommonScope CreateDebugScope()
			{
				var scope = new DECommonScope(engine, false, null);
				scope.RegisterService(typeof(IDEDebugContext), this);
				return scope;
			} // proc CreateDebugScope

			private async Task<DECommonScope> CreateCommonScopeAsync(IDEAuthentificatedUser user, IIdentity userIdentity)
			{
				DECommonScope newScope;
				if (user == null)
				{
					newScope = new DECommonScope(engine, userIdentity != null, null);
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

			private async Task BeginNewScopeAsync()
			{
				if (currentScope != null && !currentScope.IsCommited.HasValue)
					await currentScope.DisposeAsync();

				currentScope = await CreateCommonScopeAsync(context.User, null);
			} // func BeginNewScopeAsync

			private async Task<XElement> BeginScopeAsync(XElement xMessage)
			{
				// rollback curent scope
				await BeginNewScopeAsync();

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

			public LuaEngine Engine => engine;

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

					globals.Dispose();
					scripts.Dispose();
					lua.Dispose();
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
			var scriptRemove = (from s in scripts where s is LuaFileBasedScript select (LuaFileBasedScript)s).ToArray();

			foreach (var cur in XConfigNode.Create(Server.Configuration, config.ConfigNew).Elements(xnLuaScript, xnLuaTestScript))
			{
				try
				{
					if (cur.Name == xnLuaTestScript)
						LoadScript(false, cur, scriptRemove);
					else if (cur.Name == xnLuaScript)
						LoadScript(true, cur, scriptRemove);
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

		private void LoadScript(bool configScript, XConfigNode cur, LuaFileBasedScript[] scriptRemove)
		{
			// Id des Scripts
			var scriptId = cur.GetAttribute<string>("id");
			if (String.IsNullOrEmpty(scriptId))
				throw new ArgumentNullException("@id", "ScriptId is expected.");

			// Read filename
			var fileName = cur.GetAttribute<string>("filename");
			if (String.IsNullOrEmpty(fileName))
				throw new ArgumentNullException("@filename", "Filename is empty.");

			// Read encoding
			var encoding = cur.GetAttribute<Encoding>("encoding");

			// search for the script
			var script = FindScript(scriptId);

			if (configScript) // add normal config script
			{
				var forceDebugMode = cur.GetAttribute<bool>("debug");

				if (script != null && script is LuaFileScript fileScript) // script exists, update
				{
					fileScript.Encoding = encoding;
					fileScript.SetFileSource(new FileInfo(fileName));
					fileScript.SetDebugMode(forceDebugMode);

					scriptRemove[Array.IndexOf(scriptRemove, fileScript)] = null;
				}
				else
				{
					if (script != null) // there is a script of a different type
						script.Dispose();

					// add new script
					new LuaFileScript(this, scriptId, new FileInfo(fileName), encoding, forceDebugMode);
				}
			}
			else // add debug script
			{
				if (script != null && script is LuaTestScript testScript) // script exists, update
				{
					testScript.Encoding = encoding;
					testScript.SetFileSource(new FileInfo(fileName));

					scriptRemove[Array.IndexOf(scriptRemove, testScript)] = null;
				}
				else
				{
					if (script != null) // there is a script of a different type
						script.Dispose();

					// add new script
					new LuaTestScript(this, scriptId, new FileInfo(fileName), encoding);
				}
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

		private T[] FindScripts<T>(string filterExpression = null)
			where T : LuaScript
		{
			var filter = Procs.GetFilerFunction(filterExpression);
			lock (scripts)
			{
				return (
					from c in scripts
					let s = c as T
					where s != null && (filter == null || filter(s.ScriptId))
					select s
				).ToArray();
			}
		} // func FindScript

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
			{
				foreach (var c in globals)
					if (String.Compare(c.ScriptId, scriptId, true) == 0)
						yield return c;
			}
		} // func GetAttachedGlobals

		private void RemoveAttachedGlobal(LuaAttachedGlobal item)
		{
			lock (globals)
				globals.Remove(item);
		} // func RemoveAttachedGlobal

		private IEnumerable<(string scriptId, bool failed)> Recompile()
		{
			foreach (var cur in FindScripts<LuaFileScript>(null))
			{
				var r = cur.Compile();
				if (r.HasValue)
					yield return (cur.ScriptId, !r.Value);
			}
		} // proc Recompile

		#endregion

		#region -- IDEWebSocketProtocol ---------------------------------------------------

		async Task IDEWebSocketProtocol.ExecuteWebSocketAsync(IDEWebSocketScope webSocket)
		{
			switch (DebugAllowed)
			{
				case LuaEngineAllowDebug.Local:
					if (!webSocket.IsLocal)
						goto case LuaEngineAllowDebug.Disabled;
					break;
				case LuaEngineAllowDebug.Disabled:
					await webSocket.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Debugging is not active.", CancellationToken.None); // close and dispose socket
					return;
			}

			using var session = new LuaDebugSession(this, webSocket, CancellationToken.None);
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

		public ILuaScript CreateScript(Func<TextReader> open, string scriptBase, params KeyValuePair<string, Type>[] parameters)
		{
			using (var code = LuaLexer.Create(scriptBase, open()))
				return new LuaMemoryScript(this, code, scriptBase, parameters);
		} // func CreateScript

		public ILuaScript CreateScript(ILuaLexer code, string scriptBase, params KeyValuePair<string, Type>[] parameters)
			=> new LuaMemoryScript(this, code, scriptBase, parameters);

		public Lua Lua => lua;

		#endregion

		[LuaMember("DebugEnv")]
		public LuaTable DebugEnvironment => debugEnvironment;

		public LuaEngineAllowDebug DebugAllowed => TryParseAllowDebug(Config.GetAttribute("allowDebug", "local"), out var t) ? t : LuaEngineAllowDebug.Disabled;

		public override string Icon => "/images/lua16.png";

		public static bool TryParseAllowDebug(string value, out LuaEngineAllowDebug allowDebug)
		{
			switch(value)
			{
				case "true":
				case "remote":
					allowDebug = LuaEngineAllowDebug.Remote;
					return true;
				case "local":
					allowDebug = LuaEngineAllowDebug.Local;
					return true;
				default:
					allowDebug = LuaEngineAllowDebug.Disabled;
					return true;
			}
		} // func TryParseAllowDebug

		public static string FormatAllowDebug(LuaEngineAllowDebug allowDebug)
		{
			switch(allowDebug)
			{
				case LuaEngineAllowDebug.Local:
					return "local";
				case LuaEngineAllowDebug.Remote:
					return "remote";
				default:
					return "false";
			}
		} // func FormatAllowDebug
	} // class LuaEngine

	#endregion
}
