using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class SimpleServiceProvider : IServiceProvider, ILogger
	{
		public object GetService(Type serviceType)
		{
			if (serviceType.IsAssignableFrom(GetType()))
				return this;
			else
				return null;
		}

		public void LogMsg(LogMsgType typ, string message) => Console.WriteLine($"[{typ}] {message}");
	}
}
