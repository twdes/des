using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TecWare.DE.Server
{
	#region -- class DEConfigurationException -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class DEConfigurationException : Exception
	{
		private string sourceUri;
		private int lineNumber;
		private int linePosition;

		public DEConfigurationException(XObject x, string message, Exception innerException = null)
			: base(message, innerException)
		{
			this.sourceUri = x.BaseUri;

			var lineInfo = (IXmlLineInfo)x;
			if (lineInfo.HasLineInfo())
			{
				this.lineNumber = lineInfo.LineNumber;
				this.linePosition = lineInfo.LinePosition;
			}
			else
			{
				this.lineNumber = -1;
				this.linePosition = -1;
			}
		} // ctor

		public DEConfigurationException(XmlSchemaObject x, string message, Exception innerException = null)
		{
			this.sourceUri = x.SourceUri;
			this.lineNumber = x.LineNumber;
			this.linePosition = x.LinePosition;
		} // ctor

		/// <summary>Position an der der Fehler entdeckt wurde</summary>
		public string PositionText
		{
			get
			{
				var sb = new StringBuilder();
				if (String.IsNullOrEmpty(sourceUri))
					sb.Append("<unknown>");
				else
					sb.Append(sourceUri);

				if (lineNumber >= 0)
				{
					sb.Append(" (")
						.Append(lineNumber.ToString("N0"));
					if (linePosition >= 0)
					{
						sb.Append(',')
							.Append(linePosition.ToString("N0"));
					}
					sb.Append(')');
				}

				return sb.ToString();
			}
		} // prop ConfigFileName

		/// <summary></summary>
		public string SourceUri => sourceUri;
		/// <summary></summary>
		public int LineNumber => lineNumber;
		/// <summary></summary>
		public int LinePosition => linePosition;
	} // class DEConfigurationException

	#endregion

	public partial class DEConfigItem
	{
	} // class DEConfigItem
}
