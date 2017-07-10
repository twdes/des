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
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class ClientMemberValue --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class ClientMemberValue
	{
		private readonly string name;
		private readonly string typeName;
		private readonly Type type; // is null if the value is not converted
		private readonly object coreValue;
		private readonly Lazy<object> value;

		internal ClientMemberValue(string name, string typeName, Type type, object coreValue)
		{
			this.name = name;
			this.typeName = typeName;
			this.type = type;
			this.coreValue = coreValue;
			this.value = new Lazy<object>(ConvertType);
		} // ctor

		private object ConvertType()
		{
			if (IsValueArray || IsValueList)
				return coreValue;
			else if (type == null)
				return null;
			
			try
			{
				return Procs.ChangeType(coreValue, type);
			}
			catch
			{
				return null;
			}
		} // func ConvertType


		public string Name => name;
		public string TypeName => typeName;

		public bool IsConverted => type != null;

		public Type Type => type ?? typeof(string);

		public object Value => value.Value;
		public string ValueAsString => coreValue == null ? "-NULL-" : (coreValue is string s ? s : "-VALUE-");

		public bool IsValueList => coreValue is ClientMemberValue[][];
		public bool IsValueArray => coreValue is ClientMemberValue[];
	} // class ClientMemberValue

	#endregion

	#region -- class ClientDebugException -----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class ClientDebugException : Exception
	{
		private readonly string remoteStackTrace;
		private readonly string exceptionType;

		public ClientDebugException(XElement x)
			:base(x.GetAttribute("message", "No message"))
		{
			this.exceptionType = x.GetAttribute("type", "Exception");
			this.remoteStackTrace = x.Element("stackTrace")?.Value;
		} // ctor

		public string ExceptionType => exceptionType;
		public override string StackTrace => remoteStackTrace;
	} // class ClientDebugException

	#endregion

	#region -- class ClientDebugSession -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public abstract class ClientDebugSession : IDisposable
	{
		public event EventHandler CurrentUsePathChanged;

		#region -- class ReturnWait -----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ReturnWait
		{
			private readonly int token;
			private readonly TaskCompletionSource<XElement> source;

			public ReturnWait(int token, CancellationToken cancellationToken)
			{
				this.token = token;
				this.source = new TaskCompletionSource<XElement>();
				cancellationToken.Register(source.SetCanceled);
			} // ctor

			public int Token => token;
			public TaskCompletionSource<XElement> Source => source;
		} // class ReturnWait

		#endregion

		private readonly Uri serverUri;
		private readonly Random random = new Random(Environment.TickCount);

		private readonly CancellationTokenSource sessionDisposeSource;
		private bool isDisposing = false;

		private string currentUsePath = null;
		private int currentUseToken = -1;

		private int defaultTimeout = 0;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public ClientDebugSession(Uri serverUri)
		{
			var scheme = serverUri.Scheme;
			if (scheme == "http" || scheme == "https") // rewrite uri
				this.serverUri = new Uri((scheme == "https" ? "wss" : "ws") + "://" + serverUri.Host + ":" + serverUri.Port + "/" + serverUri.AbsolutePath);
			else // use uri
				this.serverUri = serverUri;

			this.sessionDisposeSource = new CancellationTokenSource();
		} // ctor

		public void Dispose()
		{
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				isDisposing = true;

				lock (socketLock)
				{
					if (clientSocket != null && clientSocket.State == WebSocketState.Open)
						Task.Run(() => clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None)).Wait();
				}

				if (!sessionDisposeSource.IsCancellationRequested)
					sessionDisposeSource.Cancel();
				sessionDisposeSource.Dispose();
			}
		} // proc Dispose
		
		#endregion

		#region -- Communication --------------------------------------------------------

		private readonly object socketLock = new object();
		private ClientWebSocket clientSocket = null;
		private CancellationToken currentConnectionToken = CancellationToken.None;
		private readonly Dictionary<int, ReturnWait> waits = new Dictionary<int, ReturnWait>();

		/// <summary>Main loop for the debug session, that runs the protocol handlers.</summary>
		/// <returns></returns>
		public async Task RunProtocolAsync()
		{
			var recvOffset = 0;
			var recvBuffer = new byte[1 << 20];
			var lastNativeErrorCode = Int32.MinValue;
			var currentConnectionTokenSource = (CancellationTokenSource)null;
			var sessionDisposeToken = sessionDisposeSource.Token;

			while (!isDisposing && !sessionDisposeToken.IsCancellationRequested)
			{
				var connectionEstablished = false;

				// create the connection
				var socket = new ClientWebSocket();
				socket.Options.Credentials = GetCredentials();
				socket.Options.SetRequestHeader("des-multiple-authentifications", "true");
				socket.Options.AddSubProtocol("dedbg");

				#region -- connect --
				try
				{
					await socket.ConnectAsync(serverUri, sessionDisposeToken);
					connectionEstablished = true;
					lock (socketLock)
					{
						clientSocket = socket;

						currentConnectionTokenSource = new CancellationTokenSource();
						currentConnectionToken = currentConnectionTokenSource.Token;
					}
					OnConnectionEstablished();
				}
				catch (WebSocketException e)
				{
					if (lastNativeErrorCode != e.NativeErrorCode) // connect exception
					{
						if (!OnConnectionFailure(e))
							lastNativeErrorCode = e.NativeErrorCode;
					}
				}
				catch (TaskCanceledException)
				{
				}
				catch (Exception e)
				{
					lastNativeErrorCode = Int32.MinValue;
					OnConnectionFailure(e);
				}
				#endregion

				try
				{
					// reconnect set use
					if (socket.State == WebSocketState.Open)
					{
						if (currentUsePath != null && currentUsePath != "/" && currentUsePath.Length > 0)
							currentUseToken = (int)await SendAsync(socket, GetUseMessage(currentUsePath), false, sessionDisposeToken);
						else
							CurrentUsePath = "/";
					}

					// wait for answers
					recvOffset = 0;
					while (socket.State == WebSocketState.Open 
						&& !sessionDisposeToken.IsCancellationRequested)
					{
						// check if the buffer is large enough
						var recvRest = recvBuffer.Length - recvOffset;
						if (recvRest == 0)
						{
							await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message to big.", sessionDisposeToken);
							break;
						}

						// receive the characters
						var r = await socket.ReceiveAsync(new ArraySegment<byte>(recvBuffer, recvOffset, recvRest), sessionDisposeToken);
						if (r.MessageType == WebSocketMessageType.Text)
						{
							recvOffset += r.Count;
							if (r.EndOfMessage)
							{
								try
								{
									var xAnswer = XElement.Parse(Encoding.UTF8.GetString(recvBuffer, 0, recvOffset));
									ProcessAnswer(xAnswer);
								}
								catch (Exception e)
								{
									lastNativeErrorCode = Int32.MinValue;
									OnCommunicationException(e);
								}
								recvOffset = 0;
							}
						}
					} // message loop
				}
				catch (WebSocketException e)
				{
					if (!isDisposing)
					{
						lastNativeErrorCode = e.NativeErrorCode;
						OnCommunicationException(e);
					}
				}
				catch (TaskCanceledException)
				{
				}

				// close connection
				if (!isDisposing && connectionEstablished)
					OnConnectionLost();

				lock (socketLock)
				{
					clientSocket = null;

					// dispose current cancellation token
					if (currentConnectionTokenSource != null)
					{
						try { currentConnectionTokenSource.Cancel(); }
						catch { }
						currentConnectionTokenSource.Dispose();
						currentConnectionTokenSource = null;
					}

					currentConnectionToken = CancellationToken.None;
				}
				socket.Dispose();
			}
		} // func ConnectionProcessor

		protected virtual ICredentials GetCredentials()
			=> null;

		private void ProcessAnswer(XElement x)
		{
			var token = x.GetAttribute("token", 0);
			DebugPrint($"[Client] Receive Message: {token}");
			if (token != 0) // answer
			{
				if (currentUseToken == token) // was the use command successful
				{
					if (x.Name == "exception")
						CurrentUsePath = "/";
					else
						CurrentUsePath = GetUsePathFromReturn(x);
					currentUseToken = -1;
				}
				else // other messages
				{
					var w = GetWait(token);
					if (w != null)
					{
						if (x.Name == "exception")
							ThreadPool.QueueUserWorkItem(s => w.Source.SetException(new ClientDebugException(x)), null);
						else
							ThreadPool.QueueUserWorkItem(s => w.Source.SetResult(x), null);
					}
				}
			}
			else // notify
			{
				if (x.Name == "log")
				{
					var t = x.Attribute("type")?.Value;
					OnMessage(String.IsNullOrEmpty(t) ? 'D' : Char.ToUpper(t[0]), x.Value);
				}
			}
		} // proc ProcessAnswer

		private ReturnWait GetWait(int token)
		{
			lock (waits)
			{
				if (waits.TryGetValue(token, out var w))
				{
					waits.Remove(token);
					return w;
				}
				else
					return null;
			}
		} // func GetWait

		private TaskCompletionSource<XElement> RegisterAnswer(int token, CancellationToken cancellationToken)
		{
			var w = new ReturnWait(token, cancellationToken);
			lock (waits)
				waits[token] = w;
			return w.Source;
		} // proc RegisterAnswer

		private void UnregisterAnswer(int token)
		{
			lock (waits)
				waits.Remove(token);
		} // proc UnregisterAnswer

		public Task<XElement> SendAsync(XElement xMessage)
			=> SendAsync(xMessage, currentConnectionToken);

		public async Task<XElement> SendAsync(XElement xMessage, CancellationToken cancellationToken)
		{
			ClientWebSocket sendSocket;
			lock (socketLock)
			{
				if (clientSocket == null || clientSocket.State != WebSocketState.Open)
					throw new ArgumentException("Debugsession is disconnected.");

				sendSocket = clientSocket;
			}

			// send and wait for answer
			CancellationTokenSource cancellationTokenSource = null;
			if (cancellationToken == CancellationToken.None || cancellationToken == currentConnectionToken)
			{
				if (defaultTimeout > 0)
				{
					cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(currentConnectionToken);
					cancellationTokenSource.CancelAfter(defaultTimeout);

					cancellationToken = cancellationTokenSource.Token;
				}
				else
				{
					cancellationTokenSource = null;
					cancellationToken = currentConnectionToken;
				}
			}
			else
			{
				cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(currentConnectionToken, cancellationToken);
				cancellationToken = cancellationTokenSource.Token;
			}

			var completionSource = (TaskCompletionSource<XElement>)await SendAsync(sendSocket, xMessage, true, cancellationToken);
			if (cancellationTokenSource == null)
				return await completionSource.Task;
			else
			{
				return await completionSource.Task.ContinueWith(
					t =>
					{
						cancellationTokenSource.Dispose();
						return t.Result;
					}
				);
			}
		} // proc Send

		private async Task<object> SendAsync(ClientWebSocket sendSocket, XElement xMessage, bool registerAnswer, CancellationToken cancellationToken)
		{
			// add token for the answer
			var token = random.Next(1, Int32.MaxValue);
			xMessage.SetAttributeValue("token", token);
			var cancellationSource = (TaskCompletionSource<XElement>)null;

			if (registerAnswer)
				cancellationSource = RegisterAnswer(token, cancellationToken);

			// send message to server
			try
			{
				var messageBytes = Encoding.UTF8.GetBytes(xMessage.ToString(SaveOptions.None));
				Debug.Print("[Client] Send Message: {0}", token);
				await sendSocket.SendAsync(new ArraySegment<byte>(messageBytes, 0, messageBytes.Length), WebSocketMessageType.Text, true, cancellationToken);
			}
			catch
			{
				UnregisterAnswer(token);
				throw;
			}

			return (object)cancellationSource ?? token;
		} // proc SendAsync

		protected virtual void OnConnectionLost()
			=> DebugPrint("Connection lost.");

		protected virtual void OnConnectionEstablished()
			=> DebugPrint("Connection established.");

		protected virtual bool OnConnectionFailure(Exception e)
		{
			DebugPrint($"Connection failed: {e}");
			return false;
		} // proc OnConnectionFailure

		protected virtual void OnCommunicationException(Exception e)
			=> DebugPrint($"Connection failed: {e}");
		
		protected virtual void DebugPrint(string message)
			=> Debug.Print(message);

		protected abstract void OnMessage(char type, string message);

		public bool IsConnected
		{
			get
			{
				lock (socketLock)
					return clientSocket != null && clientSocket.State == WebSocketState.Open;
			}
		} // prop IsConnected

		#endregion

		#region -- GetMemberValue, ParseReturn --------------------------------------------

		private Type GetType(string typeString)
		{
			var lastIndex = typeString.LastIndexOfAny(new char[] { ']', ',' });
			if (lastIndex == -1 || typeString[lastIndex] == ']')
				return LuaType.GetType(typeString);
			else
			{
				return Type.GetType(typeString,
					name =>
					{
						// do not load new assemblies
						var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(c => c.FullName == name.FullName);
						if (asm == null)
							throw new TypeLoadException("Assembly is not loaded.");
						return asm;
					},
					(asm, name, ignorecase) => LuaType.GetType(typeString).Type,
					false
				);
			}
		} // func GetType

		public ClientMemberValue GetMemberValue(XElement x, int index)
		{
			// get the member name or index
			var member = x.GetAttribute("n", String.Empty);
			if (String.IsNullOrEmpty(member))
				member = "$" + x.GetAttribute("i", index).ToString();

			// get type
			var typeString = x.GetAttribute("t", "object");
			var contentType = x.GetAttribute("ct", typeString);
			var type = typeString == "table" ? null : GetType(typeString);

			// check if the value is convertible (only convert core types)
			object value;
			if (contentType == "table" || contentType == "row") // table, row
				value = ParseReturn(x, 1).ToArray();
			else if (contentType == "rows")
			{
				var xFields = x.Element("f");
				if (xFields != null)
				{
					var j = 0;
					var columns = ( // elementName, fieldName, typeString, type
						from xField in xFields.Elements()
						let n = xField.GetAttribute("n", (j++).ToString())
						let t = xField.GetAttribute("t", "object")
						select new Tuple<string, string, string, Type>(xField.Name.LocalName, n, t, GetType(t))
					).ToArray();

					var rows = new List<ClientMemberValue[]>();
					foreach (var xRow in x.Elements("r"))
					{
						var values = new ClientMemberValue[columns.Length];

						for (var i = 0; i < columns.Length; i++)
						{
							var col = columns[i];
							var xValue = xRow.Element(col.Item1);
							values[i] = new ClientMemberValue(col.Item2, col.Item3, col.Item4, xValue == null || xValue.IsEmpty ? null : xValue.Value);
						}

						rows.Add(values);
					}

					value = rows.ToArray();
				}
				else
					value = null;
			}
			else
				value = x.IsEmpty ? null : x.Value;

			return new ClientMemberValue(member, typeString, type, value);
		} // func GetMemberValue

		private IEnumerable<ClientMemberValue> ParseReturn(XElement r, int index = 0)
			 => from c in r.Elements("v")
					select GetMemberValue(c, index++);

		#endregion

		#region -- Use --------------------------------------------------------------------

		public Task<string> SendUseAsync(string node)
			=> SendUseAsync(node, CancellationToken.None);

		public async Task<string> SendUseAsync(string node, CancellationToken cancellationToken)
		{
			// send the use command
			var r = await SendAsync(
				GetUseMessage(node),
				cancellationToken
			);

			// get the new use path
			CurrentUsePath = GetUsePathFromReturn(r);
			return CurrentUsePath;
		} // proc SendUseAsync

		private static XElement GetUseMessage(string node)
			=> new XElement("use", new XAttribute("node", node));

		private static string GetUsePathFromReturn(XElement r)
			=> r.GetAttribute("node", "/");

		protected virtual void OnCurrentUsePathChanged()
			=> CurrentUsePathChanged?.Invoke(this, EventArgs.Empty);

		public string CurrentUsePath
		{
			get { return currentUsePath; }
			private set
			{
				if (currentUsePath != value)
				{
					currentUsePath = value;
					OnCurrentUsePathChanged();
				}
			}
		} // prop CurrentUsePath

		#endregion

		#region -- Execute ----------------------------------------------------------------

		public Task<IEnumerable<ClientMemberValue>> SendExecuteAsync(string command)
			=> SendExecuteAsync(command, CancellationToken.None);

		public async Task<IEnumerable<ClientMemberValue>> SendExecuteAsync(string command, CancellationToken cancellationToken)
		{
			var r = await SendAsync(
				new XElement("execute",
					new XText(command)
				),
				cancellationToken
			);

			return ParseReturn(r);
		} // proc SendCommandAsync

		#endregion

		#region -- GlobalVars -------------------------------------------------------------

		public Task<IEnumerable<ClientMemberValue>> SendMembersAsync(string memberPath)
			=> SendMembersAsync(memberPath, CancellationToken.None);

		public async Task<IEnumerable<ClientMemberValue>> SendMembersAsync(string memberPath, CancellationToken cancellationToken)
			=> ParseReturn(await SendAsync(new XElement("member"), cancellationToken));

		#endregion

		#region -- List -------------------------------------------------------------------

		public Task<XElement> SendListAsync(bool recursive)
			=> SendListAsync(recursive, CancellationToken.None);

		public async Task<XElement> SendListAsync(bool recursive, CancellationToken cancellationToken)
			=> await SendAsync(new XElement("list", new XAttribute("r", recursive)), cancellationToken);

		#endregion

		#region -- Scope ------------------------------------------------------------------

		private string GetScopeUserName(XElement x)
			=> x.GetAttribute<string>("user", "none");

		public Task<string> SendBeginScopeAsync()
			=> SendBeginScopeAsync(CancellationToken.None);

		public async Task<string> SendBeginScopeAsync(CancellationToken cancellationToken)
			=> GetScopeUserName(await SendAsync(new XElement("scopeBegin")));

		public Task<string> SendRollbackScopeAsync()
			=> SendRollbackScopeAsync(CancellationToken.None);

		public async Task<string> SendRollbackScopeAsync(CancellationToken cancellationToken)
			=> GetScopeUserName(await SendAsync(new XElement("scopeRollback")));

		public Task<string> SendCommitScopeAsync()
			=> SendCommitScopeAsync(CancellationToken.None);

		public async Task<string> SendCommitScopeAsync(CancellationToken cancellationToken)
			=> GetScopeUserName(await SendAsync(new XElement("scopeCommit")));

		#endregion

		public int DefaultTimeout
		{
			get { return defaultTimeout; }
			set { defaultTimeout = value < 0 ? 0 : value; }
		} // prop DefaultTimeout
	} // class ClientDebugSession

	#endregion
}
