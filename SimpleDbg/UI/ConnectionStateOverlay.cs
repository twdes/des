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

namespace TecWare.DE.Server.UI
{
	#region -- enum ConnectionState ---------------------------------------------------

	[Flags]
	public enum ConnectionState
	{
		None = 0,
		Connecting = 1,
		ConnectedHttp = 2,
		ConnectedDebug = 4,
		ConnectedEvent = 8
	} // enum ConnectionState

	#endregion

	#region -- class ConnectionStateOverlay -------------------------------------------

	internal sealed class ConnectionStateOverlay : ConsoleOverlay
	{
		private ConnectionState currentState = ConnectionState.None;
		private string currentPath = "/";

		public ConnectionStateOverlay(ConsoleApplication app)
		{
			Application = app ?? throw new ArgumentNullException(nameof(app));
			ZOrder = 1000;

			// create char buffer
			Resize(20, 1);
			Left = -Width;
			Top = 0;

			// position
			Position = ConsoleOverlayPosition.Window;
			OnResize();
		} // ctor

		protected override void OnRender()
		{
			ConsoleColor GetStateColor()
			{
				if ((currentState & ConnectionState.ConnectedDebug) != 0)
					return ConsoleColor.DarkGreen;
				else if ((currentState & ConnectionState.ConnectedHttp) != 0)
					return ConsoleColor.DarkBlue;
				else
					return ConsoleColor.DarkRed;
			} // func GetStateColor

			var stateText = currentState == ConnectionState.None ? "None..." : (currentState == ConnectionState.Connecting ? "Connecting..." : currentPath);
			if (stateText.Length > 18)
				stateText = " ..." + stateText.Substring(stateText.Length - 15, 15) + " ";
			else
				stateText = " " + stateText.PadRight(19);

			Content.Write(0, 0, stateText, null, ConsoleColor.White, GetStateColor());
		} // proc OnRender

		private void SetStateCore(ConnectionState state, bool? set)
		{
			var newState = set.HasValue
				? (set.Value ? state | currentState : ~state & currentState)
				: state;

			if (newState != currentState)
			{
				currentState = newState;
				Invalidate();
			}
		} // proc SetStateCore

		private void SetPathCore(string newPath)
		{
			if (newPath != currentPath)
			{
				currentPath = newPath;
				Invalidate();
			}
		} // proc SetPathCore

		public void SetState(ConnectionState state, bool? set)
			=> Application?.Invoke(() => SetStateCore(state, set));

		public void SetPath(string path)
			=> Application?.Invoke(() => SetPathCore(path));
	} // class ConnectionStateOverlay

	#endregion
}
