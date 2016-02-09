using System;
using System.Globalization;
using System.Text;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server.Stuff
{
	#region -- struct CronBound ---------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Descripes time boundaries</summary>
	/// <example>2:10 jeden Tag 2:10
	/// 0 zu jeder Stunde
	/// 10 zu jeder 10. Minute jeder stunde
	/// 1 0:0 zum 1. jeden Monat um 0uhr
	/// 15 0 zum 15. jeden Monat 0uhr
	/// Mo 2:20 jeden Montag um 2:20
	/// Mo,Mi,1,17 2:20 zu jedem Montag bzw. Mittwoch oder zu jedem 1. und 17. um 2:20
	/// 5,10,15 2,14:30 am 5,10,15 jedes Monats einmal um 2:30 uhr und einaml 14:30
	/// 0,10,20,30,40,50 aller 10min
	/// 10,* aller 10min
	/// </example>
	[Serializable]
	public struct CronBound : IFormattable, IEquatable<CronBound>
	{
		private byte[] days;
		private byte[] weekDays;
		private byte[] hours;
		private byte[] minutes;

		#region -- Ctor -------------------------------------------------------------------

		/// <summary>Erstellt aus einer Zeichenkette eine Zeitschranke.</summary>
		/// <param name="value">Beschreibung Zeitschranke</param>
		public CronBound(string value)
			: this(value, null)
		{
		} // ctor

		/// <summary>Erstellt aus einer Zeichenkette eine Zeitschranke.</summary>
		/// <param name="value">Beschreibung Zeitschranke</param>
		/// <param name="formatProvider">Cultur für die Formatierung</param>
		public CronBound(string value, IFormatProvider formatProvider)
		{
			if (String.IsNullOrEmpty(value))
			{
				days = null;
				weekDays = null;
				hours = null;
				minutes = new byte[] { 0 };
			}
			else
			{
				var dtfi = GetDateTimeFormatInfo(formatProvider);
				var timeSepLength = dtfi.TimeSeparator.Length;
				var values = ClearArray(new bool[60]);

				value = value.Trim();

				// Tage oder Wochentage
				var daysSep = value.IndexOf(' ');
				if (daysSep >= 0)
				{
					var values2 = ClearArray(new bool[dtfi.ShortestDayNames.Length]);

					// Create a bit mask for the weekdays
					foreach (var segment in value.Substring(0, daysSep).Split(','))
					{
						int index;
						if (Int32.TryParse(segment, out index))
						{
							if (index >= 0 && index < values.Length)
								values[index] = true;
						}
						else
						{
							index = Array.FindIndex<string>(dtfi.ShortestDayNames, cur => String.Compare(cur, segment, true) == 0);
							if (index >= 0 && index < values2.Length)
								values2[index] = true;
						}
					}
					days = GetValueArray(values, 1, 31);
					weekDays = GetValueArray(values2, 0, values2.Length - 1);
				}
				else
				{
					days = null;
					weekDays = null;
				}

				// Stunden
				var hoursSep = value.IndexOf(dtfi.TimeSeparator, daysSep + 1);
				if (hoursSep >= 0)
				{
					ClearArray(values);
					SplitValues(value.Substring(daysSep + 1, hoursSep - (daysSep + 1)), values, 24);
					hours = GetValueArray(values, 0, 23);
				}
				else
				{
					hours = null;
					hoursSep = daysSep;
				}

				// Minuten
				if (value.Length - (hoursSep + timeSepLength) > 0)
				{
					ClearArray(values);
					SplitValues(value.Substring(hoursSep + timeSepLength), values, 60);
					minutes = GetValueArray(values, 0, 59);
				}
				else
					minutes = new byte[] { 0 };
			}
		} // ctor

		private static byte[] GetValueArray(bool[] values, int min, int max)
		{
			var count = CountTrue(values, min, max);
			if (count == 0)
				return null;
			else
			{
				var ret = new byte[count];
				var offset = 0;
				for (int i = min; i <= max; i++)
				{
					if (values[i])
						ret[offset++] = (byte)i;
				}
				return ret;
			}
		} // proc GetValueArray

		private static void SplitValues(string values, bool[] result, int maxValueRow)
		{
			var lastIndex = -1;
			var lastLastIndex = -1;
			foreach (var cur in values.Split(','))
			{
				int index;

				if (cur == "*")
				{
					var startAt = 0;
					var step = 1;
					if (lastIndex >= 0)
						startAt = lastIndex;
					if (lastLastIndex >= 0)
					{
						step = lastIndex - lastLastIndex;
#if DEBUG
						if (startAt < 1)
							throw new InvalidOperationException();
#endif
					}

					for (int i = startAt; i < maxValueRow; i += step)
						result[i] = true;
				}
				else if (int.TryParse(cur, out index))
				{
					if (index >= 0 && index < result.Length)
					{
						result[index] = true;
						lastLastIndex = lastIndex;
						lastIndex = index;
					}
				}
			}
		} // proc SplitValues

		private static bool[] ClearArray(bool[] array)
		{
			for (var i = 0; i < array.Length; i++)
				array[i] = false;
			return array;
		} // func ClearArray

		private static int CountTrue(bool[] array, int min, int max)
		{
			var count = 0;
			for (var i = min; i <= max; i++)
			{
				if (array[i])
					count++;
			}
			return count;
		} // func CountTrue

		private static DateTimeFormatInfo GetDateTimeFormatInfo(IFormatProvider formatProvider)
		{
			DateTimeFormatInfo dtfi = null;
			if (formatProvider != null)
				dtfi = formatProvider.GetFormat(typeof(DateTimeFormatInfo)) as DateTimeFormatInfo;
			if (dtfi == null)
				dtfi = CultureInfo.CurrentCulture.DateTimeFormat;
			return dtfi;
		} // func GetDateTimeFormatInfo

		#endregion

		#region -- IEquatable Member ------------------------------------------------------

		/// <summary>Vergleicht die zwei Schranken miteinander.</summary>
		/// <param name="other">Vergleichswert</param>
		/// <returns><c>true</c>, wenn die Schranken exakt gleich sind.</returns>
		public bool Equals(CronBound other)
		{
			return CompareTo(ref other);
		} // func Equals

		/// <summary>Vergleicht die zwei Schranken miteinander.</summary>
		/// <param name="obj">Vergleichswert. Es können Zeichenfolgen und andere Schranken (<c>CronBound</c>'s) übergeben werden.</param>
		/// <returns><c>true</c>, wenn die Schranken exakt gleich sind.</returns>
		public override bool Equals(object obj)
		{
			if (obj is CronBound)
			{
				var o = (CronBound)obj;
				return CompareTo(ref o);
			}
			else if (obj is string)
			{
				CronBound o = new CronBound((string)obj);
				return CompareTo(ref o);
			}
			else
				return base.Equals(obj);
		} // func Equals

		/// <summary>Ermittelt den Hash-Wert für diese Schranke.</summary>
		/// <returns>Hashwert</returns>
		public override int GetHashCode()
		{
			var ret = 0;
			SumArray(days, ref ret);
			SumArray(weekDays, ref ret);
			SumArray(hours, ref ret);
			SumArray(minutes, ref ret);
			return ret;
		} // func GetHashCode

		private static void SumArray(byte[] array, ref int sum)
		{
			if (array != null)
			{
				sum++;
				for (int i = 0; i < array.Length; i++)
					sum += array[i];
			}
		} // proc SumArray

		/// <summary>Vergleicht die zwei Schranken miteinander.</summary>
		/// <param name="o">Vergleichswert</param>
		/// <returns><c>true</c>, wenn die Schranken exakt gleich sind.</returns>
		public bool CompareTo(ref CronBound o)
		{
			return Procs.CompareBytes(days, o.days) &&
				Procs.CompareBytes(weekDays, o.weekDays) &&
				Procs.CompareBytes(hours, o.hours) &&
				Procs.CompareBytes(minutes, o.minutes);
		} // func CompareTo

		#endregion

		#region -- ToString ---------------------------------------------------------------

		/// <summary>Wandelt die Zeitschranke wieder in eine Zeichenkette um.</summary>
		/// <returns>Zeitschranke als lesbare Zeichenkette.</returns>
		public override string ToString()
		{
			return ToString(null, CultureInfo.CurrentCulture);
		} // func ToString

		/// <summary>Wandelt die Zeitschranke wieder in eine Zeichenkette um.</summary>
		/// <param name="format">Derzeit ohne Bedeutung. Es sollte <c>null</c> übergeben werden.</param>
		/// <param name="formatProvider">Gibt die Kultur</param>
		/// <returns>Zeitschranke als lesbare Zeichenkette.</returns>
		public string ToString(string format, IFormatProvider formatProvider)
		{
			var dtfi = GetDateTimeFormatInfo(formatProvider);
			var sb = new StringBuilder();
			if (days != null)
				for (int i = 0; i < days.Length; i++)
				{
					if (sb.Length > 0)
						sb.Append(',');
					sb.Append(days[i]);
				}
			if (weekDays != null)
				for (int i = 0; i < weekDays.Length; i++)
				{
					if (sb.Length > 0)
						sb.Append(',');
					sb.Append(dtfi.ShortestDayNames[weekDays[i]]);
				}

			if (sb.Length > 0)
				sb.Append(' ');

			if (hours != null)
			{
				for (int i = 0; i < hours.Length; i++)
				{
					if (i > 0)
						sb.Append(',');
					sb.Append(hours[i].ToString("00"));
				}
				sb.Append(dtfi.TimeSeparator);
			}

			if (minutes != null)
				for (int i = 0; i < minutes.Length; i++)
				{
					if (i > 0)
						sb.Append(',');
					sb.Append(minutes[i].ToString("00"));
				}

			return sb.ToString();
		} // func ToString

		#endregion

		#region -- GetNext ----------------------------------------------------------------

		private static byte GetFollowValue(byte[] values, byte current, bool equalAllowed, out bool overflow)
		{
			overflow = false;
			for (int i = 0; i < values.Length; i++)
			{
				if (equalAllowed)
				{
					if (current <= values[i])
						return values[i];
				}
				else
				{
					if (current < values[i])
						return values[i];
				}
			}
			overflow = true;
			return values[0];
		} // func GetFollowValue

		/// <summary>Ermittelt das nächste Datum, welches durch diese Zeitschranke beschrieben wird.</summary>
		/// <param name="lastTime">Datum von dem aus ausgegangen werden soll.</param>
		/// <returns>Das neue Datum, welches größer ist als das Übergebene.</returns>
		public DateTime GetNext(DateTime lastTime)
		{
			var ret = new DateTime(lastTime.Year, lastTime.Month, lastTime.Day, lastTime.Hour, lastTime.Minute, 0); // Sekunden und Fragmente interessieren nicht

			var changed = false;
			var overflow = false;
			
			// first check the minues
			if (minutes != null)
			{
				var cur = (byte)ret.Minute;
				var next = GetFollowValue(minutes, cur, false, out overflow);
				if (overflow)
					next += 60;
				if (cur < next)
				{
					ret = ret.AddMinutes(next - cur);
					changed = true;
				}
			}

			// next check the hours
			if (hours != null)
			{
				var cur = (byte)ret.Hour;
				var next = GetFollowValue(hours, cur, changed, out overflow);
				if (overflow)
					next += 24;
				if (cur < next)
				{
					ret = ret.AddHours(next - cur)
						.AddMinutes((minutes?[0] ?? 0) - ret.Minute); // next hour start
					changed = true;
				}
			}

			if (days != null || weekDays != null)
			{
				var overflowDay = false;
				DateTime? nextDay = null;

				// next check the days
				if (days != null)
				{
					var cur = (byte)ret.Day;

					var next = GetFollowValue(days, cur, changed, out overflow);
					var daysOfMonth = DateTime.DaysInMonth(ret.Year, ret.Month);
					if (next > daysOfMonth && !overflow) // overflow if the month has less than 31 days, and we use days before
					{
						next = days[0];
						if (next > daysOfMonth) // only a date after the end is given, use the last date of the month
							next = (byte)daysOfMonth;
						else
							overflow = true; // overflow to the next month
					}

					if (overflow)
					{
						var y = ret.Year;
						var m = ret.Month + 1;

						if (m > 12)
						{
							y++;
							m = 1;
						}

						nextDay = new DateTime(y, m, Math.Min(next, DateTime.DaysInMonth(y, m)));
						overflowDay = true;
					}
					else if (cur < next)
					{
						nextDay = new DateTime(ret.Year, ret.Month, next);
						overflowDay = true;
					}
					else
						nextDay = ret;
				}

				// next chect the weekdays
				if (weekDays != null)
				{
					var cur = (byte)ret.DayOfWeek;
					var next = GetFollowValue(weekDays, cur, changed, out overflow);
					if (overflow)
						next += 7;

					var overflowWeekDay = false;
					var nextWeekDay = ret;
					if (cur < next)
					{
						nextWeekDay = ret.AddDays(next - cur);
						overflowWeekDay = true;
					}

					if (!nextDay.HasValue || nextWeekDay < nextDay) // use earlier date
					{
						overflowDay = overflowWeekDay;
						nextDay = nextWeekDay;
					}
				}

				// reset the ret value, to the overflow
				if (nextDay.HasValue && overflowDay)
				{
					ret = nextDay.Value
						.AddHours(hours?[0] ?? 0)
						.AddMinutes(minutes?[0] ?? 0);

					changed = true;
				}
			}


			if (!changed) // Nix passiert, also einfach einen Tag dazu
				ret = ret.AddDays(1);

			if (ret <= lastTime)
				throw new InvalidOperationException("Logic error!");

			return ret;
		} // func GetNext

		#endregion

		/// <summary>Ist die Schranke Leer.</summary>
		public bool IsEmpty { get { return days == null && weekDays == null && hours == null && minutes == null; } }

		private static CronBound empty = new CronBound();

		/// <summary>Gibt eine leere Schranke zurück.</summary>
		public static CronBound Empty { get { return empty; } }
	} // struct CronBound

	#endregion
}
