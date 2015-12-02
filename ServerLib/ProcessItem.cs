using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IDEProcessItem
	{
		/// <summary></summary>
		/// <returns></returns>
		bool StartProcess();
		/// <summary></summary>
		void StopProcess();
		/// <summary></summary>
		/// <param name="text"></param>
		void SendCommand(string text);
		/// <summary></summary>
		Process Process { get; }
		bool IsProcessRunning { get; }
	} // interface IDEProcessItem
}
