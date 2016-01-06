using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class SimpleServiceProvider : IServiceProvider, ILogger
	{
		private Dictionary<Type, object> services = new Dictionary<Type, object>();

		public object GetService(Type serviceType)
		{
			object v;
			if (serviceType.IsAssignableFrom(GetType()))
				return this;
			else if (services.TryGetValue(serviceType, out v))
				return v;
			else
				return null;
		}

		public void LogMsg(LogMsgType typ, string message) => Console.WriteLine($"[{typ}] {message}");

		public void Add(Type type, object service)
		{
			services[type] = service;
		}
	}
}
