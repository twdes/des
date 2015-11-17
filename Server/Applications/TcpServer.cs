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
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Is a service thats provides a asychron handling of tcp streams.</summary>
	internal sealed class TcpServer : DEConfigLogItem, IServerTcp
	{
		#region -- class ListenerTcp ------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
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

				s = new Socket(endPoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				// bind socket
				s.Bind(endPoint);
				s.Listen(100);

				// start accepting connections
				BeginAccept(null);
			} // ctor

			public void Dispose()
			{
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

					createHandler.BeginInvoke(server.CreateConnection(e.AcceptSocket),
						EndCreateHandler, e.AcceptSocket);

					e.AcceptSocket = null;
				}
				else // error during accept
				{
					// todo: error
					return;
				}

				if (!s.AcceptAsync(e))
					BeginAccept(e);
			} // proc BeginAccept

			private void EndCreateHandler(IAsyncResult ar)
			{
				var sNew = (Socket)ar.AsyncState;
				try
				{
					createHandler.EndInvoke(ar);
				}
				catch (Exception e)
				{
					server.Log.Except(String.Format("Spawn failed for socket '{0}'.", FormatEndPoint(sNew.RemoteEndPoint)), e);
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
			private readonly Socket s;

			private long totalReadedBytes = 0;
			private long totalWrittenBytes = 0;

			public ConnectionTcp(Socket s)
			{
				this.s = s;
			} // ctor

			protected override void Dispose(bool disposing)
			{
				if (disposing)
					s.Close();

				base.Dispose(disposing);
			} // proc Dispose

			public override void Flush() { }

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
				//cancellationToken.Register(

				// create async task
				var recvArgs = new SocketAsyncEventArgs();
				recvArgs.UserToken = completion;
				recvArgs.SetBuffer(buffer, offset, count);
				recvArgs.Completed += (sender, e) => EndReadAsync(e);

        if (!s.ReceiveAsync(recvArgs))
					EndReadAsync(recvArgs);

				return completion.Task;
			} // func ReadAsync

			private void EndReadAsync( SocketAsyncEventArgs e)
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
				var completation = new TaskCompletionSource<object>();

				// register cancel

				// create async send
				var sendArgs = new SocketAsyncEventArgs();
				sendArgs.SetBuffer(buffer, offset, count);
				sendArgs.UserToken = completation;
				sendArgs.Completed += (sender, e) => EndWriteAsync(e);
				if (!s.SendAsync(sendArgs))
					EndWriteAsync(sendArgs);

				return completation.Task;
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
					throw new Exception("todo");
			} // proc EndWrite

			#endregion

			public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

			public override void SetLength(long value) { throw new NotSupportedException(); }

			public override bool CanRead => true;
			public override bool CanWrite => true;
			public override bool CanSeek => false;


			public override long Position { get { return totalReadedBytes; } set { throw new NotSupportedException(); } }
			public override long Length { get { return totalReadedBytes; } }
		} // class ConnectionTcp

		#endregion
		
		public TcpServer(IServiceProvider sp, string name)
			: base(sp, name)
		{
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

		private Stream CreateConnection(Socket sNew)
		{
			return new ConnectionTcp(sNew);
		}

		public IListenerTcp RegisterListener(IPEndPoint endPoint, Action<Stream> createHandler)
		{
			return new ListenerTcp(this, endPoint, createHandler);
		} // proc RegisterListener

		public IDisposable RegisterConnection(IPAddress address, ushort port, Action<Stream> createHandler)
		{
			return null; // ConnectionTcp
		} // proc RegisterConnection

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

	} // class TcpServer
}
