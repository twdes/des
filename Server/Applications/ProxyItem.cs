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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TecWare.DE.Networking;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	internal class ProxyItem : DEConfigItem, IHttpWorker
	{
		private HttpClient client = null;

		#region -- Ctor/Dtor/Config ---------------------------------------------------

		public ProxyItem(IServiceProvider sp, string name) 
			: base(sp, name)
		{
		} // ctor

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			config.Tags.SetProperty(nameof(client), new HttpClient(new HttpClientHandler(), true)
			{
				BaseAddress = new Uri(config.ConfigNew.GetAttribute("target", null), UriKind.Absolute)
			});
			base.OnBeginReadConfiguration(config);
		} // proc 

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			VirtualRoot = ConfigNode.GetAttribute<string>("base") ?? String.Empty;
			Priority = ConfigNode.GetAttribute<int>("priority");
			client = config.Tags.GetProperty<HttpClient>(nameof(client), null);

			base.OnEndReadConfiguration(config);
		} // proc OnEndReadConfiguration

		#endregion

		#region -- ProxyRequest -------------------------------------------------------

		private async Task<bool> ProxyRequestAsync(HttpClient client, DEWebRequestScope r)
		{
			// form url
			var sb = new StringBuilder();
			var first = true;
			sb.Append(r.RelativeSubPath);
			foreach (var parameterName in r.ParameterNames)
			{
				if (r.TryGetProperty(parameterName, out var parameterValue))
				{
					if (first)
					{
						sb.Append('?');
						first = false;
					}
					else
						sb.Append('&');


					sb.Append(WebUtility.UrlEncode(parameterName));
					sb.Append('=');
					sb.Append(WebUtility.UrlEncode(parameterValue.ToString()));
				}
			}

			// build request
			var request = new HttpRequestMessage(
				r.InputMethod == "POST" ? HttpMethod.Post : HttpMethod.Get,
				sb.ToString()
			);
			if (r.HasInputData)
			{
				var content = new StreamContent(r.Context.Request.InputStream);
				request.Content = content;
			}

			// build 
			for (var i = 0; i < r.Context.Request.Headers.Count; i++)
			{
				var k = r.Context.Request.Headers.GetKey(i);
				var v = String.Join(";", r.Context.Request.Headers.GetValues(i));
				if (!(request.Content?.Headers.TryAddWithoutValidation(k, v) ?? false))
					request.Headers.TryAddWithoutValidation(k, v);
			}

			// remote request
			using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
			{
				if (response.StatusCode == HttpStatusCode.NotFound)
					return false;

				if (response.StatusCode != HttpStatusCode.OK)
					throw new HttpResponseException(response.StatusCode, response.ReasonPhrase);

				// send header
				foreach (var kv in response.Headers)
				{
					if (String.Compare(kv.Key, "transfer-encoding", StringComparison.OrdinalIgnoreCase) == 0)
						continue;
					if (String.Compare(kv.Key, "keep-alive", StringComparison.OrdinalIgnoreCase) == 0)
						continue;

					r.OutputHeaders.Add(kv.Key, String.Join(";", kv.Value));
				}

				// copy output headers
				foreach (var kv in response.Content.Headers)
				{
					if (String.Compare(kv.Key, "content-length", StringComparison.OrdinalIgnoreCase) == 0)
						continue;
					r.OutputHeaders.Add(kv.Key, String.Join(";", kv.Value));
				}

				// process content
				using (var src = await response.Content.ReadAsStreamAsync())
				using (var dst = r.GetOutputStream(response.Content.Headers.ContentType.ToString(), response.Content.Headers.ContentLength ?? -1, false))
					await src.CopyToAsync(dst);
			}
			return true;
		} // proc ProxyRequestAsync

		async Task<bool> IHttpWorker.RequestAsync(IDEWebRequestScope r)
		{
			var cl = client;
			if (cl != null && r is DEWebRequestScope ir)
				return await ProxyRequestAsync(cl, ir);
			else
				return false;
		} // func RequestAsync

		#endregion

		public string VirtualRoot { get; set; }
		public int Priority { get; set; }
	} // class ProxyItem
}
