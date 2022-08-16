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
using Neo.IronLua;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Networking;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	internal class ProxyItem : DEConfigItem, IHttpWorker
	{
		#region -- class RewriteRule --------------------------------------------------

		private abstract class RewriteRule
		{
			private readonly string id;
			private readonly Predicate<string> mediaTypeFilter;

			protected RewriteRule(string id, Predicate<string> mediaTypeFilter)
			{
				this.id = id ?? throw new ArgumentNullException(nameof(id));
				this.mediaTypeFilter = mediaTypeFilter ?? throw new ArgumentNullException(nameof(mediaTypeFilter));
			} // ctor

			public bool IsMatch(string mediaType)
				=> mediaTypeFilter(mediaType);

			public abstract string TransformLine(string line);

			public string Id => id;
		} // class RewriteRule

		#endregion

		#region -- class RegexRewriteRule ---------------------------------------------

		private sealed class RegexRewriteRule : RewriteRule
		{
			private readonly Regex regex;
			private readonly string replacement;

			public RegexRewriteRule(string id, Predicate<string> mediaTypeFilter, Regex regex, string replacement)
				: base(id, mediaTypeFilter)
			{
				this.regex = regex ?? throw new ArgumentNullException(nameof(regex));
				this.replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
			} // ctor

			public override string TransformLine(string line)
				=> regex.Replace(line, replacement);
		} // class RegexRewriteRule

		#endregion

		#region -- class RedirectRule -------------------------------------------------

		private abstract class RedirectRule
		{
			private readonly string id;
			private readonly List<RewriteRule> rewriteRules = new List<RewriteRule>();
			private readonly string customRewriter;

			protected RedirectRule(string id, string customRewriter)
			{
				this.id = id ?? throw new ArgumentNullException(nameof(id));
				this.customRewriter = customRewriter;
			} // ctor

			public void AppendRewrite(RewriteRule rule)
				=> rewriteRules.Add(rule);

			public virtual bool TryTransform(string sourceUrl, out string targetUrl)
			{
				targetUrl = null;
				return false;
			} // func TryTransform

			public string Id => id;
			public string CustomRewriter => customRewriter;

			public IEnumerable<RewriteRule> Rewrites => rewriteRules;
		} // class RedirectRule

		#endregion

		#region -- class RedirectRegexRule --------------------------------------------

		private sealed class RedirectRegexRule : RedirectRule
		{
			private readonly Regex urlFilter;
			private readonly string replacement;

			public RedirectRegexRule(string id, Regex urlFilter, string replacement, string customRewriter)
				: base(id, customRewriter)
			{
				this.urlFilter = urlFilter ?? throw new ArgumentNullException(nameof(urlFilter));
				this.replacement = replacement;
			} // ctor

			public override bool TryTransform(string sourceUrl, out string targetUrl)
			{
				var m = urlFilter.Match(sourceUrl);
				if (m.Success)
				{
					if (replacement == null)
						targetUrl = null;
					else
						targetUrl = urlFilter.Replace(sourceUrl, replacement);
					return true;
				}
				else
					return base.TryTransform(sourceUrl, out targetUrl);
			} // func TryTransform
		} // class RedirectRegexRule

		#endregion

		#region -- class RedirectSimpleRule -------------------------------------------

		private sealed class RedirectSimpleRule : RedirectRule
		{
			private readonly Predicate<string> urlFilter;
			private readonly bool allow;

			public RedirectSimpleRule(string id, Predicate<string> urlFilter, bool allow, string customRewriter)
				: base(id, customRewriter)
			{
				this.urlFilter = urlFilter ?? throw new ArgumentNullException(nameof(urlFilter));
				this.allow = allow;
			} // ctor

			public override bool TryTransform(string sourceUrl, out string targetUrl)
			{
				if (urlFilter(sourceUrl))
				{
					targetUrl = allow ? sourceUrl : null;
					return true;
				}
				else
					return base.TryTransform(sourceUrl, out targetUrl);
			} // func TryTransform
		} // class RedirectSimpleRule

		#endregion

		private HttpClient client = null;
		private bool defaultAllow = false;
		private RedirectRule[] redirectRules = Array.Empty<RedirectRule>();

		#region -- Ctor/Dtor/Config ---------------------------------------------------

		public ProxyItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		private RedirectRule ParseRedirectRule(XConfigNode cur)
		{
			var id = cur.GetAttribute<string>("id");
			try
			{
				var url = cur.GetAttribute<string>("url");
				var allowInSearch = cur.GetAttribute<bool>("allowInSearch");
				var allow = cur.GetAttribute<bool>("allow");
				var replacement = cur.GetAttribute<string>("urlReplacement");
				var customRewriter = cur.GetAttribute<string>("customRewriter");

				if (replacement == null && url.Length > 0 && url[0] == '/') // default mode, filter by start
				{
					var tmp = url.Substring(1);
					var filter = allowInSearch
						? new Predicate<string>(c => c.IndexOf(tmp) >= 0)
						: new Predicate<string>(c => c.StartsWith(tmp));

					return new RedirectSimpleRule(id, filter, allow, customRewriter);
				}
				else
				{
					if (!allowInSearch && (url.Length == 0 || url[0] != '^'))
						url = '^' + url;

					var filter = new Regex(url, RegexOptions.Singleline | RegexOptions.Compiled);
					return new RedirectRegexRule(id, filter, allow ? replacement : null, customRewriter);
				}

			}
			catch (Exception e)
			{
				Log.Warn(new DEConfigurationException(cur.Data, $"Could not parse redirect rule (id={id})", e));
				return null;
			}
		} // func ParseRedirectRule

		private void ParseRewriteRule(IReadOnlyList<RedirectRule> redirectRules, XConfigNode cur)
		{
			var id = cur.GetAttribute<string>("id");
			try
			{
				var rule = new RegexRewriteRule(id,
					Procs.GetFilterFunction(cur.GetAttribute<string>("media"), false),
					new Regex(cur.GetAttribute<string>("pattern"), RegexOptions.Singleline | RegexOptions.Compiled),
					cur.GetAttribute<string>("replacement")
				);

				foreach (var rid in cur.GetAttribute<string[]>("redirect"))
				{
					var appended = false;

					var filterRedirect = Procs.GetFilterFunction(rid, false);
					foreach (var redirect in redirectRules.Where(c => filterRedirect(c.Id)))
					{
						redirect.AppendRewrite(rule);
						appended = true;
					}

					if (!appended)
						Log.Warn($"Rewrite '{id}' rule not assigned to redirect '{rid}'.");
				}
			}
			catch (Exception e)
			{
				Log.Warn(new DEConfigurationException(cur.Data, $"Could not parse rewrite rule (id={id})", e));
			}
		} // func ParseRewriteRule

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			var configNew = XConfigNode.Create(Server.Configuration, config.ConfigNew);

			// -- Parse target --
			// ServicePointManager.DefaultConnectionLimit
			var clientHandler = new HttpClientHandler() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
			var httpClient = new HttpClient(clientHandler, true)
			{
				BaseAddress = new Uri(configNew.GetAttribute<string>("target"), UriKind.Absolute),
				Timeout = TimeSpan.FromSeconds(configNew.GetAttribute<int>("timeout"))
			};

			// -- parse rules --
			var redirectRules = new List<RedirectRule>();

			redirectRules.AddRange(
				from cur in configNew.Elements(DEConfigurationConstants.xnProxyRedirect)
				let rule = ParseRedirectRule(cur)
				where rule != null
				select rule
			);

			foreach (var cur in configNew.Elements(DEConfigurationConstants.xnProxyRewrite))
				ParseRewriteRule(redirectRules, cur);

			config.Tags.SetProperty(nameof(client), httpClient);
			config.Tags.SetProperty(nameof(defaultAllow), configNew.GetAttribute<bool>("defaultAllow"));
			config.Tags.SetProperty(nameof(redirectRules), redirectRules.ToArray());

			base.OnBeginReadConfiguration(config);
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			VirtualRoot = ConfigNode.GetAttribute<string>("base") ?? String.Empty;
			Priority = ConfigNode.GetAttribute<int>("priority");

			client = config.Tags.GetProperty<HttpClient>(nameof(client), null);
			defaultAllow = config.Tags.GetProperty(nameof(defaultAllow), false);
			redirectRules = config.Tags.GetProperty(nameof(redirectRules), Array.Empty<RedirectRule>());

			base.OnEndReadConfiguration(config);
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Helper -------------------------------------------------------------

		private static string[] invalidHeaders = new string[] { "transfer-encoding", "keep-alive", "content-length", };

		private static Uri CreateRequestUri(IDEWebRequestScope r, string targetUrl)
		{
			// create relative uri
			var sb = new StringBuilder();
			sb.Append(targetUrl);

			// append arguments
			HttpStuff.MakeUriArguments(sb, false, r.ToProperties());

			return new Uri(sb.ToString(), UriKind.Relative);
		} // func CreateRequestUri

		private static bool IsInvalidHeader(string k)
			=> Array.Exists(invalidHeaders, c => String.Compare(c, k, StringComparison.OrdinalIgnoreCase) == 0);

		private static bool TryCopyHeaderValue(NameValueCollection requestHeaders, int i, string k, HttpHeaders headers)
			=> headers.TryAddWithoutValidation(k, requestHeaders.GetValues(i));

		private static void CopyHeaders(NameValueCollection requestHeaders, HttpRequestHeaders targetRequestHeaders, HttpContentHeaders targetContentHeaders)
		{
			for (var i = 0; i < requestHeaders.Count; i++)
			{
				var k = requestHeaders.GetKey(i);
				//if (IsInvalidHeader(k))
				//	continue;

				if (targetRequestHeaders == null || !TryCopyHeaderValue(requestHeaders, i, k, targetRequestHeaders))
				{
					if (targetContentHeaders != null)
						TryCopyHeaderValue(requestHeaders, i, k, targetContentHeaders);
				}
			}
		} // proc CopyHeaders

		private static void CopyHeaders(HttpHeaders sourceHeaders, WebHeaderCollection targetHeaders)
		{
			foreach (var kv in sourceHeaders)
			{
				if (IsInvalidHeader(kv.Key))
					continue;

				targetHeaders.Add(kv.Key, String.Join(";", kv.Value));
			}
		} // func CopyHeaders

		private static void CopyHeaders(HttpResponseHeaders sourceResponseHeaders, HttpContentHeaders sourceContentHeaders, WebHeaderCollection targetHeaders)
		{
			if (sourceResponseHeaders != null)
				CopyHeaders(sourceResponseHeaders, targetHeaders);
			if (sourceContentHeaders != null)
				CopyHeaders(sourceContentHeaders, targetHeaders);
		} // proc CopyHeaders

		private void AppendHeader(HttpRequestHeaders headers, string name, string value, bool overwrite)
		{
			if (overwrite || !headers.Contains(name))
				headers.TryAddWithoutValidation(name, value);
		} // proc AppendHeader

		private string[] GetForwardedForValue(HttpRequestHeaders headers)
			=> headers.TryGetValues("X-Forwarded-For", out var values) ? values.ToArray() : null;

		private void AppendHeaders(DEWebRequestScope r, HttpRequestHeaders headers)
		{
			string clientIP;
			var url = r.Context.Request.Url;
			var proxyList = GetForwardedForValue(headers);

			if (proxyList.Length > 0)
			{
				clientIP = proxyList[0]; // first ip is client ip

				// append own ip as proxy
				var newProxyList = new string[proxyList.Length + 1];
				Array.Copy(proxyList, 0, newProxyList, 0, proxyList.Length);
				newProxyList[proxyList.Length] = r.Context.Request.LocalEndPoint.Address.ToString();

				AppendHeader(headers, "X-Forwarded-For", String.Join(",", newProxyList), true);
			}
			else
			{
				clientIP = r.Context.Request.RemoteEndPoint.Address.ToString();
				headers.Host = url.Host;
				headers.TryAddWithoutValidation("X-Forwarded-For", clientIP);
			}
			
			AppendHeader(headers, "X-Real-IP", clientIP, false);
			AppendHeader(headers,"X-Forwarded-Proto", url.Scheme, false);
			AppendHeader(headers,"X-Forwarded-Protocol", url.Scheme,false);
			AppendHeader(headers, "X-Forwarded-Host", url.Host, false);
		} // proc AppendHeaders

		#endregion

		#region -- ProxyRequest -------------------------------------------------------

		private HttpMethod GetHttpMethod(DEWebRequestScope r)
		{
			switch (r.InputMethod)
			{
				case "POST":
					return HttpMethod.Post;
				case "GET":
					return HttpMethod.Get;
				case "PUT":
					return HttpMethod.Put;
				case "HEAD":
					return HttpMethod.Head;
				default:
					throw new ArgumentException($"Unsupported method: {r.InputMethod}");
			}
		} // func GetHttpMethod

		private string MakeNewUri(DEWebRequestScope r, string relativeUri)
		{
			if (relativeUri == null)
				relativeUri = String.Empty;

			return r.GetSubPathOrigin(new Uri(relativeUri, UriKind.Relative)).AbsolutePath;
		} // func MakeNewUri

		private void ProxyRequestFoundAsync(DEWebRequestScope r, HttpResponseMessage response, RedirectRule redirectRule)
		{
			var redirectTo = response.Headers.Location.ToString(); // get content
			CopyHeaders(response.Headers, response.Content?.Headers, r.OutputHeaders);

			// allow rewrite rules
			var isRuleHit = false;
			foreach (var rw in redirectRule.Rewrites)
			{
				if (rw.IsMatch("status/302"))
					redirectTo = rw.TransformLine(redirectTo);
			}

			// default rewrite
			if (!isRuleHit)
				redirectTo = MakeNewUri(r, redirectTo);

			// send move
			r.OutputHeaders["Location"] = redirectTo;
			r.SetStatus(HttpStatusCode.Found, response.ReasonPhrase);
		} // func ProxyRequestFoundAsync

		private async Task ProxyRequestRewriteAsync(DEWebRequestScope r, HttpResponseMessage response, RewriteRule[] rewriteRules)
		{
			// send header
			CopyHeaders(response.Headers, response.Content?.Headers, r.OutputHeaders);

			// send modified content
			using (var src = await response.Content.ReadAsStreamAsync())
			using (var tr = new StreamReader(src))
			using (var dst = r.GetOutputStream(response.Content.Headers.ContentType.ToString(), response.Content.Headers.ContentLength ?? -1))
			using (var sr = new StreamWriter(dst))
			{
				string l;
				while ((l = await tr.ReadLineAsync()) != null)
				{
					foreach (var cur in rewriteRules)
						l = cur.TransformLine(l);
					await sr.WriteLineAsync(l);
				}
			}
		} // func ProxyRequestRewriteAsync

		private async Task ProxyRequestDirectAsync(DEWebRequestScope r, HttpResponseMessage response)
		{
			// send header
			CopyHeaders(response.Headers, response.Content?.Headers, r.OutputHeaders);

			// send content
			using (var src = await response.Content.ReadAsStreamAsync())
			using (var dst = r.GetOutputStream(response.Content.Headers.ContentType.ToString(), response.Content.Headers.ContentLength ?? -1))
				await src.CopyToAsync(dst);
		} // proc ProxyRequestDirectAsync

		private async Task<object> GetRequestDataAsync(DEWebRequestScope r)
		{
			if (r.InputContentType.StartsWith("text/")
				|| r.InputContentType == "application/x-www-form-urlencoded")
			{
				using var tr = r.GetInputTextReader();
				return await tr.ReadToEndAsync();
			}
			else
			{
				using var src = r.GetInputStream();
				return await src.ReadInArrayAsync();
			}
		} // func GetRequestDataAsync

		private bool InvokeCustomRewriter(string customRewriter, HttpClient client, DEWebRequestScope request, object requestData, string targetUrl)
		{
			var r = CallMemberDirect(customRewriter, new object[] { client, request, requestData, targetUrl }, throwExceptions: true).ToBoolean();
			Log.Debug($"Rewrite[{customRewriter}]: {targetUrl} => {r}");
			return r;
		} // func InvokeCustomRewriter

		private async Task<bool> ProxyRequestAsync(HttpClient client, DEWebRequestScope r, RedirectRule redirectRule, string targetUrl)
		{
			// build request
			var method = GetHttpMethod(r);
			var request = new HttpRequestMessage(method, CreateRequestUri(r, targetUrl));

			// connect input content
			var hasCustomRewriter = !String.IsNullOrEmpty(redirectRule.CustomRewriter);
			if (r.HasInputData)
			{
				if (hasCustomRewriter)
				{
					var requestData = await GetRequestDataAsync(r);
					if (await Task.Run(() => InvokeCustomRewriter(redirectRule.CustomRewriter, client, r, requestData, targetUrl)))
						return true;
					switch (requestData)
					{
						case string s:
							request.Content = new StringContent(s, Encoding.UTF8, r.InputContentType);
							break;
						case byte[] b:
							request.Content = new ByteArrayContent(b);
							break;
						default:
							break;
					}
				}
				else
					request.Content = new StreamContent(r.Context.Request.InputStream);
			}
			else
			{
				if (hasCustomRewriter && await Task.Run(() => InvokeCustomRewriter(redirectRule.CustomRewriter, client, r, null, targetUrl)))
					return true;
			}

			// build 
			CopyHeaders(r.Context.Request.Headers, request.Headers, request.Content?.Headers);
			AppendHeaders(r, request.Headers);

			// remote request
			using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken.None))
			{
				switch (response.StatusCode)
				{
					case HttpStatusCode.OK:
						if (response.Content.Headers.ContentType != null)
						{
							var mediaType = response.Content.Headers.ContentType.MediaType;
							var rewrites = redirectRule.Rewrites.Where(c => c.IsMatch(mediaType)).ToArray();
							if (rewrites.Length == 0)
								await ProxyRequestDirectAsync(r, response);
							else
								await ProxyRequestRewriteAsync(r, response, rewrites);
						}
						else
							await ProxyRequestDirectAsync(r, response);
						return true;
					case HttpStatusCode.Found:
						ProxyRequestFoundAsync(r, response, redirectRule);
						return true;
					case HttpStatusCode.NotFound:
						return false;
					case HttpStatusCode.NotModified:
						CopyHeaders(response.Headers, response.Content?.Headers, r.OutputHeaders);
						r.SetStatus(HttpStatusCode.NotModified, response.ReasonPhrase);
						return true;
					default:
						r.SetStatus(response.StatusCode, response.ReasonPhrase);
						return true;
				}
			}
		} // proc ProxyRequestAsync

		private bool IsProxyRequest(DEWebRequestScope r, out RedirectRule redirect, out string targetUrl)
		{
			foreach (var rule in redirectRules)
			{
				if (rule.TryTransform(r.RelativeSubPath, out targetUrl))
				{
					redirect = rule;
					return true;
				}
			}
			redirect = null;
			targetUrl = null;
			return false;
		} // func IsProxyRequest

		async Task<bool> IHttpWorker.RequestAsync(IDEWebRequestScope r)
		{
			var cl = client;
			if (cl != null && r is DEWebRequestScope ir && IsProxyRequest(ir, out var redirect, out var targetUrl))
				return await ProxyRequestAsync(cl, ir, redirect, targetUrl);
			else
				return false;
		} // func RequestAsync

		#endregion

		public string VirtualRoot { get; set; }
		public int Priority { get; set; }
	} // class ProxyItem
}
