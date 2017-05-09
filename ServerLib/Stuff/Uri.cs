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
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
