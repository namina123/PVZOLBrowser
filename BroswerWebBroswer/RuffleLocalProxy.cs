using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WebBrowserApp
{
    internal sealed class RuffleLocalProxy : IDisposable
    {
        private const string ProxyPrefix = "/__proxy__/";
        private const string PlayerPrefix = "/__player__/";
        private const string AssetPrefix = "/__ruffle__/";
        private const string IeUserAgent = "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.1; Trident/6.0)";
        private const int LegacyPageMinViewportWidth = 1000;
        private const int LegacyPageMaxViewportWidth = 4096;
        private static readonly byte[] EmptyMixedArrayArgumentBody =
        {
            0x0A, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x09
        };
        private static readonly byte[] EmptyArgumentBody =
        {
            0x0A, 0x00, 0x00, 0x00, 0x00
        };
        private static readonly Regex BaseRegex = new Regex("<base\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HeadRegex = new Regex("</head>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlRegex = new Regex("<html[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CspMetaRegex = new Regex(
            "<meta[^>]+http-equiv\\s*=\\s*(['\"])content-security-policy(?:-report-only)?\\1[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ViewportMetaRegex = new Regex(
            "<meta[^>]+name\\s*=\\s*(['\"])viewport\\1[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ObjectEmbedRegex = new Regex("<(object|embed)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FlashVarsRegex = new Regex("(flashvars\\s*=|<param[^>]+name\\s*=\\s*(['\"])flashvars\\2)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GameMainPathRegex = new Regex("^(?<prefix>.*?)/pvz/index\\.php/default/main/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _assetRootPath;
        private readonly object _stateLock = new object();
        private readonly Dictionary<string, string> _domainCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _mappingHosts = new List<string>();
        private readonly List<string> _mappingUrlKeywords = new List<string>();
        private static readonly HashSet<string> EmptyArgumentAmfTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "api.duty.getAll",
            "api.active.getState",
            "api.apiorganism.getEvolutionOrgs"
        };
        private string _cacheRootPath;
        private readonly string _amfDumpRootPath;
        private HttpListener _listener;
        private CancellationTokenSource _cancellation;
        private string _upstreamProxy;
        private int _amfDumpSequence;

        internal sealed class RuffleResolvedResponse
        {
            internal RuffleResolvedResponse(int statusCode, string reasonPhrase, string contentType, byte[] body, IDictionary<string, string> headers = null)
            {
                StatusCode = statusCode;
                ReasonPhrase = string.IsNullOrWhiteSpace(reasonPhrase) ? "OK" : reasonPhrase;
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
                Body = body ?? Array.Empty<byte>();
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> entry in headers)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Key))
                        {
                            Headers[entry.Key] = entry.Value ?? string.Empty;
                        }
                    }
                }
            }

            internal int StatusCode { get; }

            internal string ReasonPhrase { get; }

            internal string ContentType { get; }

            internal byte[] Body { get; }

            internal Dictionary<string, string> Headers { get; }

            internal string BuildHeaderString()
            {
                var builder = new StringBuilder();
                builder.Append("Content-Type: ").Append(ContentType).Append("\r\n");
                builder.Append("Content-Length: ").Append(Body.LongLength).Append("\r\n");
                foreach (KeyValuePair<string, string> header in Headers)
                {
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    builder.Append(header.Key).Append(": ").Append(header.Value ?? string.Empty).Append("\r\n");
                }

                return builder.ToString();
            }
        }

        private sealed class GameMainShellInfo
        {
            internal string PathPrefix { get; set; }

            internal string BaseUrl { get; set; }

            internal string BaseUrlInfo { get; set; }

            internal string SwfUrl { get; set; }
        }

        private sealed class AmfPacketInfo
        {
            internal string Target { get; set; }

            internal ushort Version { get; set; }

            internal ushort MessageCount { get; set; }

            internal int BodyOffset { get; set; }

            internal int BodyLength { get; set; }
        }

        internal RuffleLocalProxy(string assetRootPath, string upstreamProxy)
        {
            _assetRootPath = assetRootPath;
            _upstreamProxy = upstreamProxy ?? string.Empty;
            _cacheRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            _amfDumpRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "amf_dumps");
        }

        internal Uri BaseUri { get; private set; }

        internal bool IsRunning => _listener != null && _listener.IsListening;

        internal void SetUpstreamProxy(string upstreamProxy)
        {
            _upstreamProxy = upstreamProxy ?? string.Empty;
            RuntimeDiagnostics.Write("ruffle", $"upstream proxy updated value={_upstreamProxy}");
        }

        internal void SetCookieHeader(Uri domainUri, string cookieHeader)
        {
            if (domainUri == null || string.IsNullOrWhiteSpace(domainUri.Host))
            {
                return;
            }

            lock (_stateLock)
            {
                _domainCookies[domainUri.Host] = cookieHeader ?? string.Empty;
            }

            RuntimeDiagnostics.Write("ruffle-cookie", $"cookie updated host={domainUri.Host} length={(cookieHeader ?? string.Empty).Length}");
        }

        internal void ClearCookieHeaders()
        {
            lock (_stateLock)
            {
                _domainCookies.Clear();
            }

            RuntimeDiagnostics.Write("ruffle-cookie", "cleared proxy cookie headers");
        }

        internal void ConfigureLocalMapping(string cacheRootPath, IEnumerable<string> mappingHosts, IEnumerable<string> mappingUrlKeywords)
        {
            lock (_stateLock)
            {
                _cacheRootPath = string.IsNullOrWhiteSpace(cacheRootPath)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache")
                    : cacheRootPath;

                _mappingHosts.Clear();
                _mappingHosts.AddRange((mappingHosts ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToLowerInvariant()));

                _mappingUrlKeywords.Clear();
                _mappingUrlKeywords.AddRange((mappingUrlKeywords ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToLowerInvariant()));
            }

            RuntimeDiagnostics.Write(
                "ruffle-localmap",
                $"configured cacheRoot={_cacheRootPath} hosts={_mappingHosts.Count} keywords={_mappingUrlKeywords.Count}");
        }

        internal void Start()
        {
            if (IsRunning)
            {
                return;
            }

            int port = FindAvailablePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");

            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            _listener.Start();

            _cancellation = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoopAsync(_cancellation.Token));
        }

        internal Uri ProxyUrlFor(Uri originalUri)
        {
            if (originalUri == null)
            {
                throw new ArgumentNullException(nameof(originalUri));
            }

            var builder = new UriBuilder(BaseUri)
            {
                Path = $"{ProxyPrefix.TrimStart('/')}{originalUri.Scheme}/{originalUri.Authority}{originalUri.AbsolutePath}",
                Query = originalUri.Query.TrimStart('?')
            };
            return builder.Uri;
        }

        internal Uri PlayerUrlFor(Uri originalUri)
        {
            if (originalUri == null)
            {
                throw new ArgumentNullException(nameof(originalUri));
            }

            return new Uri(BaseUri, $"{PlayerPrefix.TrimStart('/')}?url={Uri.EscapeDataString(originalUri.AbsoluteUri)}");
        }

        internal bool TryGetOriginalUri(Uri managedUri, out Uri originalUri)
        {
            originalUri = null;
            if (managedUri == null || BaseUri == null)
            {
                return false;
            }

            if (!Uri.Compare(
                    new Uri(BaseUri.GetLeftPart(UriPartial.Authority)),
                    new Uri(managedUri.GetLeftPart(UriPartial.Authority)),
                    UriComponents.AbsoluteUri,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase).Equals(0))
            {
                return false;
            }

            string path = managedUri.AbsolutePath;
            if (path.StartsWith(PlayerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string encoded = ParseQuery(managedUri.Query).TryGetValue("url", out string value) ? value : string.Empty;
                if (Uri.TryCreate(encoded, UriKind.Absolute, out Uri playerUri))
                {
                    originalUri = playerUri;
                    return true;
                }

                return false;
            }

            if (!path.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string remainder = path.Substring(ProxyPrefix.Length);
            int slashIndex = remainder.IndexOf('/');
            if (slashIndex <= 0)
            {
                return false;
            }

            string scheme = remainder.Substring(0, slashIndex);
            string authorityAndPath = remainder.Substring(slashIndex + 1);
            int nextSlashIndex = authorityAndPath.IndexOf('/');
            string authority = nextSlashIndex >= 0 ? authorityAndPath.Substring(0, nextSlashIndex) : authorityAndPath;
            string absolutePath = nextSlashIndex >= 0 ? authorityAndPath.Substring(nextSlashIndex) : "/";

            var builder = new UriBuilder
            {
                Scheme = scheme,
                Host = authority
            };

            if (authority.Contains(":"))
            {
                int portSeparator = authority.LastIndexOf(':');
                if (portSeparator > 0 && int.TryParse(authority.Substring(portSeparator + 1), out int explicitPort))
                {
                    builder.Host = authority.Substring(0, portSeparator);
                    builder.Port = explicitPort;
                }
            }

            builder.Path = absolutePath;
            builder.Query = managedUri.Query.TrimStart('?');
            originalUri = builder.Uri;
            return true;
        }

        internal Uri GetDisplayUri(Uri currentUri)
        {
            return TryGetOriginalUri(currentUri, out Uri originalUri) ? originalUri : currentUri;
        }

        internal async Task<RuffleResolvedResponse> TryHandleRequestAsync(
            Uri requestUri,
            string httpMethod,
            IDictionary<string, string> requestHeaders,
            byte[] requestBody)
        {
            if (requestUri == null)
            {
                return null;
            }

            string path = requestUri.AbsolutePath ?? "/";
            if (path.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAssetResponse(requestUri);
            }

            if (path.StartsWith(PlayerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BuildPlayerResponse(requestUri);
            }

            if (path.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(httpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateEmptyResponse(204, "No Content", BuildCorsHeaders());
                }

                if (!TryGetOriginalUri(requestUri, out Uri proxyTarget) || proxyTarget == null)
                {
                    RuntimeDiagnostics.Write("ruffle-proxy", $"invalid target url={requestUri}");
                    return CreateTextResponse(400, "Bad Request", "Invalid target");
                }

                if (TryBuildWrappedGameMainResponse(proxyTarget, out RuffleResolvedResponse wrappedProxyResponse))
                {
                    return wrappedProxyResponse;
                }

                RuntimeDiagnostics.Write("ruffle-proxy", $"intercept proxy target={proxyTarget}");
                if (TryBuildLocalFileResponse(proxyTarget, out RuffleResolvedResponse localProxyResponse))
                {
                    return localProxyResponse;
                }

                return await ProxyUpstreamRequestAsync(proxyTarget, httpMethod, requestHeaders, requestBody, true).ConfigureAwait(false);
            }

            if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!ShouldInterceptDirectRequest(requestUri, httpMethod, requestHeaders, requestBody))
            {
                return null;
            }

            if (TryBuildWrappedGameMainResponse(requestUri, out RuffleResolvedResponse wrappedResponse))
            {
                return wrappedResponse;
            }

            if (TryBuildLocalFileResponse(requestUri, out RuffleResolvedResponse localResponse))
            {
                return localResponse;
            }

            return await ProxyUpstreamRequestAsync(requestUri, httpMethod, requestHeaders, requestBody, false).ConfigureAwait(false);
        }

        private async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }

                _ = Task.Run(() => HandleContext(context), cancellationToken);
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath ?? "/";
                RuntimeDiagnostics.Write(
                    "ruffle-proxy",
                    $"incoming method={context.Request.HttpMethod} path={path} query={context.Request.Url.Query}");
                if (path.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ServeAsset(context);
                    return;
                }

                if (path.StartsWith(PlayerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ServePlayer(context);
                    return;
                }

                if (path.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ServeProxy(context);
                    return;
                }

                WriteResponse(context.Response, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"));
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"context error={ex}");
                WriteResponse(context.Response, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
            }
        }

        private static bool IsDirectInterceptMethod(string httpMethod)
        {
            return string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldInterceptDirectRequest(
            Uri requestUri,
            string httpMethod,
            IDictionary<string, string> requestHeaders,
            byte[] requestBody)
        {
            if (IsDirectInterceptMethod(httpMethod))
            {
                return true;
            }

            if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string contentType = string.Empty;
            if (requestHeaders != null && requestHeaders.TryGetValue("Content-Type", out string headerContentType))
            {
                contentType = headerContentType ?? string.Empty;
            }

            bool looksLikeAmf = contentType.IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0
                || TryParseAmfPacketInfo(requestBody) != null;
            if (looksLikeAmf)
            {
                RuntimeDiagnostics.Write(
                    "ruffle-amf",
                    $"direct intercept method={httpMethod} url={requestUri} type={contentType} bodyBytes={(requestBody == null ? 0 : requestBody.Length)}");
            }

            return looksLikeAmf;
        }

        private static RuffleResolvedResponse CreateTextResponse(int statusCode, string reasonPhrase, string body)
        {
            return new RuffleResolvedResponse(
                statusCode,
                reasonPhrase,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes(body ?? string.Empty),
                BuildCorsHeaders());
        }

        private static RuffleResolvedResponse CreateEmptyResponse(int statusCode, string reasonPhrase, IDictionary<string, string> headers)
        {
            return new RuffleResolvedResponse(statusCode, reasonPhrase, "text/plain; charset=utf-8", Array.Empty<byte>(), headers);
        }

        private static Dictionary<string, string> BuildCorsHeaders()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Access-Control-Allow-Origin"] = "*",
                ["Access-Control-Allow-Methods"] = "GET, HEAD, POST, OPTIONS",
                ["Access-Control-Allow-Headers"] = "*",
                ["Cross-Origin-Resource-Policy"] = "cross-origin",
                ["Cache-Control"] = "no-cache"
            };
        }

        private RuffleResolvedResponse BuildAssetResponse(Uri requestUri)
        {
            string relativePath = requestUri.AbsolutePath.Substring(AssetPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(_assetRootPath, relativePath);
            if (!File.Exists(fullPath))
            {
                RuntimeDiagnostics.Write("ruffle-asset", $"missing path={fullPath}");
                return CreateTextResponse(404, "Not Found", "Missing asset");
            }

            byte[] body = File.ReadAllBytes(fullPath);
            RuntimeDiagnostics.Write("ruffle-asset", $"serve path={fullPath} bytes={body.Length}");
            return new RuffleResolvedResponse(200, "OK", GuessMimeType(fullPath), body, BuildCorsHeaders());
        }

        private RuffleResolvedResponse BuildPlayerResponse(Uri requestUri)
        {
            var query = ParseQuery(requestUri.Query);
            if (!query.TryGetValue("url", out string originalUrl) || !Uri.TryCreate(originalUrl, UriKind.Absolute, out Uri originalUri))
            {
                RuntimeDiagnostics.Write("ruffle-player", $"invalid target query={requestUri.Query}");
                return CreateTextResponse(400, "Bad Request", "Invalid SWF target");
            }

            RuntimeDiagnostics.Write("ruffle-player", $"serve player target={originalUri}");
            return new RuffleResolvedResponse(
                200,
                "OK",
                "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(BuildSwfPlayerHtml(originalUri)),
                BuildCorsHeaders());
        }

        private bool TryBuildLocalFileResponse(Uri originalUri, out RuffleResolvedResponse response)
        {
            response = null;
            string cacheRootPath;
            List<string> mappingHosts;
            List<string> mappingUrlKeywords;
            lock (_stateLock)
            {
                cacheRootPath = _cacheRootPath;
                mappingHosts = new List<string>(_mappingHosts);
                mappingUrlKeywords = new List<string>(_mappingUrlKeywords);
            }

            if (string.IsNullOrWhiteSpace(cacheRootPath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip empty cache root target={originalUri}");
                return false;
            }

            string hostLower = (originalUri.Host ?? string.Empty).ToLowerInvariant();
            string absoluteUrlLower = originalUri.AbsoluteUri.ToLowerInvariant();
            bool matches = mappingHosts.Any(value => !string.IsNullOrWhiteSpace(value) && hostLower.Contains(value))
                || mappingUrlKeywords.Any(value => !string.IsNullOrWhiteSpace(value) && absoluteUrlLower.Contains(value));
            if (!matches)
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip no rule target={originalUri}");
                return false;
            }

            string relativePath = SanitizeRelativePath(originalUri.PathAndQuery);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip invalid relative path target={originalUri}");
                return false;
            }

            string localPath = Path.Combine(cacheRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            RuntimeDiagnostics.Write("ruffle-localmap", $"lookup target={originalUri} relative={relativePath} file={localPath}");
            if (!File.Exists(localPath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"miss target={originalUri} file={localPath}");
                return false;
            }

            byte[] body = File.ReadAllBytes(localPath);
            RuntimeDiagnostics.Write("ruffle-localmap", $"serve target={originalUri} file={localPath} bytes={body.Length}");
            response = new RuffleResolvedResponse(200, "OK", GuessMimeType(localPath), body, BuildCorsHeaders());
            return true;
        }

        private async Task<RuffleResolvedResponse> ProxyUpstreamRequestAsync(
            Uri originalUri,
            string httpMethod,
            IDictionary<string, string> requestHeaders,
            byte[] requestBody,
            bool addCorsHeaders)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(originalUri);
                request.Method = string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod;
                request.AllowAutoRedirect = true;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.UserAgent = IeUserAgent;
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.Proxy = BuildUpstreamProxy(originalUri);
                ApplyConfiguredCookies(request, originalUri);

                foreach (KeyValuePair<string, string> header in requestHeaders ?? new Dictionary<string, string>())
                {
                    if (string.IsNullOrWhiteSpace(header.Key))
                    {
                        continue;
                    }

                    string lowerName = header.Key.ToLowerInvariant();
                    if (lowerName == "host"
                        || lowerName == "connection"
                        || lowerName == "content-length"
                        || lowerName == "accept-encoding"
                        || lowerName == "proxy-connection"
                        || lowerName == "cookie")
                    {
                        continue;
                    }

                    string headerValue = NormalizeForwardedHeaderValue(header.Key, header.Value);
                    ApplyHeader(request, header.Key, headerValue);
                }

                string requestContentType = request.ContentType
                    ?? (requestHeaders != null && requestHeaders.TryGetValue("Content-Type", out string contentType) ? contentType : string.Empty)
                    ?? string.Empty;
                AmfPacketInfo amfInfo = TryParseAmfPacketInfo(requestBody);
                bool looksLikeAmf = requestContentType.IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0
                    || amfInfo != null;
                if (looksLikeAmf)
                {
                    requestBody = RewriteKnownBrokenAmfRequest(requestBody, originalUri, amfInfo);
                    amfInfo = TryParseAmfPacketInfo(requestBody);
                }
                RuntimeDiagnostics.Write(
                    looksLikeAmf ? "ruffle-amf" : "ruffle-upstream",
                    $"request method={request.Method} url={originalUri} type={requestContentType} bodyBytes={(requestBody == null ? 0 : requestBody.Length)} target={(amfInfo == null ? string.Empty : amfInfo.Target)} proxy={(request.Proxy == null ? "direct" : request.Proxy.GetProxy(originalUri).ToString())}");
                if (looksLikeAmf)
                {
                    WriteAmfDump("request", request.Method, originalUri, requestContentType, requestBody, amfInfo == null ? null : amfInfo.Target);
                }

                if (requestBody != null && requestBody.Length > 0)
                {
                    using (Stream output = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    {
                        await output.WriteAsync(requestBody, 0, requestBody.Length).ConfigureAwait(false);
                    }
                }

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    return await BuildUpstreamResponseAsync(response, originalUri, addCorsHeaders, looksLikeAmf, amfInfo == null ? null : amfInfo.Target).ConfigureAwait(false);
                }
            }
            catch (WebException ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"web exception status={ex.Status} target={originalUri} message={ex.Message}");
                if (ex.Response is HttpWebResponse failedResponse)
                {
                    using (failedResponse)
                    {
                        return await BuildUpstreamResponseAsync(failedResponse, originalUri, addCorsHeaders, false, null).ConfigureAwait(false);
                    }
                }

                return CreateTextResponse(502, "Bad Gateway", ex.Message);
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"upstream error target={originalUri} error={ex}");
                return CreateTextResponse(500, "Internal Server Error", ex.Message);
            }
        }

        private async Task<RuffleResolvedResponse> BuildUpstreamResponseAsync(HttpWebResponse response, Uri originalUri, bool addCorsHeaders, bool forceAmfLogging, string amfTarget)
        {
            byte[] body;
            using (var memoryStream = new MemoryStream())
            using (Stream responseStream = response.GetResponseStream())
            {
                if (responseStream != null)
                {
                    await responseStream.CopyToAsync(memoryStream).ConfigureAwait(false);
                }
                body = memoryStream.ToArray();
            }

            string contentType = response.ContentType ?? "application/octet-stream";
            bool looksLikeAmf = forceAmfLogging || contentType.IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0;
            RuntimeDiagnostics.Write(
                looksLikeAmf ? "ruffle-amf" : "ruffle-upstream",
                $"response status={(int)response.StatusCode} type={contentType} bytes={body.Length} target={originalUri} amfTarget={(amfTarget ?? string.Empty)}");
            if (looksLikeAmf)
            {
                WriteAmfDump("response", response.StatusCode.ToString(), originalUri, contentType, body, string.IsNullOrWhiteSpace(amfTarget) ? response.StatusDescription : amfTarget);
            }
            if (IsHtmlContentType(contentType) && ShouldInjectBootstrapForHtml(originalUri, body))
            {
                RuntimeDiagnostics.Write("ruffle-upstream", $"inject bootstrap target={originalUri}");
                body = InjectBootstrap(body, originalUri, contentType);
                contentType = "text/html; charset=utf-8";
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (addCorsHeaders)
            {
                foreach (KeyValuePair<string, string> header in BuildCorsHeaders())
                {
                    headers[header.Key] = header.Value;
                }
            }
            else
            {
                headers["Cache-Control"] = "no-cache";
            }

            foreach (string headerName in response.Headers.AllKeys ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(headerName))
                {
                    continue;
                }

                string lowerName = headerName.ToLowerInvariant();
                if (lowerName == "content-length"
                    || lowerName == "content-type"
                    || lowerName == "transfer-encoding"
                    || lowerName == "content-security-policy"
                    || lowerName == "content-security-policy-report-only"
                    || lowerName == "x-frame-options"
                    || lowerName == "connection")
                {
                    continue;
                }

                headers[headerName] = response.Headers[headerName];
            }

            return new RuffleResolvedResponse(
                (int)response.StatusCode,
                SanitizeReason(response.StatusDescription),
                contentType,
                body,
                headers);
        }

        private void ServeAsset(HttpListenerContext context)
        {
            string relativePath = context.Request.Url.AbsolutePath.Substring(AssetPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(_assetRootPath, relativePath);
            if (!File.Exists(fullPath))
            {
                RuntimeDiagnostics.Write("ruffle-asset", $"missing path={fullPath}");
                WriteResponse(context.Response, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Missing asset"));
                return;
            }

            byte[] body = File.ReadAllBytes(fullPath);
            RuntimeDiagnostics.Write("ruffle-asset", $"serve path={fullPath} bytes={body.Length}");
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "*";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            WriteResponse(context.Response, 200, GuessMimeType(fullPath), body, context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
        }

        private void ServePlayer(HttpListenerContext context)
        {
            var query = ParseQuery(context.Request.Url.Query);
            if (!query.TryGetValue("url", out string originalUrl) || !Uri.TryCreate(originalUrl, UriKind.Absolute, out Uri originalUri))
            {
                RuntimeDiagnostics.Write("ruffle-player", $"invalid target query={context.Request.Url.Query}");
                WriteResponse(context.Response, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid SWF target"));
                return;
            }

            RuntimeDiagnostics.Write("ruffle-player", $"serve player target={originalUri}");
            byte[] body = Encoding.UTF8.GetBytes(BuildSwfPlayerHtml(originalUri));
            WriteResponse(context.Response, 200, "text/html; charset=utf-8", body, context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
        }

        private void ServeProxy(HttpListenerContext context)
        {
            if (!TryGetOriginalUri(context.Request.Url, out Uri originalUri) || originalUri == null)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"invalid target url={context.Request.Url}");
                WriteResponse(context.Response, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid target"));
                return;
            }

            try
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"proxy target={originalUri}");
                if (TryServeLocalFile(context, originalUri))
                {
                    return;
                }

                ProxyUpstreamRequest(context, originalUri);
            }
            catch (WebException ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"web exception status={ex.Status} target={originalUri} message={ex.Message}");
                if (ex.Response is HttpWebResponse failedResponse)
                {
                    ProxyUpstreamResponse(context, failedResponse, originalUri);
                    return;
                }

                WriteResponse(context.Response, 502, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
            }
            catch (Exception ex)
            {
                WriteResponse(context.Response, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
            }
        }

        private bool TryServeLocalFile(HttpListenerContext context, Uri originalUri)
        {
            string cacheRootPath;
            List<string> mappingHosts;
            List<string> mappingUrlKeywords;
            lock (_stateLock)
            {
                cacheRootPath = _cacheRootPath;
                mappingHosts = new List<string>(_mappingHosts);
                mappingUrlKeywords = new List<string>(_mappingUrlKeywords);
            }

            if (string.IsNullOrWhiteSpace(cacheRootPath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip empty cache root target={originalUri}");
                return false;
            }

            string hostLower = (originalUri.Host ?? string.Empty).ToLowerInvariant();
            string absoluteUrlLower = originalUri.AbsoluteUri.ToLowerInvariant();
            bool matches = mappingHosts.Any(value => !string.IsNullOrWhiteSpace(value) && hostLower.Contains(value))
                || mappingUrlKeywords.Any(value => !string.IsNullOrWhiteSpace(value) && absoluteUrlLower.Contains(value));
            if (!matches)
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip no rule target={originalUri}");
                return false;
            }

            string relativePath = SanitizeRelativePath(originalUri.PathAndQuery);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"skip invalid relative path target={originalUri}");
                return false;
            }

            string localPath = Path.Combine(cacheRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            RuntimeDiagnostics.Write("ruffle-localmap", $"lookup target={originalUri} relative={relativePath} file={localPath}");
            if (!File.Exists(localPath))
            {
                RuntimeDiagnostics.Write("ruffle-localmap", $"miss target={originalUri} file={localPath}");
                return false;
            }

            byte[] body = File.ReadAllBytes(localPath);
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "*";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            RuntimeDiagnostics.Write("ruffle-localmap", $"serve target={originalUri} file={localPath} bytes={body.Length}");
            WriteResponse(
                context.Response,
                200,
                GuessMimeType(localPath),
                body,
                context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
            return true;
        }

        private void ProxyUpstreamRequest(HttpListenerContext context, Uri originalUri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(originalUri);
            request.Method = context.Request.HttpMethod;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.UserAgent = IeUserAgent;
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.Proxy = BuildUpstreamProxy(originalUri);
            ApplyConfiguredCookies(request, originalUri);

            foreach (string headerName in context.Request.Headers.AllKeys ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(headerName))
                {
                    continue;
                }

                string lowerName = headerName.ToLowerInvariant();
                if (lowerName == "host"
                    || lowerName == "connection"
                    || lowerName == "content-length"
                    || lowerName == "accept-encoding"
                    || lowerName == "proxy-connection"
                    || lowerName == "cookie")
                {
                    continue;
                }

                string headerValue = NormalizeForwardedHeaderValue(headerName, context.Request.Headers[headerName]);
                ApplyHeader(request, headerName, headerValue);
            }

            byte[] requestBody = null;
            if (context.Request.HasEntityBody)
            {
                using (Stream input = context.Request.InputStream)
                {
                    using (var buffer = new MemoryStream())
                    {
                        input.CopyTo(buffer);
                        requestBody = buffer.ToArray();
                    }
                }
            }

            string requestContentType = request.ContentType ?? context.Request.ContentType ?? string.Empty;
            bool looksLikeAmf = requestContentType.IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0;
            RuntimeDiagnostics.Write(
                looksLikeAmf ? "ruffle-amf" : "ruffle-upstream",
                $"request method={request.Method} url={originalUri} type={requestContentType} bodyBytes={(requestBody == null ? 0 : requestBody.Length)} proxy={(request.Proxy == null ? "direct" : request.Proxy.GetProxy(originalUri).ToString())}");

            if (requestBody != null && requestBody.Length > 0)
            {
                using (Stream output = request.GetRequestStream())
                {
                    output.Write(requestBody, 0, requestBody.Length);
                }
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                ProxyUpstreamResponse(context, response, originalUri);
            }
        }

        private void ProxyUpstreamResponse(HttpListenerContext context, HttpWebResponse response, Uri originalUri)
        {
            byte[] body;
            using (var memoryStream = new MemoryStream())
            using (Stream responseStream = response.GetResponseStream())
            {
                responseStream?.CopyTo(memoryStream);
                body = memoryStream.ToArray();
            }

            string contentType = response.ContentType ?? "application/octet-stream";
            RuntimeDiagnostics.Write(
                contentType.IndexOf("application/x-amf", StringComparison.OrdinalIgnoreCase) >= 0 ? "ruffle-amf" : "ruffle-upstream",
                $"response status={(int)response.StatusCode} type={contentType} bytes={body.Length} target={originalUri}");
            if (IsHtmlContentType(contentType) && ShouldInjectBootstrapForHtml(originalUri, body))
            {
                RuntimeDiagnostics.Write("ruffle-upstream", $"inject bootstrap target={originalUri}");
                body = InjectBootstrap(body, originalUri, contentType);
                contentType = "text/html; charset=utf-8";
            }

            HttpListenerResponse output = context.Response;
            output.StatusCode = (int)response.StatusCode;
            output.ContentType = contentType;
            output.Headers["Cache-Control"] = "no-cache";
            output.Headers["Access-Control-Allow-Origin"] = "*";
            output.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            output.Headers["Access-Control-Allow-Headers"] = "*";

            foreach (string headerName in response.Headers.AllKeys ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(headerName))
                {
                    continue;
                }

                string lowerName = headerName.ToLowerInvariant();
                if (lowerName == "content-length"
                    || lowerName == "content-type"
                    || lowerName == "transfer-encoding"
                    || lowerName == "content-security-policy"
                    || lowerName == "content-security-policy-report-only"
                    || lowerName == "x-frame-options"
                    || lowerName == "connection")
                {
                    continue;
                }

                try
                {
                    output.Headers[headerName] = response.Headers[headerName];
                }
                catch
                {
                }
            }

            WriteResponse(output, output.StatusCode, contentType, body, context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
        }

        private IWebProxy BuildUpstreamProxy(Uri originalUri)
        {
            if (originalUri == null || string.IsNullOrWhiteSpace(_upstreamProxy))
            {
                return null;
            }

            string value = _upstreamProxy.Trim();
            if (!value.Contains("://"))
            {
                value = "http://" + value;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri proxyUri))
            {
                RuntimeDiagnostics.Write("ruffle-upstream", $"invalid upstream proxy value={_upstreamProxy}");
                return null;
            }

            return new WebProxy(proxyUri);
        }

        private void ApplyConfiguredCookies(HttpWebRequest request, Uri originalUri)
        {
            string cookieHeader = FindCookieHeader(originalUri);
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return;
            }

            var container = new CookieContainer();
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

                try
                {
                    container.Add(new Cookie(name, value, "/", originalUri.Host));
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Write("ruffle-cookie", $"skip invalid cookie host={originalUri.Host} name={name} error={ex.Message}");
                }
            }

            request.CookieContainer = container;
            RuntimeDiagnostics.Write("ruffle-cookie", $"attached cookies host={originalUri.Host} headerLength={cookieHeader.Length}");
        }

        private string FindCookieHeader(Uri originalUri)
        {
            if (originalUri == null || string.IsNullOrWhiteSpace(originalUri.Host))
            {
                return string.Empty;
            }

            string host = originalUri.Host;
            lock (_stateLock)
            {
                if (_domainCookies.TryGetValue(host, out string exactValue))
                {
                    return exactValue;
                }

                foreach (KeyValuePair<string, string> entry in _domainCookies)
                {
                    if (host.EndsWith("." + entry.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
            }

            return string.Empty;
        }

        private string NormalizeForwardedHeaderValue(string headerName, string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return string.Empty;
            }

            string lowerName = headerName.ToLowerInvariant();
            if (lowerName != "referer" && lowerName != "origin")
            {
                return headerValue;
            }

            if (!Uri.TryCreate(headerValue, UriKind.Absolute, out Uri candidate))
            {
                return headerValue;
            }

            Uri translated = GetDisplayUri(candidate);
            if (translated == null)
            {
                return headerValue;
            }

            if (lowerName == "origin")
            {
                return translated.GetLeftPart(UriPartial.Authority);
            }

            return translated.AbsoluteUri;
        }

        private static void ApplyHeader(HttpWebRequest request, string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "accept":
                    request.Accept = value;
                    break;
                case "content-type":
                    request.ContentType = value;
                    break;
                case "referer":
                    request.Referer = value;
                    break;
                case "user-agent":
                    request.UserAgent = value;
                    break;
                default:
                    request.Headers[name] = value;
                    break;
            }
        }

        private byte[] InjectBootstrap(byte[] body, Uri originalUri, string contentType)
        {
            string html = DecodeBody(body, contentType);
            string cleanedHtml = CspMetaRegex.Replace(html, string.Empty);
            bool likelyFlashPage = ContainsFlashMarkup(cleanedHtml);
            int flashElementCount = ObjectEmbedRegex.Matches(cleanedHtml).Count;
            int flashVarsCount = FlashVarsRegex.Matches(cleanedHtml).Count;
            RuntimeDiagnostics.Write(
                "ruffle-upstream",
                $"html diagnostics target={originalUri} likelyFlash={likelyFlashPage} objectEmbedCount={flashElementCount} flashVarsCount={flashVarsCount}");
            string normalizedHtml = AddOrReplaceBaseHref(cleanedHtml, originalUri);
            bool injectLegacyViewport = !likelyFlashPage;
            if (injectLegacyViewport)
            {
                normalizedHtml = ViewportMetaRegex.Replace(normalizedHtml, string.Empty);
            }

            string viewportTag = injectLegacyViewport
                ? "<meta name=\"viewport\" content=\"width=" + LegacyPageMinViewportWidth + ", initial-scale=1, minimum-scale=0.25, maximum-scale=5, user-scalable=yes\">"
                    + "<style>html,body{max-width:100%;overflow-x:auto;overflow-y:auto;-webkit-overflow-scrolling:touch;}</style>"
                : string.Empty;
            string bootstrapTag = viewportTag
                + $"<script>{BuildRuffleConfigScript()}</script>"
                + $"<script>{BuildPageCompatScript(injectLegacyViewport)}</script>"
                + $"<script src=\"{AssetPrefix}bootstrap.windows-heavy.bak.js\"></script>";
            if (normalizedHtml.IndexOf(bootstrapTag, StringComparison.Ordinal) >= 0)
            {
                return Encoding.UTF8.GetBytes(normalizedHtml);
            }

            if (HeadRegex.IsMatch(normalizedHtml))
            {
                normalizedHtml = HeadRegex.Replace(normalizedHtml, bootstrapTag + "</head>", 1);
            }
            else
            {
                normalizedHtml = bootstrapTag + normalizedHtml;
            }

            return Encoding.UTF8.GetBytes(normalizedHtml);
        }

        private static string AddOrReplaceBaseHref(string html, Uri originalUri)
        {
            if (BaseRegex.IsMatch(html))
            {
                return html;
            }

            string baseTag = $"<base href=\"{originalUri.AbsoluteUri}\">";
            if (HeadRegex.IsMatch(html))
            {
                return HeadRegex.Replace(html, baseTag + "</head>", 1);
            }

            Match htmlMatch = HtmlRegex.Match(html);
            if (htmlMatch.Success)
            {
                return html.Insert(htmlMatch.Index + htmlMatch.Length, baseTag);
            }

            return baseTag + html;
        }

        private string BuildSwfPlayerHtml(Uri originalUri)
        {
            string sourceUrl = ProxyUrlFor(originalUri).AbsoluteUri;
            string localRuffleUrl = AssetPrefix + "ruffle.js";

            return "<!doctype html><html><head><meta charset='utf-8'>"
                + "<meta name='viewport' content='width=device-width, initial-scale=1'>"
                + "<title>PVZOL Flash 播放器</title>"
                + "<style>"
                + "html,body{margin:0;padding:0;height:100%;background:#07111f;color:#e2e8f0;font-family:'Microsoft YaHei UI','PingFang SC','Noto Sans CJK SC',sans-serif;overflow:hidden;}"
                + "body{display:flex;flex-direction:column;}"
                + ".topbar{padding:12px 16px;background:linear-gradient(135deg,#0f172a,#134e4a);font-size:12px;line-height:1.6;word-break:break-all;box-shadow:0 10px 30px rgba(0,0,0,0.2);}"
                + ".status{color:#7dd3fc;}"
                + ".host{flex:1;min-height:0;background:#000;}"
                + "#player-host,#player-host ruffle-player{width:100%;height:100%;}"
                + "ruffle-player,ruffle-embed,ruffle-object{width:100%!important;height:100%!important;max-width:100%!important;max-height:100%!important;}"
                + "</style>"
                + $"<script>{BuildRuffleConfigScript()}</script>"
                + $"<script src='{WebUtility.HtmlEncode(localRuffleUrl)}'></script>"
                + "</head><body>"
                + "<div class='topbar'>"
                + "<div>当前模式：Ruffle SWF 播放</div>"
                + $"<div>源地址：{WebUtility.HtmlEncode(originalUri.AbsoluteUri)}</div>"
                + "<div class='status' id='status'>状态：等待加载</div>"
                + "</div>"
                + "<div class='host'><div id='player-host'></div></div>"
                + "<script>(function(){"
                + "function setStatus(message){var node=document.getElementById('status');if(node){node.textContent='状态：'+message;}}"
                + "function isUsable(response){if(!response||!response.ok){return false;}var type=(response.headers.get('content-type')||'').toLowerCase();var len=response.headers.get('content-length');var parsed=len===null||len===''?null:parseInt(len,10);var zero=len!==null&&!isNaN(parsed)&&parsed<=0;var swf=type.indexOf('application/x-shockwave-flash')!==-1||/\\.swf(\\?.*)?$/i.test(response.url||'');return swf&&!zero;}"
                + "function boot(){try{if(!window.RufflePlayer||typeof window.RufflePlayer.newest!=='function'){setStatus('Ruffle 不可用');return;}var factory=window.RufflePlayer.newest();if(!factory||typeof factory.createPlayer!=='function'){setStatus('Ruffle 工厂不可用');return;}var player=factory.createPlayer();player.style.width='100%';player.style.height='100%';var host=document.getElementById('player-host');host.innerHTML='';host.appendChild(player);try{console.log('[pvzol-ruffle-player]',JSON.stringify({originalUrl:'"
                + JavaScriptEscape(originalUri.AbsoluteUri)
                + "',proxyUrl:'"
                + JavaScriptEscape(sourceUrl)
                + "'}));}catch(e){}setStatus('正在预检 SWF');fetch('"
                + JavaScriptEscape(sourceUrl)
                + "',{method:'HEAD'}).then(function(response){if(!isUsable(response)){setStatus('SWF 响应为空或无效');return null;}setStatus('正在加载');return Promise.resolve(player.load('"
                + JavaScriptEscape(sourceUrl)
                + "')).then(function(){setStatus('已加载');});}).catch(function(error){setStatus('加载失败：'+String(error));});}catch(error){setStatus('异常：'+String(error));}}if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',boot,{once:true});}else{boot();}})();</script>"
                + "</body></html>";
        }

        private string BuildWrappedGameMainHtml(Uri originalUri, GameMainShellInfo shellInfo)
        {
            string flashVars =
                "base_url=" + shellInfo.BaseUrl
                + "&base_url_info=" + shellInfo.BaseUrlInfo;

            return "<!doctype html><html><head><meta charset='utf-8'>"
                + "<meta name='viewport' content='width=760, initial-scale=1, minimum-scale=1, maximum-scale=4, user-scalable=yes'>"
                + $"<base href=\"{WebUtility.HtmlEncode(originalUri.AbsoluteUri)}\">"
                + "<title>PVZOL Browser</title>"
                + "<style>"
                + "html,body{margin:0;padding:0;min-height:100%;background:#ffffff;overflow:auto;font-family:'Microsoft YaHei UI','PingFang SC','Noto Sans CJK SC',sans-serif;}"
                + "body{display:flex;justify-content:center;align-items:flex-start;}"
                + ".page{width:100%;display:flex;justify-content:center;padding:0;box-sizing:border-box;}"
                + ".game-container{position:relative;width:760px;height:535px;min-height:535px;max-height:535px;overflow:hidden;flex:0 0 auto;background:#000;}"
                + ".game-frame{display:block;width:760px;height:535px;min-height:535px;max-height:535px;}"
                + "</style>"
                + $"<script>{BuildRuffleConfigScript()}</script>"
                + $"<script>{BuildPageCompatScript(false)}</script>"
                + $"<script src=\"{AssetPrefix}bootstrap.windows-heavy.bak.js\"></script>"
                + "</head><body>"
                + "<div class='page'><div class='game-container'>"
                + $"<embed class='game-frame' width='760' height='535' quality='high' src='{WebUtility.HtmlEncode(shellInfo.SwfUrl)}' flashvars='{WebUtility.HtmlEncode(flashVars)}'>"
                + "</div></div>"
                + "</body></html>";
        }

        private string BuildRuffleConfigScript()
        {
            return "(function(){"
                + "var ieUa='" + JavaScriptEscape(IeUserAgent) + "';"
                + "try{Object.defineProperty(navigator,'userAgent',{get:function(){return ieUa;},configurable:true});}catch(e){}"
                + "try{Object.defineProperty(navigator,'appVersion',{get:function(){return ieUa;},configurable:true});}catch(e){}"
                + "try{Object.defineProperty(navigator,'appName',{get:function(){return 'Microsoft Internet Explorer';},configurable:true});}catch(e){}"
                + "try{Object.defineProperty(navigator,'platform',{get:function(){return 'Win32';},configurable:true});}catch(e){}"
                + "try{Object.defineProperty(navigator,'vendor',{get:function(){return '';},configurable:true});}catch(e){}"
                + "try{Object.defineProperty(document,'documentMode',{get:function(){return 10;},configurable:true});}catch(e){}"
                + "window.RufflePlayer=window.RufflePlayer||{};"
                + "window.RufflePlayer.config=window.RufflePlayer.config||{};"
                + "var c=window.RufflePlayer.config;"
                + "window.__pvzolRuffleRoot='" + AssetPrefix + "';"
                + "if(typeof window.__pvzolPreferredRenderer==='undefined'){"
                + "if(window.navigator&&('gpu' in navigator)){window.__pvzolPreferredRenderer='webgpu';}"
                + "else if(window.WebGLRenderingContext||window.WebGL2RenderingContext){window.__pvzolPreferredRenderer='wgpu-webgl';}"
                + "else{window.__pvzolPreferredRenderer='canvas';}"
                + "}"
                + "if(typeof window.__pvzolTouchBridgeEnabled==='undefined'){window.__pvzolTouchBridgeEnabled=false;}"
                + "c.publicPath=window.__pvzolRuffleRoot;"
                + "c.allowScriptAccess=true;"
                + "c.allowNetworking='all';"
                + "c.openUrlMode='allow';"
                + "c.logLevel='error';"
                + "c.autoplay='on';"
                + "c.allowFullscreen=true;"
                + "c.polyfills=true;"
                + "c.unmuteOverlay='hidden';"
                + "c.warnOnUnsupportedContent=false;"
                + "c.favorFlash=false;"
                + "var preferredRenderer='';"
                + "try{preferredRenderer=String(window.__pvzolPreferredRenderer||'');}catch(e){}"
                + "if(preferredRenderer){c.preferredRenderer=preferredRenderer;}"
                + "else if(window.navigator&&('gpu' in navigator)){c.preferredRenderer='webgpu';}"
                + "else if(window.WebGLRenderingContext||window.WebGL2RenderingContext){c.preferredRenderer='wgpu-webgl';}"
                + "else{c.preferredRenderer='canvas';}"
                + "c.deviceFontRenderer='canvas';"
                + "c.defaultFonts={"
                + "sans:['Noto Sans CJK SC','Noto Sans SC','Source Han Sans SC','Droid Sans Fallback','sans-serif'],"
                + "serif:['Noto Serif CJK SC','Noto Serif SC','Source Han Serif SC','serif'],"
                + "typewriter:['monospace'],"
                + "japaneseGothic:['Noto Sans CJK SC','Noto Sans SC','Source Han Sans SC','Droid Sans Fallback','sans-serif'],"
                + "japaneseGothicMono:['monospace'],"
                + "japaneseMincho:['Noto Serif CJK SC','Noto Serif SC','Source Han Serif SC','serif']"
                + "};"
                + "try{console.log('[pvzol-ruffle-config]',JSON.stringify({preferredRenderer:c.preferredRenderer,deviceFontRenderer:c.deviceFontRenderer,allowScriptAccess:c.allowScriptAccess,allowNetworking:c.allowNetworking,openUrlMode:c.openUrlMode,touchBridgeEnabled:window.__pvzolTouchBridgeEnabled}));}catch(e){}"
                + "})();";
        }

        private string BuildPageCompatScript(bool legacyViewportMode)
        {
            return "(function(){"
                + "var legacyMode=" + (legacyViewportMode ? "true" : "false") + ";"
                + "var legacyRefreshTimer=0;"
                + "function preparePage(){"
                + "try{document.documentElement.style.maxWidth='100%';document.documentElement.style.overflowX='auto';document.documentElement.style.overflowY='auto';}catch(e){}"
                + "try{document.documentElement.style.visibility='visible';}catch(e){}"
                + "try{if(document.body){document.body.style.maxWidth='100%';document.body.style.overflowX='auto';document.body.style.overflowY='auto';document.body.style.webkitOverflowScrolling='touch';document.body.style.visibility='visible';document.body.style.opacity='1';}}catch(e){}"
                + "}"
                + "function ensureViewportTag(){"
                + "var meta=document.querySelector('meta[name=\"viewport\"]');"
                + "if(meta){return meta;}"
                + "meta=document.createElement('meta');"
                + "meta.setAttribute('name','viewport');"
                + "var head=document.head||document.getElementsByTagName('head')[0]||document.documentElement;"
                + "if(head.firstChild){head.insertBefore(meta,head.firstChild);}else{head.appendChild(meta);}"
                + "return meta;"
                + "}"
                + "function measureContentWidth(){"
                + "var width=" + LegacyPageMinViewportWidth + ";"
                + "try{if(window.innerWidth){width=Math.max(width,Math.ceil(window.innerWidth));}}catch(e){}"
                + "try{if(document.documentElement){width=Math.max(width,Math.ceil(document.documentElement.scrollWidth||0));width=Math.max(width,Math.ceil(document.documentElement.getBoundingClientRect().width||0));}}catch(e){}"
                + "try{if(document.body){width=Math.max(width,Math.ceil(document.body.scrollWidth||0));width=Math.max(width,Math.ceil(document.body.getBoundingClientRect().width||0));}}catch(e){}"
                + "try{var nodes=document.querySelectorAll('table,img,object,embed,iframe,canvas,svg,div,section,article,form,body>*');"
                + "var limit=Math.min(nodes.length,200);"
                + "for(var i=0;i<limit;i++){var node=nodes[i];if(!node||!node.getBoundingClientRect){continue;}var rect=node.getBoundingClientRect();if(!rect){continue;}width=Math.max(width,Math.ceil(rect.left+rect.width));}}catch(e){}"
                + "width=Math.max(" + LegacyPageMinViewportWidth + ",Math.min(" + LegacyPageMaxViewportWidth + ",width));"
                + "return width;"
                + "}"
                + "function applyLegacyViewport(){"
                + "preparePage();"
                + "try{var width=measureContentWidth();var meta=ensureViewportTag();if(window.__flashBrowserLegacyViewportWidth!==width){meta.setAttribute('content','width='+width+', initial-scale=1, minimum-scale=0.25, maximum-scale=5, user-scalable=yes');window.__flashBrowserLegacyViewportWidth=width;}}catch(e){}"
                + "preparePage();"
                + "}"
                + "function refresh(){if(legacyMode){applyLegacyViewport();}else{preparePage();}}"
                + "function scheduleLegacyRefresh(){"
                + "if(!legacyMode){return;}"
                + "try{if(legacyRefreshTimer){window.clearTimeout(legacyRefreshTimer);}}catch(e){}"
                + "legacyRefreshTimer=window.setTimeout(function(){legacyRefreshTimer=0;refresh();},120);"
                + "}"
                + "window.__flashBrowserRefreshPageCompat=refresh;"
                + "if(!window.__flashBrowserPageCompatBound){"
                + "window.__flashBrowserPageCompatBound=true;"
                + "document.addEventListener('DOMContentLoaded',refresh,{passive:true});"
                + "window.addEventListener('load',refresh,{passive:true});"
                + "if(legacyMode){"
                + "window.addEventListener('resize',scheduleLegacyRefresh,{passive:true});"
                + "window.addEventListener('orientationchange',scheduleLegacyRefresh,{passive:true});"
                + "window.setTimeout(refresh,0);"
                + "window.setTimeout(refresh,200);"
                + "}"
                + "}"
                + "if(legacyMode){try{document.documentElement.style.visibility='hidden';if(document.body){document.body.style.visibility='hidden';document.body.style.opacity='0';}}catch(e){}}"
                + "refresh();"
                + "})();";
        }

        private static string DecodeBody(byte[] body, string contentType)
        {
            string charset = "utf-8";
            Match match = Regex.Match(contentType ?? string.Empty, "charset=([^;]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                charset = match.Groups[1].Value.Trim().Trim('"');
            }

            try
            {
                return Encoding.GetEncoding(charset).GetString(body);
            }
            catch
            {
                return Encoding.UTF8.GetString(body);
            }
        }

        private static bool IsHtmlContentType(string contentType)
        {
            string lower = (contentType ?? string.Empty).ToLowerInvariant();
            return lower.Contains("text/html") || lower.Contains("application/xhtml");
        }

        private static bool IsSwfUrl(Uri uri)
        {
            return uri != null && uri.AbsolutePath.EndsWith(".swf", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasFlashIndicators(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return false;
            }

            string html = Encoding.UTF8.GetString(body).ToLowerInvariant();
            return html.Contains(".swf")
                || html.Contains("application/x-shockwave-flash")
                || html.Contains("<object")
                || html.Contains("<embed")
                || html.Contains("data-pvzol-flash-url")
                || html.Contains("data-pvzol-flash-proxy-url");
        }

        private static bool ContainsFlashMarkup(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            string lower = html.ToLowerInvariant();
            return lower.Contains(".swf")
                || lower.Contains("shockwave")
                || lower.Contains("ruffle-embed")
                || lower.Contains("ruffle-object")
                || lower.Contains("ruffle-player");
        }

        private static bool ShouldInjectBootstrapForHtml(Uri targetUri, byte[] body)
        {
            return IsSwfUrl(targetUri) || HasFlashIndicators(body);
        }

        private bool TryBuildWrappedGameMainResponse(Uri targetUri, out RuffleResolvedResponse response)
        {
            response = null;
            if (!TryGetGameMainShellInfo(targetUri, out GameMainShellInfo shellInfo))
            {
                return false;
            }

            RuntimeDiagnostics.Write(
                "ruffle-shell",
                $"serve wrapped game page target={targetUri} swf={shellInfo.SwfUrl} baseUrl={shellInfo.BaseUrl} baseUrlInfo={shellInfo.BaseUrlInfo}");

            response = new RuffleResolvedResponse(
                200,
                "OK",
                "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(BuildWrappedGameMainHtml(targetUri, shellInfo)),
                BuildCorsHeaders());
            return true;
        }

        private static bool TryGetGameMainShellInfo(Uri targetUri, out GameMainShellInfo shellInfo)
        {
            shellInfo = null;
            if (targetUri == null)
            {
                return false;
            }

            Match match = GameMainPathRegex.Match(targetUri.AbsolutePath ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            string prefix = (match.Groups["prefix"].Value ?? string.Empty).TrimEnd('/');
            string authority = targetUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            shellInfo = new GameMainShellInfo
            {
                PathPrefix = prefix,
                BaseUrl = authority + prefix + "/pvz/index.php/",
                BaseUrlInfo = authority + prefix + "/youkia/",
                SwfUrl = authority + prefix + "/youkia/main.swf"
            };
            return true;
        }

        private static string SanitizeReason(string reasonPhrase)
        {
            return string.IsNullOrWhiteSpace(reasonPhrase) ? "OK" : reasonPhrase.Trim();
        }

        private static string GuessMimeType(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.EndsWith(".html") || lower.EndsWith(".htm"))
            {
                return "text/html; charset=utf-8";
            }

            if (lower.EndsWith(".js"))
            {
                return "application/javascript; charset=utf-8";
            }

            if (lower.EndsWith(".css"))
            {
                return "text/css; charset=utf-8";
            }

            if (lower.EndsWith(".json"))
            {
                return "application/json; charset=utf-8";
            }

            if (lower.EndsWith(".wasm"))
            {
                return "application/wasm";
            }

            if (lower.EndsWith(".swf"))
            {
                return "application/x-shockwave-flash";
            }

            return "application/octet-stream";
        }

        private static string SanitizeRelativePath(string rawPath)
        {
            string normalized = rawPath ?? string.Empty;
            while (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            int queryIndex = normalized.IndexOf('?');
            if (queryIndex >= 0)
            {
                normalized = normalized.Substring(0, queryIndex);
            }

            normalized = normalized.Replace('\\', '/');
            var builder = new StringBuilder(normalized.Length);
            foreach (char ch in normalized)
            {
                bool invalid = ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|';
                if (!invalid)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            string raw = (query ?? string.Empty).TrimStart('?');
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string segment in raw.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index = segment.IndexOf('=');
                if (index < 0)
                {
                    map[Uri.UnescapeDataString(segment)] = string.Empty;
                    continue;
                }

                string key = Uri.UnescapeDataString(segment.Substring(0, index));
                string value = Uri.UnescapeDataString(segment.Substring(index + 1));
                map[key] = value;
            }

            return map;
        }

        private static string JavaScriptEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static int FindAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void WriteResponse(HttpListenerResponse response, int statusCode, string contentType, byte[] body, bool skipBody = false)
        {
            if (response == null)
            {
                return;
            }

            byte[] safeBody = body ?? Array.Empty<byte>();

            try
            {
                response.StatusCode = statusCode;
                response.ContentType = contentType ?? "application/octet-stream";
                response.ContentLength64 = safeBody.LongLength;

                if (!skipBody && safeBody.Length > 0)
                {
                    response.OutputStream.Write(safeBody, 0, safeBody.Length);
                }
            }
            catch (InvalidOperationException ex)
            {
                RuntimeDiagnostics.Write("ruffle-proxy", $"write skipped status={statusCode} error={ex.Message}");
            }
            finally
            {
                try
                {
                    response.OutputStream.Close();
                }
                catch
                {
                }
            }
        }

        private void WriteAmfDump(string direction, string verbOrStatus, Uri targetUri, string contentType, byte[] body, string extraStatus)
        {
            try
            {
                Directory.CreateDirectory(_amfDumpRootPath);
                int sequence = Interlocked.Increment(ref _amfDumpSequence);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string safeDirection = string.IsNullOrWhiteSpace(direction) ? "amf" : direction.Trim().ToLowerInvariant();
                string baseName = $"{stamp}_{sequence:D4}_{safeDirection}";
                string binPath = Path.Combine(_amfDumpRootPath, baseName + ".bin");
                string txtPath = Path.Combine(_amfDumpRootPath, baseName + ".txt");

                File.WriteAllBytes(binPath, body ?? Array.Empty<byte>());

                string metadata =
                    "direction=" + (direction ?? string.Empty) + Environment.NewLine
                    + "verbOrStatus=" + (verbOrStatus ?? string.Empty) + Environment.NewLine
                    + "url=" + (targetUri == null ? string.Empty : targetUri.AbsoluteUri) + Environment.NewLine
                    + "contentType=" + (contentType ?? string.Empty) + Environment.NewLine
                    + "extraStatus=" + (extraStatus ?? string.Empty) + Environment.NewLine
                    + "bytes=" + ((body == null) ? 0 : body.Length).ToString() + Environment.NewLine
                    + "hexPreview=" + BuildHexPreview(body, 96) + Environment.NewLine;
                File.WriteAllText(txtPath, metadata, Encoding.UTF8);

                RuntimeDiagnostics.Write("ruffle-amf", $"dump saved direction={safeDirection} file={binPath}");
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-amf", $"dump failed direction={direction} target={targetUri} error={ex.Message}");
            }
        }

        private byte[] RewriteKnownBrokenAmfRequest(byte[] body, Uri requestUri, AmfPacketInfo packetInfo)
        {
            if (body == null || body.Length < 12)
            {
                return body;
            }

            try
            {
                AmfPacketInfo info = packetInfo ?? TryParseAmfPacketInfo(body);
                if (info == null || !EmptyArgumentAmfTargets.Contains(info.Target))
                {
                    return body;
                }

                if (info.BodyLength != EmptyMixedArrayArgumentBody.Length || !BytesEqual(body, info.BodyOffset, EmptyMixedArrayArgumentBody))
                {
                    return body;
                }

                var patched = new byte[body.Length - EmptyMixedArrayArgumentBody.Length + EmptyArgumentBody.Length];
                Buffer.BlockCopy(body, 0, patched, 0, info.BodyOffset - 4);
                WriteInt32BigEndian(patched, info.BodyOffset - 4, EmptyArgumentBody.Length);
                Buffer.BlockCopy(EmptyArgumentBody, 0, patched, info.BodyOffset, EmptyArgumentBody.Length);

                RuntimeDiagnostics.Write(
                    "ruffle-amf",
                    $"patched empty-arg request target={info.Target} url={requestUri} oldBodyLength={info.BodyLength} newBodyLength={EmptyArgumentBody.Length}");
                return patched;
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("ruffle-amf", $"patch skipped url={requestUri} error={ex.Message}");
                return body;
            }
        }

        private static AmfPacketInfo TryParseAmfPacketInfo(byte[] body)
        {
            if (body == null || body.Length < 12)
            {
                return null;
            }

            try
            {
                int offset = 0;
                ushort version = ReadUInt16BigEndian(body, ref offset);
                if (version != 0 && version != 3)
                {
                    return null;
                }

                ushort headerCount = ReadUInt16BigEndian(body, ref offset);
                for (int i = 0; i < headerCount; i += 1)
                {
                    ushort nameLength = ReadUInt16BigEndian(body, ref offset);
                    offset += nameLength;
                    offset += 1;
                    int headerLength = ReadInt32BigEndian(body, ref offset);
                    if (headerLength < 0 || offset + headerLength > body.Length)
                    {
                        return null;
                    }
                    offset += headerLength;
                }

                ushort messageCount = ReadUInt16BigEndian(body, ref offset);
                if (messageCount < 1)
                {
                    return null;
                }

                ushort targetLength = ReadUInt16BigEndian(body, ref offset);
                if (targetLength <= 0 || offset + targetLength > body.Length)
                {
                    return null;
                }

                string target = Encoding.UTF8.GetString(body, offset, targetLength);
                offset += targetLength;
                ushort responseLength = ReadUInt16BigEndian(body, ref offset);
                if (offset + responseLength > body.Length)
                {
                    return null;
                }

                offset += responseLength;
                int messageBodyLength = ReadInt32BigEndian(body, ref offset);
                if (messageBodyLength < 0 || offset + messageBodyLength > body.Length)
                {
                    return null;
                }

                return new AmfPacketInfo
                {
                    Target = target,
                    Version = version,
                    MessageCount = messageCount,
                    BodyOffset = offset,
                    BodyLength = messageBodyLength
                };
            }
            catch
            {
                return null;
            }
        }

        private static ushort ReadUInt16BigEndian(byte[] data, ref int offset)
        {
            ushort value = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            return value;
        }

        private static int ReadInt32BigEndian(byte[] data, ref int offset)
        {
            int value =
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];
            offset += 4;
            return value;
        }

        private static void WriteInt32BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 24) & 0xFF);
            data[offset + 1] = (byte)((value >> 16) & 0xFF);
            data[offset + 2] = (byte)((value >> 8) & 0xFF);
            data[offset + 3] = (byte)(value & 0xFF);
        }

        private static bool BytesEqual(byte[] source, int offset, byte[] expected)
        {
            if (source == null || expected == null || offset < 0 || offset + expected.Length > source.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i += 1)
            {
                if (source[offset + i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildHexPreview(byte[] body, int maxBytes)
        {
            if (body == null || body.Length == 0 || maxBytes <= 0)
            {
                return string.Empty;
            }

            int count = Math.Min(body.Length, maxBytes);
            var builder = new StringBuilder(count * 3);
            for (int i = 0; i < count; i += 1)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(body[i].ToString("X2"));
            }

            return builder.ToString();
        }

        public void Dispose()
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
            }

            _listener = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
