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
using System;
using System.Text;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Stuff
{
	internal static class LogLineParser
	{
		public static void Parse(string dataLine, out LogMsgType typ, out DateTime stamp, out string text)
		{
			if (String.IsNullOrEmpty(dataLine))
				throw new ArgumentNullException(nameof(dataLine));

			var parts = dataLine.Split('\t');

			// Datum Lesen
			stamp = parts.Length > 0 ? ParseDateTime(parts[0]) : DateTime.MinValue;

			// Typ Lesen
			if (parts.Length < 2 || !Int32.TryParse(parts[1], out var t) || t < 0 || t > 3)
				typ = LogMsgType.Error;
			else
				typ = (LogMsgType)t;

			// Text Lesen
			text = parts.Length >= 3 ? parts[2].UnescapeSpecialChars() : String.Empty;
		} // proc Parse

		public static string ConvertDateTime(DateTime value)
			=> value.ToString("yyyy-MM-dd HH:mm:ss:fff");

		public static DateTime ParseDateTime(string value)
		{
			var ret = new DateTime(1900, 1, 1, 0, 0, 0, 0);
			var pos = value.IndexOf(' ');
			if (pos == -1)
				return ret;

			// Datum lesen
			var date = value.Substring(0, pos).Split('-');
			if (date.Length != 3)
				return ret;

			if (!Int32.TryParse(date[0], out var year))
				return ret;
			if (!Int32.TryParse(date[1], out var month))
				return ret;
			if (!Int32.TryParse(date[2], out var day))
				return ret;

			var time = value.Substring(pos + 1).Split(':');
			if (time.Length != 4)
				return ret;

			if (!Int32.TryParse(time[0], out var hour))
				return ret;
			if (!Int32.TryParse(time[1], out var minute))
				return ret;
			if (!Int32.TryParse(time[2], out var second))
				return ret;
			if (!Int32.TryParse(time[3], out var millisecond))
				return ret;

			try
			{
				return new DateTime(year, month, day, hour, minute, second, millisecond);
			}
			catch (ArgumentException)
			{
				return ret;
			}
		} // func ParseDateTime
	} // class LogLineParser
}
