using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TecWare.DE.Stuff
{
	public static partial class ProcsDE
	{
		private static Regex environmentSyntax = new Regex(@"\%(\w+)\%", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

		public static string GetLocalPath(string path)
		{
			if (path == null)
				return null;

			if (path.StartsWith("file://"))
				path = new Uri(path).LocalPath;
			else
				path = path.Replace('/', Path.DirectorySeparatorChar);
			return path;
		} // func GetLocalPath

		public static string GetEnvironmentPath(string filename)
		{
			// resolve environment
			return environmentSyntax.Replace(filename,
				m =>
				{
					var variableName = m.Groups[1].Value;
					var value = String.Empty;

					if (String.Compare(variableName, "currentdirectory", StringComparison.OrdinalIgnoreCase) == 0)
						value = Environment.CurrentDirectory;
					else if (String.Compare(variableName, "executedirectory", StringComparison.OrdinalIgnoreCase) == 0)
						value = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
					else if (String.Compare(variableName, "temp", StringComparison.OrdinalIgnoreCase) == 0)
						value = Path.GetTempPath();
					else if (String.Compare(variableName, "tempfile", StringComparison.OrdinalIgnoreCase) == 0)
						value = Path.GetTempFileName();
					else
						value = Environment.GetEnvironmentVariable(variableName);

					return value;
        });
		} // func GetEnvironmentPath

		public static string GetDirectoryName(XObject x) => Path.GetDirectoryName(GetLocalPath(x?.BaseUri));

		public static string GetFileName(XObject x, string filename)
		{
			if (String.IsNullOrEmpty(filename))
				return GetDirectoryName(x);

			filename = GetEnvironmentPath(filename);
			if (Path.IsPathRooted(filename))
				return Path.GetFullPath(filename);

			return Path.GetFullPath(Path.Combine(GetDirectoryName(x), filename));
		} // func GetFileName
	} // class ProcsDE
}
