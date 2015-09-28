using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TecWare.DE.Server.Stuff
{
	public static class ProcsDE
	{
		private static string GetDirectoryName(XObject x)
		{
			var path = x.BaseUri;

			if (path.StartsWith("file://"))
				path = new Uri(path).LocalPath;
			else
				path = path.Replace('/', '\\');

			return Path.GetDirectoryName(path);
		} // func GetDirectoryName

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
