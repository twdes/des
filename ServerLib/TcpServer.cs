using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IListenerTcp : IDisposable
	{
		IPEndPoint LocalEndPoint { get; }
	} // interface IListenerTcp
	
	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Interface for the tcp-Server interface.</summary>
	public interface IServerTcp
	{
		/// <summary>Registers a listener for a ip endpoint.</summary>
		/// <param name="endPoint">Endpoint to listen on.</param>
		/// <param name="createHandler">Handle that creates the protocol for the in bound data,</param>
		/// <returns>Registered tcp-Listener</returns>
		IListenerTcp RegisterListener(IPEndPoint endPoint, Action<Stream> createHandler);

		/// <summary></summary>
		/// <param name="endPoint"></param>
		/// <returns></returns>
		Stream Connect(IPEndPoint endPoint); // todo: create handler

		/// <summary>Returns a informational string about the given stream. The stream should be
		/// created from the server.</summary>
		/// <param name="stream"></param>
		/// <returns></returns>
		string GetStreamInfo(Stream stream);
  } // interface IServerTcp
}
