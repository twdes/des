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
using Neo.Console;
using System;
using System.Threading.Tasks;
using TecWare.DE.Networking;
using TecWare.DE.Server.UI;

namespace TecWare.DE.Server
{
	#region -- class ConsoleEventSocket -----------------------------------------------

	internal sealed class ConsoleEventSocket : DEHttpEventSocket
	{
		private readonly ConsoleApplication app;

		public ConsoleEventSocket(ConsoleApplication app, DEHttpClient http)
			: base(http)
		{
			this.app = app ?? throw new ArgumentNullException(nameof(app));
		} // ctor

		protected override Task OnConnectionEstablishedAsync()
		{
			Program.SetConnectionState(ConnectionState.ConnectedEvent, true);
			return base.OnConnectionEstablishedAsync();
		} // proc OnConnectionEstablishedAsync

		protected override Task OnConnectionLostAsync()
		{
			Program.SetConnectionState(ConnectionState.ConnectedEvent, false);
			return base.OnConnectionLostAsync();
		} // proc OnConnectionLostAsync

		protected override Task OnCommunicationExceptionAsync(Exception e)
		{
			app.WriteError(e);
			return base.OnCommunicationExceptionAsync(e);
		} // proc OnCommunicationExceptionAsync

		protected override Task<bool> OnConnectionFailureAsync(Exception e)
		{
			app.WriteError(e);
			return base.OnConnectionFailureAsync(e);
		} // func OnConnectionFailureAsync
	} // class ConsoleEventSocket

	#endregion
}
