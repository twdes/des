using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TecWare.DE.Networking;

namespace ServerHttpTest
{
	class Program
	{
		static void Main(string[] args)
		{
			var lines = 10;
			var http = DEHttpClient.Create(new Uri("http://localhost:8080/"));

			using (var rr = http.GetResponseAsync($"page2.html?l={lines}").Result)
			{
				var l = rr.Content.Headers.ContentLength;

				using (var src = rr.Content.ReadAsStreamAsync().Result)
				using (var dst = new FileStream("Test.txt", FileMode.Create))
				{
					var b = new byte[1024];
					while (true)
					{
						var r = src.Read(b, 0, b.Length);
						Console.Write(".");
						if (r <= 0)
							return;
						dst.Write(b, 0, r);
					}
				}
			}
		}
	}
}
