using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TecWare.DE.Stuff
{
	public static partial class ProcsDE
	{
		public static string GetLocalPath(string path)
		{
			if (path == null)
				return null;

			if (path.StartsWith("file://"))
				path = new Uri(path).LocalPath;
			else
				path = path.Replace('/', '\\');
			return path;
		} // func GetLocalPath

		public static string GetDirectoryName(XObject x) => Path.GetDirectoryName(GetLocalPath(x?.BaseUri));

		public static string GetFileName(XObject x, string filename)
		{
			if (String.IsNullOrEmpty(filename))
				return GetDirectoryName(x);
			if (Path.IsPathRooted(filename))
				return Path.GetFullPath(filename);

			return Path.GetFullPath(Path.Combine(GetDirectoryName(x), filename));
		} // func GetFileName
	} // class ProcsDE
}
