using System;
using System.Collections.Generic;
using System.IO;
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

        public async Task InitializeAsync()
        {
            if (_view.CoreWebView2 != null)
            {
                return;
            }

            await _view.EnsureCoreWebView2Async().ConfigureAwait(true);
            AttachRequestFilter();
            await AttachConsoleLoggingAsync().ConfigureAwait(true);
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
                RuntimeDiagnostics.Write("ruffle-cookie", "cleared pending cookies before webview initialization");
                return;
            }

            try
            {
                _view.CoreWebView2.CookieManager.DeleteAllCookies();
                RuntimeDiagnostics.Write("ruffle-cookie", "cleared webview2 cookie jar before apply");
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-cookie", $"clear webview2 cookies failed error={ex.Message}");
            }
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
                foreach (string segment in cookieHeader.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
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

                    CoreWebView2Cookie cookie = cookieManager.CreateCookie(name, value, targetUri.Host, "/");
                    cookie.IsHttpOnly = false;
                    cookie.IsSecure = string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                    cookie.SameSite = CoreWebView2CookieSameSiteKind.None;
                    cookieManager.AddOrUpdateCookie(cookie);
                    applied += 1;
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
            }

            _view.Dispose();
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
            _consoleAttached = true;
            RuntimeDiagnostics.Write("ruffle", "webview console logging attached");
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

                Dictionary<string, string> headers = ReadRequestHeaders(e.Request.Headers);
                byte[] requestBody = await ReadRequestBodyAsync(e.Request.Content).ConfigureAwait(true);
                RuffleLocalProxy.RuffleResolvedResponse response =
                    await _proxy.TryHandleRequestAsync(requestUri, e.Request.Method, headers, requestBody).ConfigureAwait(true);
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
    }
}
