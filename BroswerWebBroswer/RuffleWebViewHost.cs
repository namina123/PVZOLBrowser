using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebBrowserApp
{
    internal sealed class RuffleWebViewHost : IRuffleBrowserHost
    {
        private readonly WebView2 _view;
        private readonly RuffleLocalProxy _proxy;
        private readonly Dictionary<string, string> _pendingCookies =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _requestFilterAttached;
        private bool _consoleAttached;
        private bool _clearCookiesOnInitialize;

        internal RuffleWebViewHost(Control parent, RuffleLocalProxy proxy)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            _view = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            _view.SourceChanged += View_SourceChanged;
            _view.NavigationCompleted += View_NavigationCompleted;
            parent.Controls.Add(_view);
        }

        public Control ViewControl => _view;

        public bool IsInitialized => _view.CoreWebView2 != null;

        public event EventHandler<RuffleSourceChangedEventArgs> SourceChanged;

        public event EventHandler<RuffleNavigationCompletedEventArgs> NavigationCompleted;

        public event EventHandler<RuffleNewWindowRequestedEventArgs> NewWindowRequested;

        public async Task InitializeAsync()
        {
            if (_view.CoreWebView2 != null)
            {
                return;
            }

            await _view.EnsureCoreWebView2Async().ConfigureAwait(true);
            if (_clearCookiesOnInitialize)
            {
                DeleteAllWebViewCookiesIfPossible("initialization");
                _clearCookiesOnInitialize = false;
            }
            AttachRequestFilter();
            await AttachConsoleLoggingAsync().ConfigureAwait(true);
            AttachWindowInterception();
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _view.Visible = true;
            ApplyPendingCookies();
            RuntimeDiagnostics.Write(
                "ruffle",
                $"webview2 initialized browserVersion={_view.CoreWebView2.Environment.BrowserVersionString}");
        }

        public void Navigate(string url)
        {
            if (_view.CoreWebView2 == null)
            {
                throw new InvalidOperationException("Ruffle WebView2 尚未初始化。");
            }

            _view.CoreWebView2.Navigate(url);
        }

        public void Reload()
        {
            if (_view.CoreWebView2 == null)
            {
                throw new InvalidOperationException("Ruffle WebView2 尚未初始化。");
            }

            _view.CoreWebView2.Reload();
        }

        public Task<string> ExecuteScriptAsync(string script)
        {
            if (_view.CoreWebView2 == null)
            {
                throw new InvalidOperationException("Ruffle WebView2 尚未初始化。");
            }

            return _view.CoreWebView2.ExecuteScriptAsync(script);
        }

        public void ClearCookies()
        {
            _pendingCookies.Clear();
            _proxy.ClearCookieHeaders();

            if (_view.CoreWebView2 == null)
            {
                _clearCookiesOnInitialize = true;
                RuntimeDiagnostics.Write("ruffle-cookie", "queued full webview2 cookie clear before initialization");
                return;
            }

            DeleteAllWebViewCookiesIfPossible("runtime");
        }

        public void ApplyCookies(Uri targetUri, string cookieHeader)
        {
            if (targetUri == null || string.IsNullOrWhiteSpace(targetUri.Host) || string.IsNullOrWhiteSpace(cookieHeader))
            {
                return;
            }

            if (_view.CoreWebView2 == null)
            {
                _pendingCookies[targetUri.Host] = cookieHeader;
                RuntimeDiagnostics.Write("ruffle-cookie", $"queued webview2 cookies host={targetUri.Host} length={cookieHeader.Length}");
                return;
            }

            try
            {
                CoreWebView2CookieManager cookieManager = _view.CoreWebView2.CookieManager;
                int applied = 0;
                foreach (string segment in SplitCookieSegments(cookieHeader))
                {
                    string trimmed = segment.Trim();
                    int equalsIndex = trimmed.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        continue;
                    }

                    string name = trimmed.Substring(0, equalsIndex).Trim();
                    string value = trimmed.Substring(equalsIndex + 1).Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    foreach (string domain in BuildCookieDomains(targetUri.Host))
                    {
                        CoreWebView2Cookie cookie = cookieManager.CreateCookie(name, value, domain, "/");
                        cookie.IsHttpOnly = false;
                        cookie.IsSecure = string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                        cookie.SameSite = CoreWebView2CookieSameSiteKind.None;
                        cookieManager.AddOrUpdateCookie(cookie);
                        applied += 1;
                    }
                }

                RuntimeDiagnostics.Write("ruffle-cookie", $"webview2 cookies applied host={targetUri.Host} count={applied}");
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-cookie", $"webview2 cookie apply failed host={targetUri.Host} error={ex.Message}");
            }
        }

        public void Dispose()
        {
            _view.SourceChanged -= View_SourceChanged;
            _view.NavigationCompleted -= View_NavigationCompleted;

            if (_view.CoreWebView2 != null)
            {
                _view.CoreWebView2.WebMessageReceived -= View_WebMessageReceived;
                _view.CoreWebView2.WebResourceRequested -= View_WebResourceRequested;
                _view.CoreWebView2.NewWindowRequested -= View_NewWindowRequested;
            }

            _view.Dispose();
        }

        public async Task<string> GetCookieHeaderAsync(params Uri[] candidateUris)
        {
            if (_view.CoreWebView2 == null)
            {
                return string.Empty;
            }

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<Uri> targets = (candidateUris ?? Array.Empty<Uri>())
                .Where(uri => uri != null)
                .GroupBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());

            foreach (Uri candidate in targets)
            {
                try
                {
                    List<CoreWebView2Cookie> cookies = await _view.CoreWebView2.CookieManager
                        .GetCookiesAsync(candidate.AbsoluteUri)
                        .ConfigureAwait(true);
                    foreach (CoreWebView2Cookie cookie in cookies)
                    {
                        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                        {
                            continue;
                        }

                        merged[cookie.Name] = $"{cookie.Name}={cookie.Value ?? string.Empty}";
                    }
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Write("ruffle-cookie", $"read cookies failed uri={candidate} error={ex.Message}");
                }
            }

            return string.Join("; ", merged.Values);
        }

        private void View_SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            if (_view.Source == null)
            {
                return;
            }

            SourceChanged?.Invoke(this, new RuffleSourceChangedEventArgs(_view.Source));
        }

        private void View_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_view.Source == null)
            {
                return;
            }

            NavigationCompleted?.Invoke(
                this,
                new RuffleNavigationCompletedEventArgs(_view.Source, e.IsSuccess, e.WebErrorStatus.ToString()));
        }

        private async Task AttachConsoleLoggingAsync()
        {
            if (_consoleAttached || _view.CoreWebView2 == null)
            {
                return;
            }

            _view.CoreWebView2.WebMessageReceived += View_WebMessageReceived;
            await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){"
                + "if(window.__pvzolConsoleBridgeInstalled){return;}"
                + "window.__pvzolConsoleBridgeInstalled=true;"
                + "if(!window.chrome||!window.chrome.webview||typeof window.chrome.webview.postMessage!=='function'){return;}"
                + "function encodeBase64Utf8(value){"
                + "try{return btoa(unescape(encodeURIComponent(value)));}catch(e){return '';}"
                + "}"
                + "function stringify(value){"
                + "if(typeof value==='string'){return value;}"
                + "try{return JSON.stringify(value);}catch(e){}"
                + "try{return String(value);}catch(e){return '[unprintable]';}"
                + "}"
                + "function send(level,args){"
                + "try{"
                + "var parts=[];"
                + "for(var i=0;i<args.length;i++){parts.push(stringify(args[i]));}"
                + "var message=parts.join(' ');"
                + "window.chrome.webview.postMessage(JSON.stringify({kind:'ruffle-console',level:level,messageBase64:encodeBase64Utf8(message)}));"
                + "}catch(e){}"
                + "}"
                + "['log','info','warn','error'].forEach(function(level){"
                + "var original=console[level];"
                + "console[level]=function(){send(level,arguments);if(typeof original==='function'){return original.apply(console,arguments);}};"
                + "});"
                + "})();").ConfigureAwait(true);
            await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){"
                + "if(window.__pvzolNavHookInstalled){return;}"
                + "window.__pvzolNavHookInstalled=true;"
                + "function nav(url){try{if(url){window.location.href=String(url);return true;}}catch(e){}return false;}"
                + "window.open=function(url){nav(url);return window;};"
                + "if(window.showModalDialog){window.showModalDialog=function(url){nav(url);return null;};}"
                + "if(window.showModelessDialog){window.showModelessDialog=function(url){nav(url);return null;};}"
                + "function patchTargets(){"
                + "var anchors=document.getElementsByTagName('a');"
                + "for(var i=0;i<anchors.length;i++){try{if(anchors[i].target){anchors[i].target='_self';}}catch(e){}}"
                + "var forms=document.getElementsByTagName('form');"
                + "for(var j=0;j<forms.length;j++){try{if(forms[j].target){forms[j].target='_self';}}catch(e){}}"
                + "}"
                + "patchTargets();"
                + "if(window.setInterval){window.setInterval(patchTargets,1000);}"
                + "if(document.addEventListener){document.addEventListener('click',function(evt){"
                + "var el=evt&&evt.target?evt.target:null;"
                + "while(el&&el.tagName&&el.tagName.toLowerCase()!=='a'){el=el.parentElement;}"
                + "if(el&&el.href){try{el.target='_self';}catch(e){}}"
                + "},true);}"
                + "})();").ConfigureAwait(true);
            _consoleAttached = true;
            RuntimeDiagnostics.Write("ruffle", "webview console logging attached");
        }

        private void AttachWindowInterception()
        {
            if (_view.CoreWebView2 == null)
            {
                return;
            }

            _view.CoreWebView2.NewWindowRequested -= View_NewWindowRequested;
            _view.CoreWebView2.NewWindowRequested += View_NewWindowRequested;
        }

        private void View_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri targetUri))
            {
                RuntimeDiagnostics.Write("ruffle-nav", $"intercepted new window url={targetUri}");
                NewWindowRequested?.Invoke(this, new RuffleNewWindowRequestedEventArgs(targetUri));
            }
            else
            {
                RuntimeDiagnostics.Write("ruffle-nav", $"intercepted new window with invalid uri={e.Uri}");
            }
        }

        private void View_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string rawMessage;
            try
            {
                rawMessage = e.TryGetWebMessageAsString() ?? string.Empty;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return;
            }

            string level = "log";
            string message = rawMessage;
            if (rawMessage.IndexOf("\"kind\":\"ruffle-console\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                level = ExtractJsonValue(rawMessage, "level") ?? level;
                string messageBase64 = ExtractJsonValue(rawMessage, "messageBase64");
                message = DecodeBase64Utf8(messageBase64) ?? ExtractJsonValue(rawMessage, "message") ?? rawMessage;
            }

            RuntimeDiagnostics.Write("ruffle-console", $"level={level} source={e.Source} message={message}");
        }

        private void AttachRequestFilter()
        {
            if (_requestFilterAttached || _view.CoreWebView2 == null)
            {
                return;
            }

            _view.CoreWebView2.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            _view.CoreWebView2.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            _view.CoreWebView2.WebResourceRequested += View_WebResourceRequested;
            _requestFilterAttached = true;
            RuntimeDiagnostics.Write("ruffle", "webview request interception attached");
        }

        private async void View_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            CoreWebView2Deferral deferral = e.GetDeferral();
            try
            {
                if (_view.CoreWebView2 == null)
                {
                    return;
                }

                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri requestUri))
                {
                    return;
                }

                string method = e.Request.Method ?? "GET";
                string requestPath = requestUri.AbsolutePath ?? "/";
                bool isProxyAuthorityRequest =
                    _proxy.BaseUri != null
                    && string.Equals(requestUri.Authority, _proxy.BaseUri.Authority, StringComparison.OrdinalIgnoreCase);
                bool isManagedPath =
                    requestPath.StartsWith("/__proxy__/", StringComparison.OrdinalIgnoreCase)
                    || requestPath.StartsWith("/__player__/", StringComparison.OrdinalIgnoreCase)
                    || requestPath.StartsWith("/__ruffle__/", StringComparison.OrdinalIgnoreCase);
                if (isProxyAuthorityRequest
                    && requestPath.StartsWith("/__proxy__/", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeDiagnostics.Write("ruffle-amf", $"allow local proxy post pass-through url={requestUri}");
                    return;
                }

                bool isAmfCandidate = IsAmfCandidateRequest(requestUri, method, e.Request.Headers);
                if (!isManagedPath && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !isAmfCandidate)
                {
                    RuntimeDiagnostics.Write("ruffle-amf", $"bypass direct post url={requestUri}");
                    return;
                }

                Dictionary<string, string> headers = ReadRequestHeaders(e.Request.Headers);
                byte[] requestBody = null;
                if (isManagedPath || !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    requestBody = await ReadRequestBodyAsync(e.Request.Content).ConfigureAwait(true);
                }

                if (!isManagedPath && isAmfCandidate)
                {
                    string amfContentType = headers.TryGetValue("Content-Type", out string contentTypeValue)
                        ? contentTypeValue
                        : string.Empty;
                    RuntimeDiagnostics.Write(
                        "ruffle-amf",
                        $"direct post capture url={requestUri} bodyBytes={(requestBody == null ? 0 : requestBody.Length)} contentType={amfContentType}");
                    if (requestBody == null || requestBody.Length == 0)
                    {
                        RuntimeDiagnostics.Write("ruffle-amf", $"fallback to native direct post because body is empty url={requestUri}");
                        return;
                    }
                }

                RuffleLocalProxy.RuffleResolvedResponse response =
                    await _proxy.TryHandleRequestAsync(requestUri, method, headers, requestBody).ConfigureAwait(true);
                if (response == null)
                {
                    return;
                }

                var stream = new MemoryStream(response.Body ?? Array.Empty<byte>(), writable: false);
                e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream,
                    response.StatusCode,
                    response.ReasonPhrase,
                    response.BuildHeaderString());
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"webresource error={ex}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private static bool IsAmfCandidateRequest(Uri requestUri, string method, CoreWebView2HttpRequestHeaders headers)
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) || requestUri == null)
            {
                return false;
            }

            string path = requestUri.AbsolutePath ?? string.Empty;
            if (path.IndexOf("/pvz/amf/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (headers == null)
            {
                return false;
            }

            if (!headers.Contains("Content-Type"))
            {
                return false;
            }

            return headers.GetHeader("Content-Type")
                .IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyPendingCookies()
        {
            if (_view.CoreWebView2 == null || _pendingCookies.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pendingCookie in _pendingCookies)
            {
                if (Uri.TryCreate($"http://{pendingCookie.Key}", UriKind.Absolute, out Uri domainUri))
                {
                    ApplyCookies(domainUri, pendingCookie.Value);
                }
            }

            _pendingCookies.Clear();
        }

        private void DeleteAllWebViewCookiesIfPossible(string reason)
        {
            try
            {
                _view.CoreWebView2.CookieManager.DeleteAllCookies();
                RuntimeDiagnostics.Write("ruffle-cookie", $"cleared webview2 cookie jar reason={reason}");
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-cookie", $"clear webview2 cookies failed reason={reason} error={ex.Message}");
            }
        }

        private static Dictionary<string, string> ReadRequestHeaders(CoreWebView2HttpRequestHeaders headers)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return map;
            }

            CoreWebView2HttpHeadersCollectionIterator iterator = headers.GetIterator();
            while (iterator != null && iterator.HasCurrentHeader)
            {
                KeyValuePair<string, string> current = iterator.Current;
                if (!string.IsNullOrWhiteSpace(current.Key))
                {
                    map[current.Key] = current.Value ?? string.Empty;
                }

                iterator.MoveNext();
            }

            return map;
        }

        private static async Task<byte[]> ReadRequestBodyAsync(Stream content)
        {
            if (content == null)
            {
                return null;
            }

            if (content.CanSeek)
            {
                content.Position = 0;
            }

            using (var buffer = new MemoryStream())
            {
                await content.CopyToAsync(buffer).ConfigureAwait(false);
                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                return buffer.ToArray();
            }
        }

        private static string DecodeBase64Utf8(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string token = "\"" + key + "\":\"";
            int startIndex = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
            {
                return null;
            }

            startIndex += token.Length;
            int endIndex = startIndex;
            bool escaped = false;
            while (endIndex < json.Length)
            {
                char current = json[endIndex];
                if (!escaped && current == '"')
                {
                    break;
                }

                escaped = !escaped && current == '\\';
                if (current != '\\' || escaped)
                {
                    escaped = false;
                }
                endIndex += 1;
            }

            if (endIndex <= startIndex || endIndex > json.Length)
            {
                return null;
            }

            string value = json.Substring(startIndex, endIndex - startIndex);
            return value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static IEnumerable<string> SplitCookieSegments(string cookieHeader)
        {
            return (cookieHeader ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Contains("="));
        }

        private static IEnumerable<string> BuildCookieDomains(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (seen.Add(host))
            {
                yield return host;
            }

            string normalizedHost = host.Trim().TrimStart('.');
            if (normalizedHost.EndsWith(".youkia.com", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(".youkia.com"))
                {
                    yield return ".youkia.com";
                }

                if (seen.Add("youkia.com"))
                {
                    yield return "youkia.com";
                }
            }
        }
    }
}
