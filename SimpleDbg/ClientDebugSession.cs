using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class ClientMemberValue --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class ClientMemberValue
	{
		internal ClientMemberValue(string name, Type type, object value)
		{
			this.Name = name;
			this.Type = type;
			this.Value = value;
		} // ctor

		public string Name { get; }
		public Type Type { get; }
		public object Value { get; }

		public string TypeAsString
			=> LuaType.GetType(Type).AliasName ?? Type.Name;

		public string ValueAsString
		{
			get
			{
				try
				{
					if (Value == null)
						return "null";
					else if (Type == typeof(string))
						return "'" + Value.ToString() + "'";
					else
						return Procs.ChangeType<string>(Value);
				}
				catch (Exception)
				{
					return Value.ToString();
				}
			}
		} // prop ValueAsString
	} // class ClientMemberValue

	#endregion

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class ClientDebugSession : IDisposable
	{
		#region -- class ReturnWait -------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ReturnWait
		{
			public ReturnWait(int token, CancellationToken cancellationToken)
			{
				this.Token = token;
				this.Source = new TaskCompletionSource<XElement>();
			} // ctor

			public int Token { get; }
			public TaskCompletionSource<XElement> Source { get; }
		} // class ReturnWait

		#endregion

		private readonly Uri serverUri;
		private readonly ClientWebSocket socket = new ClientWebSocket();
		private readonly Random random = new Random(Environment.TickCount);

		private readonly Dictionary<int, ReturnWait> waits = new Dictionary<int, ReturnWait>();
		private readonly CancellationTokenSource sessionDisposeClose;
		private Task backgroundListener;

		public ClientDebugSession(Uri serverUri)
		{
			this.serverUri = serverUri;
			this.sessionDisposeClose = new CancellationTokenSource();
		} // ctor

		public void Dispose()
		{
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				sessionDisposeClose.Cancel();
			}
		} // proc Dispose

		public async Task ConnectAsync()
		{
			// create connection
			socket.Options.AddSubProtocol("dedbg");
			await socket.ConnectAsync(serverUri, CancellationToken.None);

			// background listener
			backgroundListener = Task.Run(BackgroundListener, sessionDisposeClose.Token);
		} // proc Connect

		private async Task BackgroundListener()
		{
			var recvOffset = 0;
			var recvBuffer = new byte[1 << 20];

			while (true)
			{
				var recvRest = recvBuffer.Length - recvOffset;
				if (recvRest == 0)
				{
					throw new OutOfMemoryException(); // todo:
				}

				var r = await socket.ReceiveAsync(new ArraySegment<byte>(recvBuffer, recvOffset, recvRest), sessionDisposeClose.Token);
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
							// todo:
							Debug.Print(e.Message);
						}
						recvOffset = 0;
					}
				}
			}
		} // proc BackgroundListener

		private void ProcessAnswer(XElement x)
		{
			var token = x.GetAttribute("token", 0);
			Debug.Print("[Client] Receive Message: {0}", token);
			if (token != 0) // answer
			{
				var w = GetWait(token);
				if (w != null)
				{
					if (x.Name == "exception")
						Task.Run(() => w.Source.SetException(new Exception(x.GetAttribute("message", String.Empty))), sessionDisposeClose.Token);
					else
						Task.Run(() => w.Source.SetResult(x), sessionDisposeClose.Token);
				}
			}
			else // notify
			{
			}
		} // proc ProcessAnswer

		private ReturnWait GetWait(int token)
		{
			lock (waits)
			{
				ReturnWait w;
				if (waits.TryGetValue(token, out w))
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

		public Task<XElement> SendAsync(XElement cmd)
			=> SendAsync(cmd, sessionDisposeClose.Token);

		public async Task<XElement> SendAsync(XElement xMessage, CancellationToken cancellationToken)
		{
			// add token for the answer
			var token = random.Next(1, Int32.MaxValue);
			xMessage.SetAttributeValue("token", token);

			// send message to server
			var messageBytes = Encoding.UTF8.GetBytes(xMessage.ToString(SaveOptions.None));
			Debug.Print("[Client] Send Message: {0}", token);
			await socket.SendAsync(new ArraySegment<byte>(messageBytes, 0, messageBytes.Length), WebSocketMessageType.Text, true, cancellationToken);

			// wait for answer
			return await RegisterAnswer(token, cancellationToken).Task;
		} // proc Send

		#region -- GetMemberValue, ParseReturn --------------------------------------------

		public ClientMemberValue GetMemberValue(XElement x, int index)
		{
			var member = x.GetAttribute("n", String.Empty);
			if (String.IsNullOrEmpty(member))
				member = "$" + x.GetAttribute("i", index).ToString();

			// convert value 
			var type = LuaType.GetType(x.GetAttribute("t", "object")).Type ?? typeof(object);
			object value;
			try
			{
				value = Lua.RtConvertValue(x.Value, type);
			}
			catch (Exception)
			{
				value = x.Value;
			}

			return new ClientMemberValue(member, type, value);
		} // func GetMemberValue

		private IEnumerable<ClientMemberValue> ParseReturn(XElement r)
		{
			var i = 0;
			var p = new PropertyDictionary();

			return from c in r.Elements("v")
						 select GetMemberValue(c, i++);
		} // func ParseReturn

		#endregion

		#region -- Use --------------------------------------------------------------------

		public Task<string> SendUseAsync(string node)
			=> SendUseAsync(node, sessionDisposeClose.Token);

		public async Task<string> SendUseAsync(string node, CancellationToken cancellationToken)
		{
			var r = await SendAsync(
				new XElement("use",
					new XAttribute("node", node)
				),
				cancellationToken
			);
			return r.GetAttribute("node", "/");
		} // proc SendUseAsync

		#endregion

		#region -- Execute ----------------------------------------------------------------

		public Task<IEnumerable<ClientMemberValue>> SendExecuteAsync(string command)
			=> SendExecuteAsync(command, sessionDisposeClose.Token);

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
			=> SendMembersAsync(memberPath, sessionDisposeClose.Token);

		public async Task<IEnumerable<ClientMemberValue>> SendMembersAsync(string memberPath, CancellationToken cancellationToken)
		{
			var r = await SendAsync(
				new XElement("member")
			);

			return ParseReturn(r);
		} // proc SendCommandAsync

		#endregion
	} // class ClientDebugSession
}
