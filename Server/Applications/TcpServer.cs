using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Is a service thats provides a asychron handling of tcp streams.</summary>
	internal sealed class TcpServer : DEConfigLogItem, IServerTcp
	{
		#region -- class ListenerTcp ------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Implementation of a lisener.</summary>
		private sealed class ListenerTcp : IListenerTcp, IDisposable
		{
			private readonly TcpServer server;
			private readonly Socket s;

			private readonly Action<Stream> createHandler;

			public ListenerTcp(TcpServer server, IPEndPoint endPoint, Action<Stream> createHandler)
			{
				if (createHandler == null)
					throw new ArgumentNullException("createHandler");

				this.server = server;
				this.createHandler = createHandler;

				// create the socket
				s = new Socket(endPoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				// bind socket
				s.Bind(endPoint);
				s.Listen(100);

				// start accepting connections
				BeginAccept(null);
			} // ctor

			public void Dispose()
			{
				try
				{
					s.Close();
				}
				catch { }
			} // proc Dispose

			private void BeginAccept(SocketAsyncEventArgs e)
			{
				if (e == null)
				{
					e = new SocketAsyncEventArgs();
					e.Completed += (sender, _e) => BeginAccept(_e);
				}
				else if (e.SocketError == SocketError.Success) // socket accepted
				{
					var sNew = e.AcceptSocket;

					// wrap the call, that all kind of methods will work
					internalCreateHanlder.BeginInvoke(createHandler, server.CreateConnection(e.AcceptSocket),
						EndCreateHandler, e.AcceptSocket);

					e.AcceptSocket = null;
				}
				else // error during accept
				{
					if (e.SocketError != SocketError.OperationAborted)
						server.Log.Warn(String.Format("Listen for {0} failed: {1}", FormatEndPoint(s.LocalEndPoint), e.SocketError));
					return;
				}

				if (!s.AcceptAsync(e))
					BeginAccept(e);
			} // proc BeginAccept

			private static readonly Action<Action<Stream>, Stream> internalCreateHanlder = InternalCreateHandler;

			private static void InternalCreateHandler(Action<Stream> createHandler, Stream networkStream)
			{
				createHandler(networkStream);
			} // proc InternalCreateHandler

			private void EndCreateHandler(IAsyncResult ar)
			{
				var socketNew = (Socket)ar.AsyncState;
				try
				{
					internalCreateHanlder.EndInvoke(ar);
				}
				catch (Exception e)
				{
					server.Log.Except(String.Format("Spawn failed for socket '{0}'.", FormatEndPoint(socketNew.RemoteEndPoint)), e);
				}
			} // func EndCreateHandler

			public IPEndPoint LocalEndPoint => s.LocalEndPoint as IPEndPoint;
		} // class ListenerTcp

		#endregion

		#region -- class ConnectionTcp ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ConnectionTcp : Stream
		{
			private readonly TcpServer server;
			private readonly Socket s;

			private readonly Lazy<string> streamInfo;
			
			private long totalReadedBytes = 0;
			private long totalWrittenBytes = 0;

			private bool isDisposed = false;

			#region -- Ctor/Dtor ------------------------------------------------------------

			public ConnectionTcp(TcpServer server, Socket s)
			{
				if (server == null)
					throw new ArgumentNullException("server");
				if (s == null)
					throw new ArgumentNullException("socket");

				this.server = server;
				this.s = s;

				this.streamInfo = new Lazy<string>(
					() =>
					{
						var ip = s.RemoteEndPoint as IPEndPoint;
						if (ip == null)
							return s.RemoteEndPoint?.ToString();
						else if (ip.AddressFamily == AddressFamily.InterNetwork)
							return $"{ip.Address}:{ip.Port}";
						else
							return $"{ip.Address},{ip.Port}";
					});

				server.Log.Info("[{0}] Connection created.", StreamInfo);

				// set timeouts
				s.ReceiveTimeout = 10000;
				s.SendTimeout = 10000;
			} // ctor

			protected override void Dispose(bool disposing)
			{
				if (isDisposed)
					return;

				isDisposed = true;
				if (disposing)
				{
					server.Log.Info("[{0}] Connection closed ({1} bytes received, {2} bytes sent).", StreamInfo, totalReadedBytes, totalWrittenBytes);

					s.Close();
					server.RemoveConnection(this);
				}
				base.Dispose(disposing);
			} // proc Dispose

			public override void Flush() { }

			#endregion

			#region -- Read -----------------------------------------------------------------

			public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
				=> s.BeginReceive(buffer, offset, count, SocketFlags.None, callback, state);

			public override int EndRead(IAsyncResult asyncResult)
			{
				SocketError error;
				var r = s.EndReceive(asyncResult, out error);
				return EndRead(r, error);
			} // func EndRead

			public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				var completion = new TaskCompletionSource<int>();

				// register cancel
				cancellationToken.Register(() => completion.SetCanceled());

				// create async task
				var recvArgs = new SocketAsyncEventArgs();
				recvArgs.UserToken = completion;
				recvArgs.SetBuffer(buffer, offset, count);
				recvArgs.Completed += (sender, e) => EndReadAsync(e);

				if (!s.ReceiveAsync(recvArgs))
					EndReadAsync(recvArgs);

				return completion.Task;
			} // func ReadAsync

			private void EndReadAsync(SocketAsyncEventArgs e)
			{
				var completion = e.UserToken as TaskCompletionSource<int>;
				try
				{
					completion.SetResult(EndRead(e.BytesTransferred, e.SocketError));
				}
				catch (Exception ex)
				{
					completion.SetException(ex);
				}
			} // proc EndReadAsync

			public override int Read(byte[] buffer, int offset, int count)
			{
				SocketError error;
				var r = s.Receive(buffer, offset, count, SocketFlags.None, out error);
				return EndRead(r, error);
			} // func Read

			private int EndRead(int r, SocketError error)
			{
				if (error == SocketError.Success)
				{
					Interlocked.Add(ref totalReadedBytes, r);
					return r;
				}
				else
					return -1;
			} // func EndRead

			#endregion

			#region -- Write ----------------------------------------------------------------

			public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
				=> s.BeginSend(buffer, offset, count, SocketFlags.None, callback, state);

			public override void EndWrite(IAsyncResult asyncResult)
			{
				SocketError error;
				var r = s.EndSend(asyncResult, out error);
				EndWrite(r, error);
			} // proc EndWrite

			public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				var completion = new TaskCompletionSource<object>();

				// register cancel
				cancellationToken.Register(() => completion.SetCanceled());

				// create async send
				var sendArgs = new SocketAsyncEventArgs();
				sendArgs.SetBuffer(buffer, offset, count);
				sendArgs.UserToken = completion;
				sendArgs.Completed += (sender, e) => EndWriteAsync(e);
				if (!s.SendAsync(sendArgs))
					EndWriteAsync(sendArgs);

				return completion.Task;
			} // func WriteAsync

			private void EndWriteAsync(SocketAsyncEventArgs e)
			{
				var completion = e.UserToken as TaskCompletionSource<object>;
				try
				{
					EndWrite(e.BytesTransferred, e.SocketError);
					completion.SetResult(null);
				}
				catch (Exception ex)
				{
					completion.SetException(ex);
				}
			} // proc EndWriteAsync

			public override void Write(byte[] buffer, int offset, int count)
			{
				SocketError error;
				var r = s.Send(buffer, offset, count, SocketFlags.None, out error);
				EndWrite(r, error);
			} // proc Write

			private void EndWrite(int r, SocketError error)
			{
				if (error == SocketError.Success)
					Interlocked.Add(ref totalWrittenBytes, r);
				else
					throw new SocketException((int)error);
			} // proc EndWrite

			#endregion

			public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

			public override void SetLength(long value) { throw new NotSupportedException(); }

			public override bool CanRead => true;
			public override bool CanWrite => true;
			public override bool CanSeek => false;


			public override long Position { get { return totalReadedBytes; } set { throw new NotSupportedException(); } }
			public override long Length { get { return totalReadedBytes; } }

			public string StreamInfo => streamInfo.Value;
		} // class ConnectionTcp

		#endregion

		private DEList<ConnectionTcp> connections;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public TcpServer(IServiceProvider sp, string name)
			: base(sp, name)
		{
			this.connections = new DEList<ConnectionTcp>(this, "tw_connections", "Connections");

			var sc = this.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IServerTcp), this);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				var sc = this.GetService<IServiceContainer>(false);
				sc?.RemoveService(typeof(IServerTcp));
			}

			base.Dispose(disposing);
		} // proc Dispose

		#endregion

		#region -- Create Connection ------------------------------------------------------

		private Stream CreateConnection(Socket sNew)
		{
			var c = new ConnectionTcp(this, sNew);
			connections.Add(c);
			return c;
		} // func CreateConnection

		private void RemoveConnection(ConnectionTcp connection)
		{
			connections.Remove(connection);
		} // proc RemoveConnection

		[LuaMember("RegisterListener")]
		public IListenerTcp RegisterListener(IPEndPoint endPoint, Action<Stream> createHandler)
		{
			return new ListenerTcp(this, endPoint, createHandler);
		} // proc RegisterListener

		public Task<Stream> CreateConnectionAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
		{
			var taskCompletion = new TaskCompletionSource<Stream>();

			// register cancel
			cancellationToken.Register(() => taskCompletion.TrySetCanceled());

			// create the connect
			var e = new SocketAsyncEventArgs();
			e.RemoteEndPoint = endPoint;
			e.UserToken = taskCompletion;
			e.Completed += (sender, _e) => EndConnection(_e);
			if (!Socket.ConnectAsync(SocketType.Stream, ProtocolType.Tcp, e))
				EndConnection(e);

			return taskCompletion.Task;
		} // func CreateConnection

		private void EndConnection(SocketAsyncEventArgs e)
		{
			var taskCompletion = (TaskCompletionSource<Stream>)e.UserToken;
			try
			{
				if (taskCompletion.Task.IsCanceled)
					throw new TaskCanceledException();
        else if (e.SocketError == SocketError.Success)
					taskCompletion.SetResult(CreateConnection(e.ConnectSocket));
				else
					throw new SocketException((int)e.SocketError);
			}
			catch (Exception ex)
			{
				taskCompletion.SetException(ex);
			}
		} // proc ConnectSocket

		public string GetStreamInfo(Stream stream)
		{
			if (stream is ConnectionTcp)
				return ((ConnectionTcp)stream).StreamInfo;
			else
				return null;
		} // func GetStreamInfo

		public Task<IPEndPoint> ResolveEndpointAsync(string dnsOrAddress, int port, CancellationToken cancellationToken)
		{
			IPAddress addr;
			if (IPAddress.TryParse(dnsOrAddress, out addr))
				return Task.FromResult(new IPEndPoint(addr, port));

			return Task.Factory.FromAsync<IPEndPoint>(Dns.BeginGetHostAddresses(dnsOrAddress, null, port), EndResolveEndPoint);
		} // func ResolveEndpointAsync

		private IPEndPoint EndResolveEndPoint(IAsyncResult ar)
		{
			var port = (int)ar.AsyncState;
			var addresses = Dns.EndGetHostAddresses(ar);
			return new IPEndPoint(addresses[0], port);
		} // func EndResolveEndPoint

		private static string FormatEndPoint(EndPoint remoteEndPoint)
		{
			if (remoteEndPoint == null)
				return "<null>";

			var ep = remoteEndPoint as IPEndPoint;
			if (ep != null)
				return ep.Address.ToString();
			else
				return remoteEndPoint?.ToString();
		} // func FormatEndPoint

		#endregion
	} // class TcpServer
}
