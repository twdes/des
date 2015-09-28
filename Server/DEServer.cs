using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal interface IDEServerResolver
	{
		/// <summary></summary>
		/// <param name="path"></param>
		void AddPath(string path);
		/// <summary></summary>
		/// <param name="assemblyName"></param>
		/// <returns></returns>
		Assembly Load(string assemblyName);
	} // interface IDEServerResolver

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal partial class DEServer
	{
		public DEServer(string configurationFile, IEnumerable<string> properties)
		{
		} // ctor

		private void OnStop()
		{
		}

		private void OnStart()
		{
		}
	} // class DEServer
}
