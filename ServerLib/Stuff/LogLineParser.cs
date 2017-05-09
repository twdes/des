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
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal static class LogLineParser
  {
    public static void Parse(string dataLine, out LogMsgType typ, out DateTime stamp, out string text)
    {
      if (String.IsNullOrEmpty(dataLine))
        throw new ArgumentNullException("DataLine");

      var parts = dataLine.Split('\t');

			// Datum Lesen
			stamp = parts.Length > 0 ? ParseDateTime(parts[0]) : DateTime.MinValue;

      // Typ Lesen
      int iTmp;
      if (parts.Length < 2 || !int.TryParse(parts[1], out iTmp) || iTmp < 0 || iTmp > 2)
        typ = LogMsgType.Error;
      else
        typ = (LogMsgType)iTmp;

      // Text Lesen
      if (parts.Length >= 3)
      {
        var sb = new StringBuilder();
        var tmp = parts[2];
        var pos = 0;
        while (pos < tmp.Length)
        {
          char c = tmp[pos];
          switch (c)
          {
            case '\\':
              if (++pos < tmp.Length)
              {
                c = tmp[pos++];
                switch (c)
                {
                  case 't':
                    sb.Append('\t');
                    break;
                  case 'n':
                    sb.AppendLine();
                    break;
                  case '\\':
                    sb.Append('\\');
                    break;
									case '0':
										sb.Append('\0');
										break;
									default:
                    sb.Append(c);
                    break;
                }
              }
              break;
            default:
              sb.Append(c);
              pos++;
              break;
          }
        }
        text = sb.ToString();
      }
      else
        text = "";
    } // proc Parse

		public static string ConvertDateTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss:fff");

    public static DateTime ParseDateTime(string value)
    {
      var ret = new DateTime(1900, 1, 1, 0, 0, 0, 0);
      var pos = value.IndexOf(' ');
      if (pos == -1)
        return ret;

      // Datum lesen
      int year;
      int month;
      int day;
      int hour;
      int minute;
      int second;
      int millisecond;
      var date = value.Substring(0, pos).Split('-');
      if (date.Length != 3)
        return ret;

      if (!int.TryParse(date[0], out year))
        return ret;
      if (!int.TryParse(date[1], out month))
        return ret;
      if (!int.TryParse(date[2], out day))
        return ret;

      var time = value.Substring(pos + 1).Split(':');
      if (time.Length != 4)
        return ret;

      if (!int.TryParse(time[0], out hour))
        return ret;
      if (!int.TryParse(time[1], out minute))
        return ret;
      if (!int.TryParse(time[2], out second))
        return ret;
      if (!int.TryParse(time[3], out millisecond))
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
