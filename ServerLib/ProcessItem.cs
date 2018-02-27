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
using System.Diagnostics;

namespace TecWare.DE.Server
{
	#region -- interface IDEProcessItem -----------------------------------------------

	/// <summary>Process item contract.</summary>
	public interface IDEProcessItem
	{
		/// <summary>Start the process.</summary>
		/// <returns><c>true</c>, if the process was started successful.</returns>
		bool StartProcess();
		/// <summary>Stop the process.</summary>
		void StopProcess();
		/// <summary>Send a command to this process (InputStream is used).</summary>
		/// <param name="text">Line to send</param>
		void SendCommand(string text);
		/// <summary>Access the process information.</summary>
		Process Process { get; }
		/// <summary>Is the current process running.</summary>
		bool IsProcessRunning { get; }
	} // interface IDEProcessItem

	#endregion
}
