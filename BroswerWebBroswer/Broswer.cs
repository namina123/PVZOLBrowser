using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using BroswerWebBroswer.Properties;

namespace WebBrowserApp
{
    public partial class Browser : Form
    {
        [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool InternetGetCookieEx(
            string url,
            string cookieName,
            StringBuilder cookieData,
            ref int size,
            int flags,
            IntPtr reserved);

        private const int InternetCookieHttpOnly = 0x00002000;
        private const string DefaultHome = "http://pvz.youkia.com";

        private readonly CookieManager _cookieManager;
        private readonly CookieProfileManager _cookieProfileManager;
        private readonly ProxyManager _proxyManager;
        private readonly ZoneOrderManager _zoneOrderManager;
        private readonly LocalMappingRuleSet _localMappingRules;
        private readonly List<string> _cookieFiles = new List<string>();
        private readonly List<CookieDisplayEntry> _cookieDisplayEntries = new List<CookieDisplayEntry>();
        private readonly Timer _zoneJumpSavePollTimer;
        private readonly Timer _cookieImportToastTimer;

        private FileSystemWatcher watcher;
        private ProxySettings _settings = new ProxySettings();
        private SystemProxySnapshot _originalProxySnapshot;
        private CookieSelectionForm _cookieSelectionForm;
        private ProxySettingsUserControl _proxySettingsPanel;
        private IntPtr _nativeProxy = IntPtr.Zero;
        private int _currentPort = 9000;
        private BrowserBackendMode _browserMode = BrowserBackendMode.NativeIe;
        private BrowserBackendDecision _backendDecision;
        private FlashRuntimeInfo _flashRuntimeInfo;
        private IRuffleBrowserHost _ruffleHost;
        private RuffleLocalProxy _ruffleProxy;
        private string _pendingNavigationUrl;
        private bool _ruffleInitializing;
        private readonly bool _legacyDirectMode;
        private bool _shutdownCleanupStarted;
        private bool _allowImmediateClose;
        private Color _cookieToolDefaultBackColor;
        private ServerJumpPanel _serverJumpPanel;
        private Form _serverJumpForm;
        private bool _zoneJumpAvailable;
        private bool _zoneJumpPanelManuallyHidden;
        private string _zoneJumpAvailabilityKey = string.Empty;
        private string _zoneJumpOrderSignature = string.Empty;
        private int _zoneJumpRefreshVersion;
        private bool _zoneJumpRefreshInFlight;
        private readonly Timer _zoneJumpMonitorTimer;
        private string _lastPopupTargetUrl = string.Empty;
        private Panel _cookieImportToastPanel;
        private Label _cookieImportToastLabel;
        private Panel _zoneJumpSavePromptPanel;
        private Label _zoneJumpSavePromptLabel;
        private CookieProfileManager.SaveCookieMatch _pendingZoneJumpSaveMatch;
        private string _pendingZoneJumpTargetUrl = string.Empty;
        private bool _pendingZoneJumpRetryAvailable;
        private bool _pendingZoneJumpRetryConsumed;
        private bool _zoneJumpSavePollInFlight;
        private Point _lastWindowLocation;
        private bool _hasLastWindowLocation;

        public Browser()
        {
            InitializeComponent();

            _cookieManager = new CookieManager();
            _cookieProfileManager = new CookieProfileManager(AppDomain.CurrentDomain.BaseDirectory);
            _proxyManager = new ProxyManager();
            _zoneOrderManager = new ZoneOrderManager(AppDomain.CurrentDomain.BaseDirectory);
            _localMappingRules = LocalMappingRuleSet.CreateDefault(Path.Combine(Application.StartupPath, "cache"));
            _originalProxySnapshot = _proxyManager.CaptureCurrentProxy();
            _legacyDirectMode = BrowserBackendSelector.IsLegacyWindowsOnly();
            _cookieToolDefaultBackColor = btnCookieTool.BackColor;
            _zoneJumpMonitorTimer = new Timer { Interval = 1000 };
            _zoneJumpSavePollTimer = new Timer { Interval = 500 };
            _cookieImportToastTimer = new Timer { Interval = 3500 };
            webBrowser.ObjectForScripting = new BrowserScriptBridge(this);

            InitializeCookieImportDragDrop();
            InitializeZoneJumpUi();
            InitializeTopBarLayout();
            InitializeZoneJumpMonitor();
            InitializeJumpSavePromptUi();
            InitializeCookieImportToastUi();
            webBrowser.NewWindow += WebBrowser_NewWindow;
            webBrowser.Navigating += WebBrowser_Navigating;
            webBrowser.DocumentCompleted += WebBrowser_DocumentCompleted;
            _zoneJumpSavePollTimer.Tick += ZoneJumpSavePollTimer_Tick;
            _cookieImportToastTimer.Tick += CookieImportToastTimer_Tick;

            InitializeCookieLibrary();
            if (!_legacyDirectMode)
            {
                InitializeProxySystem();
            }
            InitializeBrowserSettings();

            Shown += Browser_Shown;
            FormClosing += Browser_FormClosing;
            Resize += Browser_Resize;
            Move += Browser_Move;
        }

        private void Browser_Shown(object sender, EventArgs e)
        {
            _lastWindowLocation = Location;
            _hasLastWindowLocation = true;
            LayoutTopBarControls();
            PositionCookieImportToast();
            if (_cookieSelectionForm == null || _cookieSelectionForm.IsDisposed)
            {
                BtnCookieTool_Click(btnCookieTool, EventArgs.Empty);
            }
        }

        private void Browser_Resize(object sender, EventArgs e)
        {
            PositionCookieImportToast();
            PositionZoneJumpSavePrompt();
            UpdateCookiePopupPlacement(true);
            UpdateZoneJumpPopupPlacement();
        }

        private void Browser_Move(object sender, EventArgs e)
        {
            if (!_hasLastWindowLocation)
            {
                _lastWindowLocation = Location;
                _hasLastWindowLocation = true;
                return;
            }

            int deltaX = Left - _lastWindowLocation.X;
            int deltaY = Top - _lastWindowLocation.Y;
            _lastWindowLocation = Location;

            if (deltaX == 0 && deltaY == 0)
            {
                return;
            }

            MoveFloatingPopupWithWindow(_cookieSelectionForm, deltaX, deltaY);
            MoveFloatingPopupWithWindow(_serverJumpForm, deltaX, deltaY);
        }

        private void InitializeBrowserSettings()
        {
            SetBrowserFeatureControl();
            webBrowser.ScriptErrorsSuppressed = true;
            webBrowser.AllowWebBrowserDrop = false;
            _flashRuntimeInfo = FlashRuntimeDetector.Detect();
            _backendDecision = BrowserBackendSelector.Decide(_flashRuntimeInfo);
            RuntimeDiagnostics.Write(
                "backend",
                $"policy={_backendDecision?.Policy} flashAvailable={_flashRuntimeInfo?.IsAvailable} flashVersion={_flashRuntimeInfo?.Version} webView2Available={_backendDecision?.WebView2Available} selected={_backendDecision?.Mode} reason={_backendDecision?.Reason}");
            InitializeBrowserBackend();
            ClearBrowserCookies();
            RuntimeDiagnostics.Write("cookie", $"startup cookie clear mode={_browserMode}");
            _cookieManager.UpdateCurrentDomain(DefaultHome);
            txtUrl.Text = DefaultHome;
            NavigateToAddress(DefaultHome);
            UpdateStatus(BuildStartupStatus());
        }

        private string BuildStartupStatus()
        {
            string flashVersion = _flashRuntimeInfo?.Version ?? "未知";
            string processArch = Environment.Is64BitProcess ? "x64" : "x86";
            string backend = _browserMode == BrowserBackendMode.RuffleWebView2 ? "Ruffle/WebView2" : "IE/Flash";
            string reason = _backendDecision?.Reason ?? "未提供";
            string proxyMode = _legacyDirectMode ? "直连兼容模式" : "本地映射代理";
            return $"浏览器已就绪 | 进程 {processArch} | 后端 {backend} | Flash {flashVersion} | 网络 {proxyMode} | {reason}";
        }

        private void InitializeBrowserBackend()
        {
            _browserMode = _backendDecision?.Mode ?? BrowserBackendMode.NativeIe;
            if (_browserMode != BrowserBackendMode.RuffleWebView2)
            {
                webBrowser.Visible = true;
                return;
            }

            if (_ruffleProxy == null)
            {
                _ruffleProxy = new RuffleLocalProxy(
                    Path.Combine(Application.StartupPath, "assets", "ruffle"),
                    ResolveUpstreamProxy());
                _ruffleProxy.ConfigureLocalMapping(_localMappingRules);
                RuntimeDiagnostics.Write("ruffle", "webview request handler ready");
            }

            if (_ruffleHost == null)
            {
                _ruffleHost = CreateRuffleHost();
                _ruffleHost.SourceChanged += RuffleHost_SourceChanged;
                _ruffleHost.NavigationCompleted += RuffleHost_NavigationCompleted;
                _ruffleHost.NewWindowRequested += RuffleHost_NewWindowRequested;
            }

            webBrowser.Visible = false;
            _ruffleHost.ViewControl.BringToFront();
            _ = EnsureRuffleViewInitializedAsync();
        }

        private async Task EnsureRuffleViewInitializedAsync()
        {
            if (_ruffleHost == null || _ruffleHost.IsInitialized || _ruffleInitializing)
            {
                return;
            }

            _ruffleInitializing = true;
            try
            {
                await _ruffleHost.InitializeAsync().ConfigureAwait(true);
                _ruffleHost.ViewControl.Visible = true;
                RuntimeDiagnostics.Write("ruffle", "webview2 host initialized");

                if (!string.IsNullOrWhiteSpace(_pendingNavigationUrl))
                {
                    string pendingUrl = _pendingNavigationUrl;
                    _pendingNavigationUrl = null;
                    NavigateToAddress(pendingUrl);
                }
            }
            catch (Exception ex)
            {
                _browserMode = BrowserBackendMode.NativeIe;
                webBrowser.Visible = true;
                if (_ruffleHost?.ViewControl != null)
                {
                    _ruffleHost.ViewControl.Visible = false;
                }
                RuntimeDiagnostics.Write("ruffle", $"initialization failed fallback=IE error={ex}");
                UpdateStatus($"Ruffle 初始化失败，已回退到 IE：{ex.Message}");
            }
            finally
            {
                _ruffleInitializing = false;
            }
        }

        private IRuffleBrowserHost CreateRuffleHost()
        {
            if (_ruffleProxy == null)
            {
                throw new InvalidOperationException("Ruffle 本地代理尚未初始化。");
            }

            IRuffleBrowserHost host = new RuffleWebViewHost(pnlBrowserHost, _ruffleProxy);
            return host;
        }

        private void SetBrowserFeatureControl()
        {
            try
            {
                string appName = Path.GetFileName(Application.ExecutablePath);

                using (var emulationKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    emulationKey?.SetValue(appName, 11001, Microsoft.Win32.RegistryValueKind.DWord);
                }

                using (var dpiKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL"))
                {
                    dpiKey?.SetValue(appName, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }

                using (var visualKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_ENABLE_WEB_CONTROL_VISUALS"))
                {
                    visualKey?.SetValue(appName, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }

        private void InitializeCookieLibrary()
        {
            _cookieProfileManager.EnsureInitialized();
            string cookieDirectory = _cookieProfileManager.CookieDirectory;

            watcher = new FileSystemWatcher(cookieDirectory)
            {
                Filter = "*.xml",
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };
            watcher.Created += (s, e) => LoadCookieFiles();
            watcher.Deleted += (s, e) => LoadCookieFiles();
            watcher.Changed += (s, e) => LoadCookieFiles();
            watcher.Renamed += (s, e) => LoadCookieFiles();

            LoadCookieFiles();
        }

        private void LoadCookieFiles()
        {
            this.InvokeIfRequired(() =>
            {
                _cookieFiles.Clear();
                _cookieFiles.AddRange(_cookieProfileManager.LoadProfileFiles());
                _cookieDisplayEntries.Clear();
                foreach (CookieDisplayEntry entry in BuildCookieDisplayEntries(_cookieFiles))
                {
                    _cookieDisplayEntries.Add(entry);
                }

                if (_cookieSelectionForm != null && !_cookieSelectionForm.IsDisposed)
                {
                    _cookieSelectionForm.SetCookieFiles(_cookieDisplayEntries);
                }
            });
        }

        private IEnumerable<CookieDisplayEntry> BuildCookieDisplayEntries(IEnumerable<string> files)
        {
            var groups = new List<Tuple<string, CookieProfileManager.CookieProfile, string>>();
            foreach (string file in files ?? Enumerable.Empty<string>())
            {
                CookieProfileManager.CookieProfile profile = _cookieProfileManager.LoadProfile(file);
                string signature = profile == null
                    ? "file:" + file
                    : CookieProfileManager.BuildProfileSignature(profile);
                groups.Add(Tuple.Create(file, profile, signature));
            }

            foreach (IGrouping<string, Tuple<string, CookieProfileManager.CookieProfile, string>> group in groups.GroupBy(item => item.Item3))
            {
                Tuple<string, CookieProfileManager.CookieProfile, string> first = group.First();
                List<string> filePaths = group.Select(item => item.Item1).ToList();
                string displayName = first.Item2?.UserName;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = "未知用户";
                }

                yield return new CookieDisplayEntry(first.Item1, filePaths, displayName);
            }
        }

        internal bool TryGetDroppedCookieXmlFiles(IDataObject dataObject, out string[] filePaths)
        {
            filePaths = Array.Empty<string>();
            if (dataObject == null || !dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                return false;
            }

            var droppedFiles = dataObject.GetData(DataFormats.FileDrop) as string[];
            if (droppedFiles == null || droppedFiles.Length == 0)
            {
                return false;
            }

            filePaths = droppedFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Where(File.Exists)
                .Where(path =>
                {
                    string extension = Path.GetExtension(path);
                    return string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return filePaths.Length > 0;
        }

        internal void ImportCookieProfileFiles(IEnumerable<string> filePaths)
        {
            List<string> files = (filePaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                return;
            }

            int importedCount = 0;
            int groupedCount = 0;
            var errors = new List<string>();
            foreach (string filePath in files)
            {
                string extension = Path.GetExtension(filePath) ?? string.Empty;
                if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    int importedFromZip = ImportCookieProfilesFromZip(filePath, errors);
                    importedCount += importedFromZip;
                    groupedCount += importedFromZip;
                    continue;
                }

                CookieProfileManager.ImportResult result = _cookieProfileManager.ImportProfileFile(filePath);
                if (result.Success)
                {
                    importedCount += 1;
                    continue;
                }

                errors.Add(Path.GetFileName(filePath) + "： " + result.ErrorMessage);
            }

            LoadCookieFiles();

            if (importedCount > 0)
            {
                UpdateStatus($"已导入 {importedCount} 个 Cookie XML");
                ShowCookieImportToast(groupedCount > 0
                    ? $"已从压缩包识别并保存 {groupedCount} 个 Cookie"
                    : $"已导入 {importedCount} 个 Cookie");
            }

            if (errors.Count == 0)
            {
                return;
            }

            string message = importedCount > 0
                ? "部分 Cookie XML 导入失败：\r\n\r\n" + string.Join("\r\n", errors)
                : "未导入任何有效的 Cookie XML。\r\n\r\n" + string.Join("\r\n", errors);
            MessageBox.Show(message, "Cookie 导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private int ImportCookieProfilesFromZip(string zipPath, List<string> errors)
        {
            try
            {
                var zipFile = new FileInfo(zipPath);
                if (!zipFile.Exists)
                {
                    errors.Add(Path.GetFileName(zipPath) + "：文件不存在");
                    return 0;
                }

                if (zipFile.Length > 50L * 1024L * 1024L)
                {
                    errors.Add(Path.GetFileName(zipPath) + "：压缩包超过 50MB");
                    return 0;
                }

                int imported = 0;
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                        {
                            continue;
                        }

                        if (!string.Equals(Path.GetExtension(entry.Name), ".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using (Stream stream = entry.Open())
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            string rawText = reader.ReadToEnd();
                            CookieProfileManager.ImportResult result =
                                _cookieProfileManager.ImportProfileText(rawText, Path.GetFileNameWithoutExtension(entry.Name), preserveRawXml: true);
                            if (result.Success)
                            {
                                imported += 1;
                            }
                            else
                            {
                                errors.Add(Path.GetFileName(zipPath) + " -> " + entry.Name + "： " + result.ErrorMessage);
                            }
                        }
                    }
                }

                return imported;
            }
            catch (InvalidDataException)
            {
                errors.Add(Path.GetFileName(zipPath) + "：压缩包损坏或包含不支持的内容");
                return 0;
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(zipPath) + "： " + ex.Message);
                return 0;
            }
        }

        public void ApplyCookieProfileFile(string filePath)
        {
            CookieProfileManager.CookieProfile profile = _cookieProfileManager.LoadProfile(filePath);
            if (profile == null)
            {
                MessageBox.Show("解析 Cookie 文件失败或文件内容无效。");
                return;
            }

            ApplyCookieProfile(profile);
        }

        private void ApplyCookieProfile(CookieProfileManager.CookieProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            string targetUrl = CookieProfileManager.BuildTargetUrl(profile);
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                MessageBox.Show("Cookie 文件未能生成可访问的目标地址。");
                return;
            }

            if (!Uri.TryCreate(profile.UserDomain, UriKind.Absolute, out Uri domainUri)
                || !Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri targetUri))
            {
                MessageBox.Show("Cookie 文件中的域名格式无效。");
                return;
            }

            CookieProfileManager.CookieApplicationPlan applicationPlan =
                CookieProfileManager.BuildCookieApplicationPlan(profile.UserCookies);
            List<string> cookieEntries = applicationPlan == null
                ? new List<string>()
                : new List<string>(applicationPlan.CookieEntries);
            if (cookieEntries.Count == 0)
            {
                MessageBox.Show("Cookie 文件里没有可应用的关键 Cookie。");
                return;
            }

            ClearBrowserCookies();

            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                string combinedCookieHeader = applicationPlan.CookieHeader;
                foreach (string cookieEntry in cookieEntries)
                {
                    ApplyRuffleCookies(domainUri, cookieEntry);
                    ApplyRuffleCookies(targetUri, cookieEntry);
                }

                _ruffleProxy?.SetCookieHeader(domainUri, combinedCookieHeader);
                _ruffleProxy?.SetCookieHeader(targetUri, combinedCookieHeader);
                RuntimeDiagnostics.Write("cookie", $"apply via ruffle domain={profile.UserDomain} target={targetUrl} count={cookieEntries.Count} rule={applicationPlan.Rule}");
            }
            else
            {
                _cookieManager.ApplyCookieEntries(domainUri, targetUri, cookieEntries);
                RuntimeDiagnostics.Write("cookie", $"apply via IE domain={profile.UserDomain} target={targetUrl} count={cookieEntries.Count} rule={applicationPlan.Rule}");
            }

            NavigateToAddress(targetUrl);
            txtUrl.Text = targetUrl;
            UpdateStatus($"已应用 Cookie 并跳转到 {targetUrl}");
        }

        public void SetCurrentCookie(string uri, string cookies)
        {
            _cookieManager.UpdateCookies(uri, cookies);
        }

        private void NavigateToUrl(object sender = null, EventArgs e = null)
        {
            try
            {
                string url = txtUrl.Text.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "http://" + url;
                }

                NavigateToAddress(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导航错误: {ex.Message}");
            }
        }

        private void NavigateToAddress(string url)
        {
            Uri target = NormalizeUrl(url);
            txtUrl.Text = target.AbsoluteUri;

            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleProxy == null)
                {
                    throw new InvalidOperationException("Ruffle 本地代理尚未启动。");
                }

                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    _pendingNavigationUrl = target.AbsoluteUri;
                    _ = EnsureRuffleViewInitializedAsync();
                    return;
                }

                _ruffleHost.Navigate(target.AbsoluteUri);
                return;
            }

            webBrowser.Navigate(target);
        }

        internal void NavigateInPlace(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            this.InvokeIfRequired(() => NavigateToAddress(url));
        }

        internal void RememberPopupTarget(string url)
        {
            string normalized = ResolveIeRelativeUrl(url);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _lastPopupTargetUrl = normalized;
            }
        }

        private static Uri NormalizeUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return new Uri(DefaultHome);
            }

            string normalized = rawUrl.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "http://" + normalized;
            }

            return new Uri(normalized);
        }

        private void WebBrowser_Navigated(object sender, WebBrowserNavigatedEventArgs e)
        {
            if (_browserMode != BrowserBackendMode.NativeIe)
            {
                return;
            }

            txtUrl.Text = e.Url.ToString();
            _cookieManager.UpdateCurrentDomain(e.Url.ToString());
            UpdateStatus($"正在浏览 {e.Url.Host}");
            RefreshZoneJumpAvailabilityAsync();
        }

        private void WebBrowser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            if (_browserMode != BrowserBackendMode.NativeIe || e?.Url == null)
            {
                return;
            }

            string targetFrame = e.TargetFrameName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetFrame)
                || string.Equals(targetFrame, "_self", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetFrame, "_top", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!IsHttpUrl(e.Url.AbsoluteUri))
            {
                return;
            }

            e.Cancel = true;
            RememberPopupTarget(e.Url.AbsoluteUri);
            RuntimeDiagnostics.Write("ie-nav", $"intercepted target-frame navigation frame={targetFrame} url={e.Url.AbsoluteUri}");
            BeginInvoke((Action)(() => NavigateToAddress(e.Url.AbsoluteUri)));
        }

        private void WebBrowser_NewWindow(object sender, CancelEventArgs e)
        {
            if (_browserMode != BrowserBackendMode.NativeIe)
            {
                return;
            }

            e.Cancel = true;
            string targetUrl = ResolveCurrentIePopupTarget();
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                RuntimeDiagnostics.Write("ie-nav", "intercepted new window but target url was empty");
                return;
            }

            RuntimeDiagnostics.Write("ie-nav", $"intercepted new window url={targetUrl}");
            BeginInvoke((Action)(() => NavigateToAddress(targetUrl)));
        }

        private void WebBrowser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (_browserMode != BrowserBackendMode.NativeIe)
            {
                return;
            }

            if (webBrowser.Url != null && e?.Url != null
                && !string.Equals(webBrowser.Url.AbsoluteUri, e.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ForceIeLinksToReuseCurrentWindow();
            InjectIeNavigationOverrideScript();
            RefreshZoneJumpAvailabilityAsync();
            _ = RefreshZoneJumpAvailabilityDelayedAsync(1200);
            _ = VerifyPendingZoneJumpAfterLoadAsync(600);
        }

        private string ResolveCurrentIePopupTarget()
        {
            if (!string.IsNullOrWhiteSpace(_lastPopupTargetUrl))
            {
                string rememberedUrl = _lastPopupTargetUrl;
                _lastPopupTargetUrl = string.Empty;
                return rememberedUrl;
            }

            try
            {
                HtmlElement activeElement = webBrowser.Document?.ActiveElement;
                if (activeElement != null)
                {
                    string href = activeElement.GetAttribute("href");
                    string action = activeElement.GetAttribute("action");
                    string candidate = !string.IsNullOrWhiteSpace(href) ? href : action;
                    string resolved = ResolveIeRelativeUrl(candidate);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }
            catch
            {
            }

            try
            {
                string statusText = webBrowser.StatusText;
                string resolved = ResolveIeRelativeUrl(statusText);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private string ResolveIeRelativeUrl(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            candidate = candidate.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri absoluteUri))
            {
                return absoluteUri.AbsoluteUri;
            }

            if (webBrowser.Url != null && Uri.TryCreate(webBrowser.Url, candidate, out Uri relativeUri))
            {
                return relativeUri.AbsoluteUri;
            }

            return string.Empty;
        }

        private void ForceIeLinksToReuseCurrentWindow()
        {
            HtmlDocument document = webBrowser.Document;
            if (document == null)
            {
                return;
            }

            foreach (HtmlElement link in document.GetElementsByTagName("a"))
            {
                if (!string.IsNullOrWhiteSpace(link.GetAttribute("target")))
                {
                    link.SetAttribute("target", "_self");
                }
            }

            foreach (HtmlElement form in document.GetElementsByTagName("form"))
            {
                if (!string.IsNullOrWhiteSpace(form.GetAttribute("target")))
                {
                    form.SetAttribute("target", "_self");
                }
            }
        }

        private void InjectIeNavigationOverrideScript()
        {
            HtmlDocument document = webBrowser.Document;
            if (document?.Body == null)
            {
                return;
            }

            const string script =
                "(function(){"
                + "if(window.__pvzolNavHookInstalled){return;}"
                + "window.__pvzolNavHookInstalled=true;"
                + "function remember(url){try{if(url&&window.external&&typeof window.external.RememberPopupTarget!=='undefined'){window.external.RememberPopupTarget(String(url));}}catch(e){}}"
                + "function nav(url){remember(url);try{if(url&&window.external&&typeof window.external.NavigateInPlace!=='undefined'){window.external.NavigateInPlace(String(url));return true;}}catch(e){}try{if(url){window.location.href=String(url);return true;}}catch(ex){}return false;}"
                + "window.open=function(url){remember(url);nav(url);return window;};"
                + "if(window.showModalDialog){window.showModalDialog=function(url){remember(url);nav(url);return null;};}"
                + "if(window.showModelessDialog){window.showModelessDialog=function(url){remember(url);nav(url);return null;};}"
                + "function patchTargets(){"
                + "var anchors=document.getElementsByTagName('a');"
                + "for(var i=0;i<anchors.length;i++){try{anchors[i].target='_self';anchors[i].onclick=function(evt){evt=evt||window.event;remember(this.href);nav(this.href);if(evt){evt.returnValue=false;if(evt.preventDefault){evt.preventDefault();}}return false;};anchors[i].onmousedown=function(){remember(this.href);};}catch(e){}}"
                + "var forms=document.getElementsByTagName('form');"
                + "for(var j=0;j<forms.length;j++){try{if(forms[j].target){forms[j].target='_self';}}catch(e){}}"
                + "}"
                + "patchTargets();"
                + "if(window.setInterval){window.setInterval(patchTargets,800);}"
                + "if(document.attachEvent){document.attachEvent('onclick',function(evt){evt=evt||window.event;var el=evt?evt.srcElement:null;while(el&&el.tagName&&el.tagName.toLowerCase()!='a'){el=el.parentNode;}if(el&&el.href){remember(el.href);nav(el.href);evt.returnValue=false;return false;}return true;});}"
                + "})();";

            try
            {
                document.InvokeScript("execScript", new object[] { script, "JavaScript" });
            }
            catch
            {
            }
        }

        private async Task RefreshZoneJumpAvailabilityDelayedAsync(int delayMilliseconds)
        {
            await Task.Delay(Math.Max(1, delayMilliseconds)).ConfigureAwait(true);
            if (!IsDisposed)
            {
                RefreshZoneJumpAvailabilityAsync();
            }
        }

        private void InitializeTopBarLayout()
        {
            pnlTopBar.Resize += (s, e) => LayoutTopBarControls();
        }

        private void LayoutTopBarControls()
        {
            if (pnlTopBar == null)
            {
                return;
            }

            int margin = 12;
            int gap = 8;
            int top = 10;
            int buttonHeight = 34;
            int x = margin;

            btnZoneJump.SetBounds(x, top, 46, buttonHeight);
            x += btnZoneJump.Width + gap;

            btnRefresh.SetBounds(x, top, 72, buttonHeight);
            x += btnRefresh.Width + gap;

            btnHome.SetBounds(x, top, 72, buttonHeight);
            x += btnHome.Width + gap;

            int right = pnlTopBar.ClientSize.Width - margin;
            btnSaveCookie.SetBounds(right - 170, top, 170, buttonHeight);
            right = btnSaveCookie.Left - gap;

            btnGo.SetBounds(right - 58, top, 58, buttonHeight);
            right = btnGo.Left - gap;

            int urlWidth = Math.Max(180, right - x);
            txtUrl.SetBounds(x, top + 4, urlWidth, 26);
        }

        private void InitializeZoneJumpMonitor()
        {
            _zoneJumpMonitorTimer.Tick += ZoneJumpMonitorTimer_Tick;
            _zoneJumpMonitorTimer.Start();
        }

        private void InitializeJumpSavePromptUi()
        {
            _zoneJumpSavePromptPanel = new Panel
            {
                BackColor = Color.FromArgb(255, 251, 235),
                Height = 46,
                Padding = new Padding(12, 8, 12, 8),
                Visible = false
            };
            _zoneJumpSavePromptPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            _zoneJumpSavePromptLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(120, 53, 15),
                TextAlign = ContentAlignment.MiddleLeft
            };

            Button btnSave = CreatePromptActionButton("确认保存");
            btnSave.Click += (s, e) => HandleZoneJumpSavePromptAction(saveAndApply: false, dismissOnly: false);
            Button btnSaveApply = CreatePromptActionButton("保存并跳转");
            btnSaveApply.Click += (s, e) => HandleZoneJumpSavePromptAction(saveAndApply: true, dismissOnly: false);
            Button btnSkip = CreatePromptActionButton("不保存");
            btnSkip.Click += (s, e) => HandleZoneJumpSavePromptAction(saveAndApply: false, dismissOnly: true);

            var buttonPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                WrapContents = false
            };
            buttonPanel.Controls.Add(btnSave);
            buttonPanel.Controls.Add(btnSaveApply);
            buttonPanel.Controls.Add(btnSkip);

            _zoneJumpSavePromptPanel.Controls.Add(_zoneJumpSavePromptLabel);
            _zoneJumpSavePromptPanel.Controls.Add(buttonPanel);
            pnlBrowserHost.Controls.Add(_zoneJumpSavePromptPanel);
            PositionZoneJumpSavePrompt();
            _zoneJumpSavePromptPanel.BringToFront();
        }

        private void PositionZoneJumpSavePrompt()
        {
            if (_zoneJumpSavePromptPanel == null || pnlBrowserHost == null)
            {
                return;
            }

            int width = Math.Max(0, pnlBrowserHost.ClientSize.Width);
            int y = Math.Max(0, pnlBrowserHost.ClientSize.Height - _zoneJumpSavePromptPanel.Height);
            _zoneJumpSavePromptPanel.Bounds = new Rectangle(0, y, width, _zoneJumpSavePromptPanel.Height);
        }

        private void InitializeCookieImportToastUi()
        {
            _cookieImportToastPanel = new Panel
            {
                BackColor = Color.FromArgb(30, 41, 59),
                Size = new Size(320, 42),
                Visible = false
            };

            _cookieImportToastLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _cookieImportToastPanel.Controls.Add(_cookieImportToastLabel);
            Controls.Add(_cookieImportToastPanel);
            PositionCookieImportToast();
            _cookieImportToastPanel.BringToFront();
        }

        private static Button CreatePromptActionButton(string text)
        {
            return new Button
            {
                AutoSize = true,
                BackColor = Color.White,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 8.8F),
                ForeColor = Color.FromArgb(55, 65, 81),
                Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(10, 3, 10, 3),
                Text = text,
                UseVisualStyleBackColor = false
            };
        }

        private void ZoneJumpMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_zoneJumpRefreshInFlight)
            {
                return;
            }

            Uri currentUri = GetCurrentPageUri();
            if (currentUri == null || !ShouldPollZoneJump(currentUri))
            {
                return;
            }

            RefreshZoneJumpAvailabilityAsync();
        }

        private static bool ShouldPollZoneJump(Uri currentUri)
        {
            if (currentUri == null)
            {
                return false;
            }

            string url = currentUri.AbsoluteUri ?? string.Empty;
            return url.IndexOf("pvz", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("youkia", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RuffleHost_SourceChanged(object sender, RuffleSourceChangedEventArgs e)
        {
            if (e?.Source == null)
            {
                return;
            }

            Uri displayUri = _ruffleProxy?.GetDisplayUri(e.Source) ?? e.Source;
            txtUrl.Text = displayUri.AbsoluteUri;
            _cookieManager.UpdateCurrentDomain(displayUri.AbsoluteUri);
        }

        private void RuffleHost_NavigationCompleted(object sender, RuffleNavigationCompletedEventArgs e)
        {
            if (e?.Source == null)
            {
                return;
            }

            Uri displayUri = _ruffleProxy?.GetDisplayUri(e.Source) ?? e.Source;
            RuntimeDiagnostics.Write(
                "ruffle-nav",
                $"success={e.IsSuccess} status={e.WebErrorStatus} source={e.Source} display={displayUri}");
            UpdateStatus(e.IsSuccess ? $"正在浏览 {displayUri.Host}" : $"Ruffle 页面加载失败: {e.WebErrorStatus}");
            RefreshZoneJumpAvailabilityAsync();
            _ = RefreshZoneJumpAvailabilityDelayedAsync(1200);
            _ = VerifyPendingZoneJumpAfterLoadAsync(600);
        }

        private void RuffleHost_NewWindowRequested(object sender, RuffleNewWindowRequestedEventArgs e)
        {
            if (e?.TargetUri == null)
            {
                return;
            }

            BeginInvoke((Action)(() => NavigateToAddress(e.TargetUri.AbsoluteUri)));
        }

        private async Task VerifyPendingZoneJumpAfterLoadAsync(int delayMilliseconds)
        {
            string pendingTarget = _pendingZoneJumpTargetUrl;
            if (string.IsNullOrWhiteSpace(pendingTarget))
            {
                return;
            }

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(true);
            }

            if (IsDisposed || string.IsNullOrWhiteSpace(_pendingZoneJumpTargetUrl))
            {
                return;
            }

            bool hasSwf = await CurrentPageHasSwfAsync().ConfigureAwait(true);
            if (hasSwf)
            {
                _pendingZoneJumpRetryAvailable = false;
                _pendingZoneJumpTargetUrl = string.Empty;
                return;
            }

            if (_pendingZoneJumpRetryAvailable && !_pendingZoneJumpRetryConsumed)
            {
                _pendingZoneJumpRetryConsumed = true;
                UpdateStatus("当前页面未检测到 SWF，已自动重试一次跳转");
                NavigateToAddress(pendingTarget);
                return;
            }

            _pendingZoneJumpTargetUrl = string.Empty;
        }

        private async Task<bool> CurrentPageHasSwfAsync()
        {
            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    return false;
                }

                string script = @"
(function(){
    function hasSwfUrl(value){
        return typeof value==='string' && /\.swf(\?.*)?$/i.test(value);
    }
    if(document.querySelector('ruffle-player,.pvzol-ruffle-host')){ return true; }
    var nodes=document.querySelectorAll('embed,object,param[name=""movie""],param[name=""src""]');
    for(var i=0;i<nodes.length;i++){
        var node=nodes[i];
        if(hasSwfUrl(node.getAttribute&&node.getAttribute('src'))){ return true; }
        if(hasSwfUrl(node.getAttribute&&node.getAttribute('data'))){ return true; }
        if(hasSwfUrl(node.getAttribute&&node.getAttribute('value'))){ return true; }
        if(node.getAttribute&&node.getAttribute('data-pvzol-flash-url')){ return true; }
    }
    return false;
})();";
                string result = await _ruffleHost.ExecuteScriptAsync(script).ConfigureAwait(true);
                return string.Equals(DecodeWebView2String(result), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals((result ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                HtmlDocument doc = webBrowser.Document;
                if (doc == null)
                {
                    return false;
                }

                foreach (HtmlElement element in doc.GetElementsByTagName("embed"))
                {
                    string src = element.GetAttribute("src");
                    if (!string.IsNullOrWhiteSpace(src) && src.IndexOf(".swf", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                foreach (HtmlElement element in doc.GetElementsByTagName("object"))
                {
                    string data = element.GetAttribute("data");
                    if (!string.IsNullOrWhiteSpace(data) && data.IndexOf(".swf", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void ClearRuffleCookies()
        {
            _ruffleHost?.ClearCookies();
        }

        private void ApplyRuffleCookies(Uri targetUri, string cookieHeader)
        {
            if (targetUri == null || string.IsNullOrWhiteSpace(targetUri.Host) || string.IsNullOrWhiteSpace(cookieHeader))
            {
                return;
            }

            _ruffleHost?.ApplyCookies(targetUri, cookieHeader);
        }

        private void BtnGo_Click(object sender, EventArgs e)
        {
            NavigateToUrl();
        }

        private void BtnHome_Click(object sender, EventArgs e)
        {
            txtUrl.Text = DefaultHome;
            NavigateToUrl();
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    NavigateToAddress(txtUrl.Text);
                    return;
                }

                _ruffleHost.Reload();
                UpdateStatus("已刷新当前页面");
                return;
            }

            if (webBrowser.Url == null)
            {
                txtUrl.Text = DefaultHome;
                NavigateToUrl();
                return;
            }

            webBrowser.Refresh(WebBrowserRefreshOption.Completely);
            UpdateStatus("已刷新当前页面");
        }

        private void BtnZoneJump_Click(object sender, EventArgs e)
        {
            if (!_zoneJumpAvailable)
            {
                UpdateStatus("当前页面尚不满足区服跳转条件");
                return;
            }

            _zoneJumpPanelManuallyHidden = _serverJumpForm != null && !_serverJumpForm.IsDisposed && _serverJumpForm.Visible;
            ShowZoneJumpPanel(!_zoneJumpPanelManuallyHidden);
        }

        private async void BtnSaveCookie_Click(object sender, EventArgs e)
        {
            Uri currentUri = GetCurrentPageUri();
            if (currentUri == null)
            {
                UpdateStatus("当前没有可保存 Cookie 的页面");
                return;
            }

            CookieProfileManager.SaveCookieMatch match = await FindSavableCookieMatchAsync(currentUri).ConfigureAwait(true);
            if (match == null)
            {
                UpdateStatus("当前页面没有可保存的 Cookie");
                return;
            }

            FileInfo savedFile = _cookieProfileManager.SaveProfileFromPage(
                match.SourceUri,
                match.PersistedCookies,
                GetCurrentPageTitleHint(currentUri));
            if (savedFile == null)
            {
                RuntimeDiagnostics.Write("cookie-save", $"failed page={currentUri} source={match.SourceUri} rule={match.Rule}");
                UpdateStatus("保存 Cookie 失败");
                return;
            }

            LoadCookieFiles();
            RuntimeDiagnostics.Write(
                "cookie-save",
                $"saved page={currentUri} source={match.SourceUri} domain={match.UserDomain} rule={match.Rule} file={savedFile.FullName}");
            UpdateStatus($"已保存 Cookie：{savedFile.Name}");
        }

        private void InitializeZoneJumpUi()
        {
            EnsureZoneJumpPanelCreated();
            btnZoneJump.Enabled = false;
        }

        private void EnsureZoneJumpPanelCreated()
        {
            if (_serverJumpPanel != null && !_serverJumpPanel.IsDisposed)
            {
                return;
            }

            _serverJumpPanel = new ServerJumpPanel(HandleZoneJumpRequest, HandleZoneFavoriteToggle, HandleZoneJumpPanelClosed)
            {
                Width = 292
            };
            _serverJumpPanel.SetOrderFilePath(_zoneOrderManager.FilePath);
        }

        private void EnsureZoneJumpPopupCreated()
        {
            EnsureZoneJumpPanelCreated();
            if (_serverJumpForm != null && !_serverJumpForm.IsDisposed)
            {
                return;
            }

            _serverJumpForm = new Form
            {
                AutoScaleMode = AutoScaleMode.Font,
                BackColor = Color.White,
                ClientSize = new Size(292, 420),
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                MaximizeBox = false,
                MinimizeBox = false,
                MinimumSize = new Size(292, 340),
                ShowIcon = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Text = "区服跳转"
            };
            _serverJumpPanel.Dock = DockStyle.Fill;
            _serverJumpForm.Controls.Add(_serverJumpPanel);
            _serverJumpForm.FormClosed += ServerJumpForm_FormClosed;
        }

        private void HandleZoneJumpPanelClosed()
        {
            _zoneJumpPanelManuallyHidden = true;
            ShowZoneJumpPanel(false);
        }

        private void ShowZoneJumpPanel(bool visible)
        {
            if (_serverJumpPanel == null)
            {
                return;
            }

            EnsureZoneJumpPopupCreated();
            if (_serverJumpForm == null)
            {
                return;
            }

            _serverJumpPanel.Visible = visible;

            if (visible)
            {
                UpdateZoneJumpPopupPlacement();
                if (!_serverJumpForm.Visible)
                {
                    _serverJumpForm.Show(this);
                }
                else
                {
                    _serverJumpForm.BringToFront();
                }
                return;
            }

            if (_serverJumpForm.Visible)
            {
                _serverJumpForm.Hide();
            }
        }

        private async void HandleZoneJumpRequest(int zone)
        {
            if (zone <= 0)
            {
                return;
            }

            string targetUrl = $"http://www.youkia.com/index.php/pvz/s{zone}";
            Uri currentUri = GetCurrentPageUri();
            string cookieHeader = await GetCurrentCookieHeaderAsync(currentUri).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                ApplyCookieHeaderToTarget(targetUrl, cookieHeader);
            }

            _zoneJumpPanelManuallyHidden = true;
            ShowZoneJumpPanel(false);
            _pendingZoneJumpTargetUrl = targetUrl;
            _pendingZoneJumpRetryAvailable = true;
            _pendingZoneJumpRetryConsumed = false;
            HideZoneJumpSavePrompt();
            _pendingZoneJumpSaveMatch = null;
            _zoneJumpSavePollTimer.Stop();
            _zoneJumpSavePollTimer.Start();
            NavigateToAddress(targetUrl);
            UpdateStatus($"正在跳转到 {zone} 区");
        }

        private void HandleZoneFavoriteToggle(int zone)
        {
            _zoneOrderManager.ToggleFavorite(zone);
            RefreshZoneJumpPanelItems();
        }

        private void ApplyCookieHeaderToTarget(string targetUrl, string cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(targetUrl)
                || string.IsNullOrWhiteSpace(cookieHeader)
                || !Uri.TryCreate("http://www.youkia.com", UriKind.Absolute, out Uri rootUri)
                || !Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri targetUri))
            {
                return;
            }

            ClearBrowserCookies(rootUri, targetUri);

            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                _ruffleHost?.ApplyCookies(rootUri, cookieHeader);
                _ruffleHost?.ApplyCookies(targetUri, cookieHeader);
                _ruffleProxy?.SetCookieHeader(rootUri, cookieHeader);
                _ruffleProxy?.SetCookieHeader(targetUri, cookieHeader);
                return;
            }

            _cookieManager.ApplyCookieEntries(rootUri, targetUri, SplitCookieHeaderEntries(cookieHeader));
        }

        private static IEnumerable<string> SplitCookieHeaderEntries(string cookieHeader)
        {
            return (cookieHeader ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Contains("="));
        }

        private void RefreshZoneJumpPanelItems()
        {
            IReadOnlyList<int> zones = _zoneOrderManager.BuildDisplayOrder();
            HashSet<int> favorites = _zoneOrderManager.LoadFavoriteZones();
            string orderSignature = string.Join(",", zones) + "|fav:" + string.Join(",", favorites.OrderBy(value => value));
            _zoneJumpOrderSignature = orderSignature;
            _serverJumpPanel?.SetOrderFilePath(_zoneOrderManager.FilePath);
            _serverJumpPanel?.SetZones(zones, favorites);
        }

        private async void RefreshZoneJumpAvailabilityAsync()
        {
            if (_zoneJumpRefreshInFlight)
            {
                return;
            }

            _zoneJumpRefreshInFlight = true;
            int version = ++_zoneJumpRefreshVersion;
            try
            {
                Uri currentUri = GetCurrentPageUri();
                string cookieHeader = await GetCurrentCookieHeaderAsync(currentUri).ConfigureAwait(true);
                if (IsDisposed || version != _zoneJumpRefreshVersion)
                {
                    return;
                }

                bool available = IsZoneJumpContextAvailable(currentUri, cookieHeader, out string availabilityKey);
                bool popupVisible = _serverJumpForm != null && !_serverJumpForm.IsDisposed && _serverJumpForm.Visible;
                if (!available)
                {
                    _zoneJumpAvailable = false;
                    _zoneJumpAvailabilityKey = string.Empty;
                    btnZoneJump.Enabled = popupVisible;
                    if (!popupVisible)
                    {
                        ShowZoneJumpPanel(false);
                    }
                    return;
                }

                bool availabilityChanged = !string.Equals(_zoneJumpAvailabilityKey, availabilityKey, StringComparison.OrdinalIgnoreCase);
                if (availabilityChanged)
                {
                    _zoneJumpPanelManuallyHidden = false;
                }

                _zoneJumpAvailable = true;
                _zoneJumpAvailabilityKey = availabilityKey;
                btnZoneJump.Enabled = true;
                IReadOnlyList<int> zones = _zoneOrderManager.BuildDisplayOrder();
                HashSet<int> favorites = _zoneOrderManager.LoadFavoriteZones();
                string orderSignature = string.Join(",", zones) + "|fav:" + string.Join(",", favorites.OrderBy(value => value));
                if (availabilityChanged || !string.Equals(_zoneJumpOrderSignature, orderSignature, StringComparison.Ordinal))
                {
                    RefreshZoneJumpPanelItems();
                }

                if (!_zoneJumpPanelManuallyHidden && !popupVisible)
                {
                    ShowZoneJumpPanel(true);
                }
            }
            finally
            {
                _zoneJumpRefreshInFlight = false;
            }
        }

        private static bool IsZoneJumpContextAvailable(Uri currentUri, string cookieHeader, out string availabilityKey)
        {
            availabilityKey = string.Empty;
            if (currentUri == null || string.IsNullOrWhiteSpace(cookieHeader))
            {
                return false;
            }

            string url = currentUri.AbsoluteUri ?? string.Empty;
            if (url.IndexOf("pvz", StringComparison.OrdinalIgnoreCase) < 0
                || url.IndexOf("youkia", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            List<string> cookieKeys = SplitCookieHeaderEntries(cookieHeader)
                .Select(entry => entry.Substring(0, entry.IndexOf('=')).Trim())
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool hasPvz = cookieKeys.Any(key => string.Equals(key, "pvz", StringComparison.OrdinalIgnoreCase));
            bool hasYoukia = cookieKeys.Any(key => string.Equals(key, "youkia", StringComparison.OrdinalIgnoreCase));
            if (!hasPvz || !hasYoukia)
            {
                return false;
            }

            availabilityKey = currentUri.Host + "|" + string.Join(",", cookieKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            return true;
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateToUrl();
            }
        }

        private void BtnProxyTool_Click(object sender, EventArgs e)
        {
            if (_proxySettingsPanel != null && !_proxySettingsPanel.IsDisposed)
            {
                _proxySettingsPanel.Close();
                return;
            }

            CloseCookiePopup();

            _proxySettingsPanel = new ProxySettingsUserControl(_settings, _originalProxySnapshot);
            _proxySettingsPanel.SettingsSaved += ProxySettingsPanel_SettingsSaved;
            _proxySettingsPanel.SettingsCanceled += ProxySettingsPanel_SettingsCanceled;
            _proxySettingsPanel.FormClosed += ProxySettingsPanel_FormClosed;

            ShowPopupForm(_proxySettingsPanel, btnProxyTool);
        }

        private void ProxySettingsPanel_SettingsSaved(object sender, EventArgs e)
        {
            if (_proxySettingsPanel == null)
            {
                return;
            }

            _settings = _proxySettingsPanel.CurrentSettings;
            if (_legacyDirectMode)
            {
                UpdateStatus("当前系统为兼容模式，已保存上游代理设置，但未启用本地映射代理");
                RuntimeDiagnostics.Write("proxy", "legacy direct mode active; proxy settings saved without starting native proxy");
                return;
            }

            _ruffleProxy?.SetUpstreamProxy(ResolveUpstreamProxy());
            RestartProxyService();
        }

        private void ProxySettingsPanel_SettingsCanceled(object sender, EventArgs e)
        {
        }

        private void ProxySettingsPanel_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_proxySettingsPanel != null)
            {
                _proxySettingsPanel.SettingsSaved -= ProxySettingsPanel_SettingsSaved;
                _proxySettingsPanel.SettingsCanceled -= ProxySettingsPanel_SettingsCanceled;
                _proxySettingsPanel.FormClosed -= ProxySettingsPanel_FormClosed;
                _proxySettingsPanel = null;
            }
        }

        private void BtnCookieTool_Click(object sender, EventArgs e)
        {
            if (_cookieSelectionForm != null && !_cookieSelectionForm.IsDisposed)
            {
                _cookieSelectionForm.Close();
                return;
            }

            CloseProxyPopup();

            _cookieSelectionForm = new CookieSelectionForm(this);
            _cookieSelectionForm.FormClosed += CookieSelectionForm_FormClosed;
            _cookieSelectionForm.SetCookieFiles(_cookieDisplayEntries);

            ShowPopupForm(_cookieSelectionForm, btnCookieTool);
        }

        private void BtnFlashFullscreen_Click(object sender, EventArgs e)
        {
            TryToggleEmbeddedFlashFullscreen();
        }

        private void CookieSelectionForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_cookieSelectionForm != null)
            {
                SaveCookiePopupPlacement(_cookieSelectionForm);
                _cookieSelectionForm.FormClosed -= CookieSelectionForm_FormClosed;
                _cookieSelectionForm = null;
            }
        }

        private void ServerJumpForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_serverJumpForm != null)
            {
                _serverJumpForm.FormClosed -= ServerJumpForm_FormClosed;
                _serverJumpForm = null;
            }
            _serverJumpPanel = null;
            _zoneJumpPanelManuallyHidden = true;
        }

        private void Browser_FormClosing(object sender, FormClosingEventArgs e)
        {
            _zoneJumpMonitorTimer?.Stop();
            _zoneJumpSavePollTimer?.Stop();
            SaveCookiePopupPlacement(_cookieSelectionForm);
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
            }

            if (_allowImmediateClose)
            {
                return;
            }

            if (_shutdownCleanupStarted)
            {
                e.Cancel = true;
                return;
            }

            _shutdownCleanupStarted = true;
            e.Cancel = true;

            try
            {
                Enabled = false;
                ShowInTaskbar = false;
                Opacity = 0;
                Hide();
            }
            catch
            {
            }

            CloseProxyPopup();
            CloseCookiePopup();
            CloseZoneJumpPopup();
            BeginShutdownCleanup();
        }

        private async void BeginShutdownCleanup()
        {
            try
            {
                await Task.Run(() => CleanupNonUiResources()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"background cleanup failed error={ex}");
            }

            if (IsDisposed)
            {
                return;
            }

            _allowImmediateClose = true;
            try
            {
                Close();
            }
            catch
            {
            }
        }

        private void CleanupNonUiResources()
        {
            try
            {
                ClearBrowserCookies();
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"clear browser cookies failed error={ex}");
            }

            try
            {
                StopNativeProxy();
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"stop native proxy failed error={ex}");
            }

            try
            {
                _proxyManager.RestoreProxy(_originalProxySnapshot);
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"restore system proxy failed error={ex}");
            }

            try
            {
                watcher?.Dispose();
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"dispose watcher failed error={ex}");
            }

            try
            {
                _ruffleProxy?.Dispose();
                _ruffleProxy = null;
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"dispose ruffle proxy failed error={ex}");
            }

            try
            {
                _zoneJumpSavePollTimer?.Stop();
                _cookieImportToastTimer?.Stop();
            }
            catch
            {
            }

            try
            {
                if (_nativeProxy != IntPtr.Zero)
                {
                    FlashProxyNative.flash_proxy_destroy(_nativeProxy);
                    _nativeProxy = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("shutdown", $"destroy native proxy failed error={ex}");
            }
        }

        private void ShowPopupForm(Form popup, Control anchor)
        {
            popup.StartPosition = FormStartPosition.Manual;

            Point anchorPoint = anchor.PointToScreen(Point.Empty);
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(workingArea.Left + 12, anchorPoint.X - popup.Width - 12);
            int y = Math.Max(workingArea.Top + 72, anchorPoint.Y);

            if (popup is CookieSelectionForm)
            {
                if (!TryGetSavedCookiePopupLocation(popup.Size, out Point savedLocation))
                {
                    x = Right;
                    y = Math.Max(workingArea.Top + 12, GetActiveBrowserSurface().PointToScreen(Point.Empty).Y);
                }
                else
                {
                    x = savedLocation.X;
                    y = savedLocation.Y;
                }
            }

            popup.Location = ClampPopupLocation(new Point(x, y), popup.Size);
            popup.Show(this);
            UpdateCookiePopupPlacement(true);
            popup.BringToFront();
        }

        private bool TryGetSavedCookiePopupLocation(Size popupSize, out Point location)
        {
            int savedLeft = Settings.Default.CookiePanelLeft;
            int savedTop = Settings.Default.CookiePanelTop;
            if (savedLeft == -1 && savedTop == -1)
            {
                location = Point.Empty;
                return false;
            }

            location = ClampPopupLocation(new Point(Left + savedLeft, Top + savedTop), popupSize);
            return true;
        }

        private void UpdateCookiePopupPlacement(bool keepCurrentRelativeOffset)
        {
            if (_cookieSelectionForm == null || _cookieSelectionForm.IsDisposed)
            {
                return;
            }

            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            Control browserSurface = GetActiveBrowserSurface();
            Point browserOrigin = browserSurface.PointToScreen(Point.Empty);
            int desiredHeight = Math.Max(360, browserSurface.Height);
            _cookieSelectionForm.Height = Math.Min(desiredHeight, workingArea.Height - 24);

            Point targetLocation;
            if (keepCurrentRelativeOffset)
            {
                Point relativeOffset = new Point(_cookieSelectionForm.Left - Left, _cookieSelectionForm.Top - Top);
                targetLocation = new Point(Left + relativeOffset.X, Top + relativeOffset.Y);
            }
            else if (TryGetSavedCookiePopupLocation(_cookieSelectionForm.Size, out Point savedLocation))
            {
                targetLocation = savedLocation;
            }
            else
            {
                targetLocation = new Point(Right, Math.Max(workingArea.Top + 12, browserOrigin.Y));
            }

            _cookieSelectionForm.Location = ClampPopupLocation(targetLocation, _cookieSelectionForm.Size);
        }

        private void UpdateZoneJumpPopupPlacement()
        {
            if (_serverJumpForm == null || _serverJumpForm.IsDisposed)
            {
                return;
            }

            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            Control browserSurface = GetActiveBrowserSurface();
            Point browserOrigin = browserSurface.PointToScreen(Point.Empty);
            int desiredHeight = Math.Max(360, browserSurface.Height);
            _serverJumpForm.Height = Math.Min(desiredHeight, workingArea.Height - 24);

            int x = browserOrigin.X - _serverJumpForm.Width - 8;
            if (x < workingArea.Left + 12)
            {
                x = workingArea.Left + 12;
            }

            int y = Math.Max(workingArea.Top + 12, browserOrigin.Y);
            _serverJumpForm.Location = ClampPopupLocation(new Point(x, y), _serverJumpForm.Size);
        }

        private Point ClampPopupLocation(Point location, Size popupSize)
        {
            Rectangle workingArea = Screen.FromPoint(location).WorkingArea;
            int x = location.X;
            int y = location.Y;

            if (x < workingArea.Left)
            {
                x = workingArea.Left;
            }

            if (y < workingArea.Top + 12)
            {
                y = workingArea.Top + 12;
            }

            if (x + popupSize.Width > workingArea.Right - 12)
            {
                x = Math.Max(workingArea.Left, workingArea.Right - popupSize.Width - 12);
            }

            if (y + popupSize.Height > workingArea.Bottom - 12)
            {
                y = Math.Max(workingArea.Top + 12, workingArea.Bottom - popupSize.Height - 12);
            }

            return new Point(x, y);
        }

        private void SaveCookiePopupPlacement(Form popup)
        {
            if (popup == null || popup.IsDisposed)
            {
                return;
            }

            Point location = popup.Location;
            if (location.X < 0 && location.Y < 0)
            {
                return;
            }

            Settings.Default.CookiePanelLeft = location.X - Left;
            Settings.Default.CookiePanelTop = location.Y - Top;
            Settings.Default.Save();
        }

        private void MoveFloatingPopupWithWindow(Form popup, int deltaX, int deltaY)
        {
            if (popup == null || popup.IsDisposed || !popup.Visible)
            {
                return;
            }

            popup.Location = ClampPopupLocation(
                new Point(popup.Left + deltaX, popup.Top + deltaY),
                popup.Size);
        }

        private void CloseProxyPopup()
        {
            if (_proxySettingsPanel != null && !_proxySettingsPanel.IsDisposed)
            {
                _proxySettingsPanel.Close();
            }
        }

        private void CloseCookiePopup()
        {
            if (_cookieSelectionForm != null && !_cookieSelectionForm.IsDisposed)
            {
                _cookieSelectionForm.Close();
            }
        }

        private void CloseZoneJumpPopup()
        {
            if (_serverJumpForm != null && !_serverJumpForm.IsDisposed)
            {
                _serverJumpForm.Close();
            }
        }

        private void InitializeProxySystem()
        {
            if (_legacyDirectMode)
            {
                RuntimeDiagnostics.Write("proxy", "legacy direct mode enabled; native proxy startup skipped");
                UpdateStatus("当前系统为兼容模式，已跳过本地映射代理，浏览器将直接联网");
                return;
            }

            try
            {
                RestartProxyService();
            }
            catch (Exception ex)
            {
                UpdateStatus($"本地映射代理启动失败: {ex.Message}");
                MessageBox.Show($"本地映射代理启动失败：{ex.Message}");
            }
        }

        private void RestartProxyService()
        {
            if (_legacyDirectMode)
            {
                RuntimeDiagnostics.Write("proxy", "legacy direct mode enabled; restart request ignored");
                return;
            }

            EnsureNativeProxy();

            _currentPort = FindAvailablePort(9000, 9999);
            if (_currentPort == -1)
            {
                UpdateStatus("找不到可用端口 (9000-9999)");
                return;
            }

            ConfigureNativeProxy();
            StartNativeProxy(_currentPort);
            ApplyBrowserProxy(_currentPort);
            UpdateStatus($"代理运行中，监听端口 {_currentPort}");
        }

        private void ApplyBrowserProxy(int port)
        {
            if (_legacyDirectMode)
            {
                return;
            }

            _proxyManager.SetProxyFromLocalPort(port);
        }

        private void EnsureNativeProxy()
        {
            if (_nativeProxy != IntPtr.Zero)
            {
                return;
            }

            FlashProxyNative.EnsureLoaded();
            _nativeProxy = FlashProxyNative.flash_proxy_create();
            if (_nativeProxy == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建 flash_proxy_core 句柄。");
            }
        }

        private void ConfigureNativeProxy()
        {
            string cacheRoot = _localMappingRules.CacheRootPath;
            Directory.CreateDirectory(cacheRoot);

            if (FlashProxyNative.flash_proxy_set_cache_root(_nativeProxy, cacheRoot) == 0)
            {
                throw new InvalidOperationException("设置本地 cache 目录失败。");
            }

            FlashProxyNative.flash_proxy_clear_mapping_hosts(_nativeProxy);
            foreach (string host in _localMappingRules.NativeHostFragments)
            {
                FlashProxyNative.flash_proxy_add_mapping_host(_nativeProxy, host);
            }

            FlashProxyNative.flash_proxy_clear_mapping_url_keywords(_nativeProxy);
            foreach (string keyword in _localMappingRules.UrlKeywords)
            {
                FlashProxyNative.flash_proxy_add_mapping_url_keyword(_nativeProxy, keyword);
            }

            RuntimeDiagnostics.Write("localmap", $"native proxy mapping configured {_localMappingRules.Describe()}");

            string upstreamProxy = ResolveUpstreamProxy();
            if (FlashProxyNative.flash_proxy_set_upstream_proxy(_nativeProxy, upstreamProxy ?? string.Empty) == 0)
            {
                throw new InvalidOperationException("设置上游代理失败。");
            }
        }

        private string ResolveUpstreamProxy()
        {
            if (_settings.UseCustomProxy && !string.IsNullOrWhiteSpace(_settings.CustomProxy))
            {
                return _settings.CustomProxy.Trim();
            }

            if (_settings.UseSystemProxy &&
                _originalProxySnapshot != null &&
                _originalProxySnapshot.Enabled &&
                !string.IsNullOrWhiteSpace(_originalProxySnapshot.ProxyServer))
            {
                string systemProxy = NormalizeProxyServer(_originalProxySnapshot.ProxyServer);
                string localProxy = $"127.0.0.1:{_currentPort}";
                if (!string.IsNullOrWhiteSpace(systemProxy) &&
                    !systemProxy.Equals(localProxy, StringComparison.OrdinalIgnoreCase))
                {
                    return systemProxy;
                }
            }

            return string.Empty;
        }

        private static string NormalizeProxyServer(string rawProxyServer)
        {
            if (string.IsNullOrWhiteSpace(rawProxyServer))
            {
                return string.Empty;
            }

            string[] segments = rawProxyServer.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                string value = segment.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                int equalsIndex = value.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    string scheme = value.Substring(0, equalsIndex).Trim();
                    string endpoint = value.Substring(equalsIndex + 1).Trim();
                    if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                        scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        return StripProxyScheme(endpoint);
                    }
                    continue;
                }

                return StripProxyScheme(value);
            }

            return string.Empty;
        }

        private static string StripProxyScheme(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Trim();
            int schemeIndex = normalized.IndexOf("://", StringComparison.Ordinal);
            return schemeIndex >= 0 ? normalized.Substring(schemeIndex + 3) : normalized;
        }

        private void StartNativeProxy(int port)
        {
            StopNativeProxy();

            if (FlashProxyNative.flash_proxy_start(_nativeProxy, port, out int actualPort) == 0)
            {
                string lastError = FlashProxyNative.GetLastError(_nativeProxy);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "启动本地代理失败。" : lastError);
            }

            _currentPort = actualPort;
        }

        private void StopNativeProxy()
        {
            if (_nativeProxy != IntPtr.Zero)
            {
                FlashProxyNative.flash_proxy_stop(_nativeProxy);
            }
        }

        private int FindAvailablePort(int startPort, int endPort)
        {
            var ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpListeners = ipProperties.GetActiveTcpListeners();
            var udpListeners = ipProperties.GetActiveUdpListeners();

            HashSet<int> usedPorts = new HashSet<int>(tcpListeners.Select(listener => listener.Port));
            foreach (var listener in udpListeners)
            {
                usedPorts.Add(listener.Port);
            }

            for (int port = startPort; port <= endPort; port++)
            {
                if (!usedPorts.Contains(port))
                {
                    return port;
                }
            }

            return -1;
        }

        private Uri GetCurrentPageUri()
        {
            if (_browserMode == BrowserBackendMode.NativeIe && webBrowser.Url != null)
            {
                return webBrowser.Url;
            }

            if (Uri.TryCreate(txtUrl.Text, UriKind.Absolute, out Uri currentUri))
            {
                return currentUri;
            }

            return null;
        }

        private string GetCurrentPageTitleHint(Uri currentUri)
        {
            string title = string.Empty;
            if (_browserMode == BrowserBackendMode.NativeIe)
            {
                title = webBrowser.DocumentTitle ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(title) && currentUri != null)
            {
                title = currentUri.Host ?? string.Empty;
            }

            return title;
        }

        private async Task<string> GetCurrentCookieHeaderAsync(Uri currentUri)
        {
            if (currentUri == null)
            {
                return string.Empty;
            }

            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    return string.Empty;
                }

                try
                {
                    Uri[] candidateUris = BuildCookieProbeUris(currentUri).ToArray();
                    string cookieManagerHeader = await _ruffleHost.GetCookieHeaderAsync(candidateUris).ConfigureAwait(true);
                    string scriptResult = await _ruffleHost.ExecuteScriptAsync("document.cookie").ConfigureAwait(true);
                    string documentCookie = DecodeWebView2String(scriptResult);
                    string mergedCookie = MergeCookieHeaders(cookieManagerHeader, documentCookie);
                    if (!string.IsNullOrWhiteSpace(mergedCookie))
                    {
                        return mergedCookie;
                    }

                    return string.Empty;
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Write("cookie-save", $"read ruffle cookie failed error={ex.Message}");
                    return string.Empty;
                }
            }

            try
            {
                string internetCookie = ReadInternetCookieHeaderFromCandidates(currentUri);
                string documentCookie = webBrowser.Document?.Cookie ?? string.Empty;
                string mergedCookie = MergeCookieHeaders(internetCookie, documentCookie);
                if (!string.IsNullOrWhiteSpace(mergedCookie))
                {
                    return mergedCookie;
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("cookie-save", $"read ie cookie failed error={ex.Message}");
                return string.Empty;
            }
        }

        private async Task<CookieProfileManager.SaveCookieMatch> FindSavableCookieMatchAsync(Uri currentUri)
        {
            if (currentUri == null)
            {
                return null;
            }

            foreach (Uri candidateUri in BuildCookieProbeUris(currentUri))
            {
                string cookieHeader = await GetCookieHeaderForCandidateAsync(currentUri, candidateUri).ConfigureAwait(true);
                CookieProfileManager.SaveCookieMatch match = CookieProfileManager.MatchSavableCookies(candidateUri, cookieHeader);
                RuntimeDiagnostics.Write(
                    "cookie-save",
                    $"probe page={currentUri} candidate={candidateUri} cookieLength={(cookieHeader ?? string.Empty).Length} matched={(match != null)}");
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private async Task<string> GetCookieHeaderForCandidateAsync(Uri currentUri, Uri candidateUri)
        {
            if (candidateUri == null)
            {
                return string.Empty;
            }

            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    return string.Empty;
                }

                try
                {
                    string cookieManagerHeader = await _ruffleHost.GetCookieHeaderAsync(candidateUri).ConfigureAwait(true);
                    string proxyHeader = _ruffleProxy?.GetRememberedCookieHeader(candidateUri) ?? string.Empty;
                    if (currentUri != null
                        && string.Equals(currentUri.Host, candidateUri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        string scriptResult = await _ruffleHost.ExecuteScriptAsync("document.cookie").ConfigureAwait(true);
                        string documentCookie = DecodeWebView2String(scriptResult);
                        return MergeCookieHeaders(cookieManagerHeader, proxyHeader, documentCookie);
                    }

                    return MergeCookieHeaders(cookieManagerHeader, proxyHeader);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Write("cookie-save", $"read candidate ruffle cookie failed uri={candidateUri} error={ex.Message}");
                    return string.Empty;
                }
            }

            try
            {
                string internetCookie = ReadInternetCookieHeader(candidateUri);
                if (currentUri != null
                    && string.Equals(currentUri.Host, candidateUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    string documentCookie = webBrowser.Document?.Cookie ?? string.Empty;
                    return MergeCookieHeaders(internetCookie, documentCookie);
                }

                return internetCookie ?? string.Empty;
            }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Write("cookie-save", $"read candidate ie cookie failed uri={candidateUri} error={ex.Message}");
                return string.Empty;
            }
        }

        private static string ReadInternetCookieHeaderFromCandidates(Uri currentUri)
        {
            return MergeCookieHeaders(BuildCookieProbeUris(currentUri).Select(ReadInternetCookieHeader));
        }

        private static IEnumerable<Uri> BuildCookieProbeUris(Uri currentUri)
        {
            if (currentUri == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Uri candidate in EnumerateCookieProbeUris(currentUri))
            {
                if (candidate != null && seen.Add(candidate.AbsoluteUri))
                {
                    yield return candidate;
                }
            }
        }

        private static IEnumerable<Uri> EnumerateCookieProbeUris(Uri currentUri)
        {
            yield return currentUri;

            string legacyRedirectTarget = CookieProfileManager.ResolveLegacyYoukiaRedirectTarget(currentUri);
            if (!string.IsNullOrWhiteSpace(legacyRedirectTarget)
                && Uri.TryCreate(legacyRedirectTarget, UriKind.Absolute, out Uri legacyTargetUri))
            {
                yield return legacyTargetUri;
            }

            string authority = currentUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (Uri.TryCreate(authority + "/", UriKind.Absolute, out Uri authorityRoot))
            {
                yield return authorityRoot;
            }

            if (Uri.TryCreate(authority + "/index.php", UriKind.Absolute, out Uri authorityIndex))
            {
                yield return authorityIndex;
            }

            if (Uri.TryCreate(authority + "/pvz/index.php/default/main", UriKind.Absolute, out Uri authorityMain))
            {
                yield return authorityMain;
            }

            if (ShouldProbeYoukiaCookieDomain(currentUri))
            {
                foreach (string candidate in new[]
                {
                    "http://youkia.com/",
                    "http://youkia.com/index.php",
                    "http://pvz.youkia.com/",
                    "http://pvz.youkia.com/index.php",
                    "http://pvz.youkia.com/pvz/index.php/default/main",
                    "http://www.youkia.com/",
                    "http://www.youkia.com/index.php",
                    "http://www.youkia.com/pvz/index.php/default/main"
                })
                {
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri parsed))
                    {
                        yield return parsed;
                    }
                }
            }
        }

        private static bool ShouldProbeYoukiaCookieDomain(Uri currentUri)
        {
            if (currentUri == null)
            {
                return false;
            }

            string host = currentUri.Host ?? string.Empty;
            string url = currentUri.AbsoluteUri ?? string.Empty;
            return host.EndsWith(".youkia.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "youkia.com", StringComparison.OrdinalIgnoreCase)
                || url.IndexOf("youkia", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("pvz", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadInternetCookieHeader(Uri currentUri)
        {
            if (currentUri == null)
            {
                return string.Empty;
            }

            int size = 8192;
            var builder = new StringBuilder(size);
            if (!InternetGetCookieEx(currentUri.AbsoluteUri, null, builder, ref size, InternetCookieHttpOnly, IntPtr.Zero))
            {
                if (size <= 0)
                {
                    return string.Empty;
                }

                builder = new StringBuilder(size);
                if (!InternetGetCookieEx(currentUri.AbsoluteUri, null, builder, ref size, InternetCookieHttpOnly, IntPtr.Zero))
                {
                    return string.Empty;
                }
            }

            return builder.ToString();
        }

        private static string MergeCookieHeaders(params string[] cookieHeaders)
        {
            return MergeCookieHeaders((IEnumerable<string>)cookieHeaders);
        }

        private static string MergeCookieHeaders(IEnumerable<string> cookieHeaders)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string header in cookieHeaders ?? Enumerable.Empty<string>())
            {
                foreach (string entry in SplitCookieHeaderEntries(header))
                {
                    int equalsIndex = entry.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        continue;
                    }

                    string key = entry.Substring(0, equalsIndex).Trim();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    merged[key] = entry.Trim();
                }
            }

            return string.Join("; ", merged.Values);
        }

        private static bool IsHttpUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string DecodeWebView2String(string scriptResult)
        {
            if (string.IsNullOrWhiteSpace(scriptResult) || string.Equals(scriptResult, "null", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string normalized = scriptResult.Trim();
            if (normalized.Length >= 2 && normalized[0] == '"' && normalized[normalized.Length - 1] == '"')
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return Regex.Unescape(normalized);
        }

        private void ClearBrowserCookies(params Uri[] candidateUris)
        {
            try
            {
                ClearRuffleCookies();
            }
            catch
            {
            }

            try
            {
                _cookieManager.ClearAllCookies(candidateUris);
            }
            catch
            {
            }
        }

        private void InitializeCookieImportDragDrop()
        {
            btnCookieTool.AllowDrop = true;
            btnCookieTool.DragEnter += BtnCookieTool_DragEnter;
            btnCookieTool.DragOver += BtnCookieTool_DragOver;
            btnCookieTool.DragLeave += BtnCookieTool_DragLeave;
            btnCookieTool.DragDrop += BtnCookieTool_DragDrop;
        }

        private void BtnCookieTool_DragEnter(object sender, DragEventArgs e)
        {
            UpdateCookieButtonDropState(e);
        }

        private void BtnCookieTool_DragOver(object sender, DragEventArgs e)
        {
            UpdateCookieButtonDropState(e);
        }

        private void BtnCookieTool_DragLeave(object sender, EventArgs e)
        {
            SetCookieToolDropVisual(false);
            _cookieSelectionForm?.SetDropOverlayVisible(false);
        }

        private void BtnCookieTool_DragDrop(object sender, DragEventArgs e)
        {
            SetCookieToolDropVisual(false);
            _cookieSelectionForm?.SetDropOverlayVisible(false);

            if (!TryGetDroppedCookieXmlFiles(e.Data, out string[] filePaths))
            {
                return;
            }

            ImportCookieProfileFiles(filePaths);
        }

        private void UpdateCookieButtonDropState(DragEventArgs e)
        {
            bool canImport = TryGetDroppedCookieXmlFiles(e.Data, out _);
            e.Effect = canImport ? DragDropEffects.Copy : DragDropEffects.None;
            SetCookieToolDropVisual(canImport);
            _cookieSelectionForm?.SetDropOverlayVisible(canImport);
        }

        private void SetCookieToolDropVisual(bool active)
        {
            btnCookieTool.BackColor = active ? Color.FromArgb(219, 234, 254) : _cookieToolDefaultBackColor;
            btnCookieTool.FlatAppearance.BorderColor = active
                ? Color.FromArgb(59, 130, 246)
                : Color.FromArgb(229, 231, 235);
        }

        private async void ZoneJumpSavePollTimer_Tick(object sender, EventArgs e)
        {
            if (_zoneJumpSavePollInFlight)
            {
                return;
            }

            Uri currentUri = GetCurrentPageUri();
            if (currentUri == null)
            {
                return;
            }

            _zoneJumpSavePollInFlight = true;
            try
            {
                CookieProfileManager.SaveCookieMatch match = await FindSavableCookieMatchAsync(currentUri).ConfigureAwait(true);
                if (match == null)
                {
                    return;
                }

                _pendingZoneJumpSaveMatch = match;
                _zoneJumpSavePollTimer.Stop();
                ShowZoneJumpSavePrompt(match);
            }
            finally
            {
                _zoneJumpSavePollInFlight = false;
            }
        }

        private void CookieImportToastTimer_Tick(object sender, EventArgs e)
        {
            _cookieImportToastTimer.Stop();
            if (_cookieImportToastPanel != null)
            {
                _cookieImportToastPanel.Visible = false;
            }
        }

        private void ShowZoneJumpSavePrompt(CookieProfileManager.SaveCookieMatch match)
        {
            if (_zoneJumpSavePromptPanel == null || match == null)
            {
                return;
            }

            _zoneJumpSavePromptLabel.Text = "检测到可保存的 Cookie，是否现在保存？";
            PositionZoneJumpSavePrompt();
            _zoneJumpSavePromptPanel.Visible = true;
            _zoneJumpSavePromptPanel.BringToFront();
        }

        private void HideZoneJumpSavePrompt()
        {
            if (_zoneJumpSavePromptPanel != null)
            {
                _zoneJumpSavePromptPanel.Visible = false;
            }
        }

        private void HandleZoneJumpSavePromptAction(bool saveAndApply, bool dismissOnly)
        {
            CookieProfileManager.SaveCookieMatch match = _pendingZoneJumpSaveMatch;
            _pendingZoneJumpSaveMatch = null;
            HideZoneJumpSavePrompt();
            if (dismissOnly || match == null)
            {
                return;
            }

            FileInfo savedFile = _cookieProfileManager.SaveProfileFromPage(
                match.SourceUri,
                match.PersistedCookies,
                GetCurrentPageTitleHint(match.SourceUri));
            if (savedFile == null)
            {
                UpdateStatus("保存 Cookie 失败");
                return;
            }

            LoadCookieFiles();
            UpdateStatus($"已保存 Cookie：{savedFile.Name}");
            if (saveAndApply)
            {
                ApplyCookieProfileFile(savedFile.FullName);
            }
        }

        private void ShowCookieImportToast(string message)
        {
            if (_cookieImportToastPanel == null || _cookieImportToastLabel == null)
            {
                return;
            }

            _cookieImportToastLabel.Text = message ?? string.Empty;
            PositionCookieImportToast();
            _cookieImportToastPanel.Visible = true;
            _cookieImportToastPanel.BringToFront();
            _cookieImportToastTimer.Stop();
            _cookieImportToastTimer.Start();
        }

        private void PositionCookieImportToast()
        {
            if (_cookieImportToastPanel == null)
            {
                return;
            }

            int x = Math.Max(12, ClientSize.Width - _cookieImportToastPanel.Width - 24);
            int y = Math.Max(72, ClientSize.Height - _cookieImportToastPanel.Height - 56);
            _cookieImportToastPanel.Location = new Point(x, y);
        }

        private void UpdateStatus(string message)
        {
            lblStatus.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        }

        private Control GetActiveBrowserSurface()
        {
            if (_browserMode == BrowserBackendMode.RuffleWebView2 && _ruffleHost?.ViewControl != null && _ruffleHost.ViewControl.Visible)
            {
                return _ruffleHost.ViewControl;
            }

            return webBrowser;
        }

        private async void TryToggleEmbeddedFlashFullscreen()
        {
            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                if (_ruffleHost == null || !_ruffleHost.IsInitialized)
                {
                    UpdateStatus("Ruffle 页面尚未完成初始化");
                    return;
                }

                try
                {
                    string script = @"
(function(){
    if(typeof window.__pvzolToggleEmbeddedFullscreen==='function'){
        return window.__pvzolToggleEmbeddedFullscreen();
    }
    var node=document.querySelector('ruffle-player,ruffle-embed,ruffle-object');
    if(!node){
        return 'missing';
    }
    var methods=['enterFullscreen','requestFullscreen','webkitRequestFullscreen'];
    for(var i=0;i<methods.length;i++){
        var fn=node[methods[i]];
        if(typeof fn==='function'){
            try{fn.call(node);return 'enter';}catch(e){}
        }
    }
    if(typeof node.setFullscreen==='function'){
        try{node.setFullscreen(true);return 'enter';}catch(e){}
    }
    return 'missing';
})();";
                    string result = await _ruffleHost.ExecuteScriptAsync(script).ConfigureAwait(true);
                    if ((result ?? string.Empty).Contains("enter"))
                    {
                        UpdateStatus("已切换到浏览器窗口内的 Ruffle 全屏");
                    }
                    else if ((result ?? string.Empty).Contains("exit"))
                    {
                        UpdateStatus("已退出浏览器窗口内的 Ruffle 全屏");
                    }
                    else
                    {
                        UpdateStatus("当前页面没有可全屏的 Ruffle 容器");
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Flash 全屏触发失败: {ex.Message}");
                }

                return;
            }

            try
            {
                if (webBrowser.Document == null)
                {
                    UpdateStatus("当前页面尚未完成加载");
                    return;
                }

                HtmlElement documentBody = webBrowser.Document.Body;
                if (documentBody == null)
                {
                    UpdateStatus("当前页面没有可用内容");
                    return;
                }

                string script = @"
(function () {
    function getStyleText(node) {
        if (!node) return '';
        try { return node.getAttribute('style') || ''; } catch (e) { return ''; }
    }

    function setStyleText(node, value) {
        if (!node) return;
        try {
            if (value) {
                node.setAttribute('style', value);
            } else {
                node.removeAttribute('style');
            }
        } catch (e) {}
    }

    function isMarked(node) {
        try { return node && node.getAttribute('data-pvzol-inline-fullscreen') === '1'; } catch (e) { return false; }
    }

    function storeStyle(node, key) {
        if (!node) return;
        try { node.setAttribute(key, getStyleText(node)); } catch (e) {}
    }

    function restoreStyle(node, key) {
        if (!node) return;
        try {
            var value = node.getAttribute(key) || '';
            setStyleText(node, value);
            node.removeAttribute(key);
        } catch (e) {}
    }

    function tryNativeFullscreen(node) {
        if (!node) return false;
        if (typeof node.focus === 'function') {
            try { node.focus(); } catch (e) {}
        }
        var methods = ['requestFullscreen', 'webkitRequestFullscreen', 'mozRequestFullScreen', 'msRequestFullscreen'];
        for (var i = 0; i < methods.length; i++) {
            var fn = node[methods[i]];
            if (typeof fn === 'function') {
                try { fn.call(node); return true; } catch (e) {}
            }
        }
        return false;
    }

    function enterInlineFullscreen(node) {
        if (!node) return 'missing';
        var parent = node.parentElement || node.parentNode;
        storeStyle(node, 'data-pvzol-prev-style');
        storeStyle(parent, 'data-pvzol-prev-style');
        storeStyle(document.body, 'data-pvzol-prev-style');
        storeStyle(document.documentElement, 'data-pvzol-prev-style');
        try { node.setAttribute('data-pvzol-inline-fullscreen', '1'); } catch (e) {}
        setStyleText(document.documentElement, 'width:100%;height:100%;overflow:hidden;background:#000;margin:0;padding:0;');
        setStyleText(document.body, 'width:100%;height:100%;overflow:hidden;background:#000;margin:0;padding:0;');
        if (parent) {
            setStyleText(parent, 'position:fixed;left:0;top:0;width:100%;height:100%;margin:0;padding:0;z-index:2147483646;background:#000;overflow:hidden;');
        }
        setStyleText(node, 'position:fixed;left:0;top:0;width:100%;height:100%;margin:0;padding:0;z-index:2147483647;background:#000;');
        if (typeof node.focus === 'function') {
            try { node.focus(); } catch (e) {}
        }
        return 'inline-enter';
    }

    function exitInlineFullscreen(node) {
        if (!node) return 'missing';
        var parent = node.parentElement || node.parentNode;
        restoreStyle(node, 'data-pvzol-prev-style');
        restoreStyle(parent, 'data-pvzol-prev-style');
        restoreStyle(document.body, 'data-pvzol-prev-style');
        restoreStyle(document.documentElement, 'data-pvzol-prev-style');
        try { node.removeAttribute('data-pvzol-inline-fullscreen'); } catch (e) {}
        return 'inline-exit';
    }

    var nodes = document.querySelectorAll ? document.querySelectorAll('object, embed') : [];
    for (var j = 0; j < nodes.length; j++) {
        if (isMarked(nodes[j])) {
            return exitInlineFullscreen(nodes[j]);
        }
    }

    if (document.fullscreenElement || document.msFullscreenElement || document.webkitFullscreenElement) {
        if (document.exitFullscreen) { document.exitFullscreen(); return 'exit'; }
        if (document.msExitFullscreen) { document.msExitFullscreen(); return 'exit'; }
        if (document.webkitExitFullscreen) { document.webkitExitFullscreen(); return 'exit'; }
    }

    for (var i = 0; i < nodes.length; i++) {
        if (tryNativeFullscreen(nodes[i])) return 'ok';
        return enterInlineFullscreen(nodes[i]);
    }

    return 'missing';
})();";

                object result = webBrowser.Document.InvokeScript("eval", new object[] { script });
                string state = Convert.ToString(result) ?? string.Empty;
                switch (state)
                {
                    case "ok":
                        UpdateStatus("已尝试让页面内 Flash 进入全屏");
                        break;
                    case "inline-enter":
                        UpdateStatus("已切换到页面内 Flash 全屏");
                        break;
                    case "inline-exit":
                        UpdateStatus("已退出页面内 Flash 全屏");
                        break;
                    case "exit":
                        UpdateStatus("已尝试退出页面内全屏");
                        break;
                    default:
                        webBrowser.Focus();
                        SendKeys.SendWait("{F11}");
                        UpdateStatus("当前页面未暴露 Flash 全屏接口，已尝试触发浏览器全屏");
                        break;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Flash 全屏触发失败: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_shutdownCleanupStarted)
                {
                    CloseProxyPopup();
                    CloseCookiePopup();
                    CloseZoneJumpPopup();
                    CleanupNonUiResources();
                }

                watcher = null;
                if (_ruffleHost != null)
                {
                    _ruffleHost.Dispose();
                    _ruffleHost = null;
                }

                components?.Dispose();
            }

            base.Dispose(disposing);
        }

        [ComVisible(true)]
        public sealed class BrowserScriptBridge
        {
            private readonly Browser _browser;

            public BrowserScriptBridge(Browser browser)
            {
                _browser = browser;
            }

            public void NavigateInPlace(string url)
            {
                _browser?.NavigateInPlace(url);
            }

            public void RememberPopupTarget(string url)
            {
                _browser?.RememberPopupTarget(url);
            }
        }

        private sealed class CookieManager
        {
            private static readonly string[] RelevantUrlKeywords =
            {
                "pvz",
                "youkia",
                "pvzol"
            };

            private static readonly string[] KnownRelevantUrls =
            {
                "http://youkia.com/",
                "http://youkia.com/index.php",
                "http://pvz.youkia.com/",
                "http://pvz.youkia.com/index.php",
                "http://pvz.youkia.com/pvz/index.php/default/main",
                "http://www.youkia.com/",
                "http://www.youkia.com/index.php",
                "http://www.youkia.com/pvz/index.php/default/main",
                "http://pvzol.org/",
                "http://pvzol.org/pvz/index.php/default/main"
            };

            private static readonly string[] RelevantCookiePaths =
            {
                "/",
                "/index.php",
                "/index.php/pvz",
                "/pvz",
                "/pvz/",
                "/pvz/index.php",
                "/pvz/index.php/default",
                "/pvz/index.php/default/main"
            };

            [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern bool InternetSetCookie(string lpszUrlName, string lpszCookieName, string lpszCookieData);

            [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern int InternetSetCookieEx(
                string lpszUrl,
                string lpszCookieName,
                string lpszCookieData,
                int dwFlags,
                IntPtr dwReserved);

            [DllImport("wininet.dll", SetLastError = true)]
            private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int lpdwBufferLength);

            private const int InternetOptionEndBrowserSession = 42;

            private Uri _currentDomain;
            private string _cookies = string.Empty;
            private readonly HashSet<string> _appliedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _appliedCookieNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void UpdateCurrentDomain(string url)
            {
                _currentDomain = new Uri(url);
            }

            public void SetCookies(Uri url)
            {
                if (_currentDomain?.Host != url.Host)
                {
                    _currentDomain = url;
                    _cookies = string.Empty;
                }

                if (!string.IsNullOrEmpty(_cookies))
                {
                    InternetSetCookie(url.ToString(), null, _cookies);
                }
            }

            public void UpdateCookies(string domain, string cookies)
            {
                if (new Uri(domain).Host == _currentDomain?.Host)
                {
                    _cookies = cookies;
                }
            }

            public void ApplyCookieEntries(Uri domainUri, Uri targetUri, IEnumerable<string> cookieEntries)
            {
                List<string> entries = (cookieEntries ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Where(value => value.Contains("="))
                    .ToList();
                if (entries.Count == 0)
                {
                    return;
                }

                ApplyCookieEntriesToUri(domainUri, entries);
                ApplyCookieEntriesToUri(targetUri, entries);
            }

            public void ClearAllCookies(IEnumerable<Uri> candidateUris)
            {
                InternetSetOption(IntPtr.Zero, InternetOptionEndBrowserSession, IntPtr.Zero, 0);

                var cleanupUrls = CollectRelevantCleanupUrls(candidateUris);
                var cookieNames = new HashSet<string>(_appliedCookieNames, StringComparer.OrdinalIgnoreCase);
                foreach (string url in cleanupUrls)
                {
                    foreach (string cookieName in ReadCookieNamesForUrl(url))
                    {
                        cookieNames.Add(cookieName);
                    }
                }

                foreach (string url in cleanupUrls)
                {
                    foreach (string cookieName in cookieNames)
                    {
                        try
                        {
                            ExpireCookieAcrossRelevantScopes(url, cookieName);
                        }
                        catch
                        {
                        }
                    }
                }

                _appliedUrls.Clear();
                _appliedCookieNames.Clear();
                _cookies = string.Empty;
            }

            private void ApplyCookieEntriesToUri(Uri uri, IEnumerable<string> cookieEntries)
            {
                if (uri == null)
                {
                    return;
                }

                string absoluteUrl = uri.AbsoluteUri;
                foreach (string cookieEntry in cookieEntries)
                {
                    int equalsIndex = cookieEntry.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        continue;
                    }

                    string cookieName = cookieEntry.Substring(0, equalsIndex).Trim();
                    if (string.IsNullOrWhiteSpace(cookieName))
                    {
                        continue;
                    }

                    InternetSetCookie(absoluteUrl, null, cookieEntry + ";path=/");
                    _appliedUrls.Add(absoluteUrl);
                    _appliedCookieNames.Add(cookieName);
                }
            }

            private HashSet<string> CollectRelevantCleanupUrls(IEnumerable<Uri> candidateUris)
            {
                var cleanupUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string knownUrl in KnownRelevantUrls)
                {
                    if (IsRelevantCookieUrl(knownUrl))
                    {
                        cleanupUrls.Add(knownUrl);
                    }
                }

                foreach (string appliedUrl in _appliedUrls)
                {
                    if (!Uri.TryCreate(appliedUrl, UriKind.Absolute, out Uri parsedAppliedUri))
                    {
                        continue;
                    }

                    foreach (string candidate in BuildRelevantUrlVariants(parsedAppliedUri))
                    {
                        cleanupUrls.Add(candidate);
                    }
                }

                if (_currentDomain != null)
                {
                    foreach (string candidate in BuildRelevantUrlVariants(_currentDomain))
                    {
                        cleanupUrls.Add(candidate);
                    }
                }

                foreach (Uri candidateUri in candidateUris ?? Enumerable.Empty<Uri>())
                {
                    foreach (string candidate in BuildRelevantUrlVariants(candidateUri))
                    {
                        cleanupUrls.Add(candidate);
                    }
                }

                return cleanupUrls;
            }

            private static IEnumerable<string> BuildRelevantUrlVariants(Uri uri)
            {
                if (uri == null)
                {
                    yield break;
                }

                if (IsRelevantCookieUrl(uri.AbsoluteUri))
                {
                    yield return uri.AbsoluteUri;
                }

                string authority = uri.GetLeftPart(UriPartial.Authority);
                foreach (string suffix in new[]
                {
                    "/",
                    "/index.php",
                    "/pvz/index.php/default/main"
                })
                {
                    string candidate = authority + suffix;
                    if (IsRelevantCookieUrl(candidate))
                    {
                        yield return candidate;
                    }
                }

                if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    string httpsAuthority = "https://" + uri.Authority;
                    foreach (string suffix in new[]
                    {
                        "/",
                        "/index.php",
                        "/pvz/index.php/default/main"
                    })
                    {
                        string candidate = httpsAuthority + suffix;
                        if (IsRelevantCookieUrl(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
            }

            private static bool IsRelevantCookieUrl(string url)
            {
                string lower = (url ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(lower))
                {
                    return false;
                }

                return RelevantUrlKeywords.Any(keyword => lower.Contains(keyword));
            }

            private static IEnumerable<string> ReadCookieNamesForUrl(string url)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    yield break;
                }

                int size = 8192;
                var builder = new StringBuilder(size);
                bool ok = Browser.InternetGetCookieEx(url, null, builder, ref size, Browser.InternetCookieHttpOnly, IntPtr.Zero);
                if (!ok && size > 0)
                {
                    builder = new StringBuilder(size);
                    ok = Browser.InternetGetCookieEx(url, null, builder, ref size, Browser.InternetCookieHttpOnly, IntPtr.Zero);
                }

                if (!ok)
                {
                    yield break;
                }

                foreach (string segment in builder.ToString().Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = (segment ?? string.Empty).Trim();
                    int equalsIndex = trimmed.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        continue;
                    }

                    string name = trimmed.Substring(0, equalsIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        yield return name;
                    }
                }
            }

            private static void ExpireCookieAcrossRelevantScopes(string url, string cookieName)
            {
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(cookieName))
                {
                    return;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri targetUri))
                {
                    return;
                }

                foreach (string domain in BuildRelevantCookieDomains(targetUri.Host))
                {
                    foreach (string path in RelevantCookiePaths)
                    {
                        try
                        {
                            string cookieData = string.IsNullOrWhiteSpace(domain)
                                ? $"{cookieName}=deleted;expires=Thu, 01 Jan 1970 00:00:00 GMT;path={path}"
                                : $"{cookieName}=deleted;expires=Thu, 01 Jan 1970 00:00:00 GMT;path={path};domain={domain}";
                            InternetSetCookieEx(url, null, cookieData, Browser.InternetCookieHttpOnly, IntPtr.Zero);
                            InternetSetCookie(url, null, cookieData);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            private static IEnumerable<string> BuildRelevantCookieDomains(string host)
            {
                var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    null,
                    string.Empty
                };

                string normalizedHost = (host ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalizedHost))
                {
                    return domains;
                }

                domains.Add(normalizedHost);
                domains.Add("." + normalizedHost);

                string[] parts = normalizedHost.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    string suffix = string.Join(".", parts.Skip(i));
                    if (!IsRelevantCookieUrl(suffix))
                    {
                        continue;
                    }

                    domains.Add(suffix);
                    domains.Add("." + suffix);
                }

                return domains;
            }
        }

        private sealed class ProxyManager
        {
            private const int InternetOptionProxy = 38;
            private const int InternetOptionSettingsChanged = 39;
            private const int InternetOptionRefresh = 37;
            private const int InternetOpenTypeDirect = 1;
            private const int InternetOpenTypeProxy = 3;

            [DllImport("wininet.dll", SetLastError = true)]
            private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int lpdwBufferLength);

            private struct InternetProxyInfo
            {
                public int dwAccessType;
                public IntPtr lpszProxy;
                public IntPtr lpszProxyBypass;
            }

            public SystemProxySnapshot CaptureCurrentProxy()
            {
                using (var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    return new SystemProxySnapshot
                    {
                        Enabled = regKey?.GetValue("ProxyEnable")?.ToString() == "1",
                        ProxyServer = regKey?.GetValue("ProxyServer")?.ToString() ?? string.Empty,
                        ProxyBypass = regKey?.GetValue("ProxyOverride")?.ToString() ?? "local"
                    };
                }
            }

            public void SetProxyFromLocalPort(int port)
            {
                ApplyProxy($"127.0.0.1:{port}", "local");
            }

            public void RestoreProxy(SystemProxySnapshot snapshot)
            {
                if (snapshot == null)
                {
                    DisableProxy();
                    return;
                }

                if (snapshot.Enabled && !string.IsNullOrWhiteSpace(snapshot.ProxyServer))
                {
                    ApplyProxy(snapshot.ProxyServer, string.IsNullOrWhiteSpace(snapshot.ProxyBypass) ? "local" : snapshot.ProxyBypass);
                }
                else
                {
                    DisableProxy();
                }
            }

            private void ApplyProxy(string proxy, string bypass)
            {
                InternetProxyInfo info = new InternetProxyInfo
                {
                    dwAccessType = InternetOpenTypeProxy,
                    lpszProxy = Marshal.StringToHGlobalAnsi(proxy),
                    lpszProxyBypass = Marshal.StringToHGlobalAnsi(string.IsNullOrWhiteSpace(bypass) ? "local" : bypass)
                };

                IntPtr buffer = Marshal.AllocCoTaskMem(Marshal.SizeOf(info));
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    InternetSetOption(IntPtr.Zero, InternetOptionProxy, buffer, Marshal.SizeOf(info));
                    InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
                    InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                    if (info.lpszProxy != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(info.lpszProxy);
                    }
                    if (info.lpszProxyBypass != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(info.lpszProxyBypass);
                    }
                }
            }

            private void DisableProxy()
            {
                InternetProxyInfo info = new InternetProxyInfo
                {
                    dwAccessType = InternetOpenTypeDirect,
                    lpszProxy = IntPtr.Zero,
                    lpszProxyBypass = IntPtr.Zero
                };

                IntPtr buffer = Marshal.AllocCoTaskMem(Marshal.SizeOf(info));
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    InternetSetOption(IntPtr.Zero, InternetOptionProxy, buffer, Marshal.SizeOf(info));
                    InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
                    InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
        }
    }

    public class SystemProxySnapshot
    {
        public bool Enabled { get; set; }
        public string ProxyServer { get; set; }
        public string ProxyBypass { get; set; }
    }

    public static class ControlExtensions
    {
        public static void InvokeIfRequired(this Control control, Action action)
        {
            if (control.InvokeRequired)
            {
                control.Invoke(action);
            }
            else
            {
                action();
            }
        }
    }

    public sealed class CookieDisplayEntry
    {
        public CookieDisplayEntry(string filePath, IReadOnlyList<string> groupedFilePaths, string displayName)
        {
            FilePath = filePath;
            GroupedFilePaths = groupedFilePaths ?? Array.Empty<string>();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未知用户" : displayName;
        }

        public string FilePath { get; }

        public IReadOnlyList<string> GroupedFilePaths { get; }

        public string DisplayName { get; }
    }

    public class CookieSelectionForm : Form
    {
        private readonly Browser _browser;
        private readonly FlowLayoutPanel _cookiePanel;
        private readonly Label _emptyLabel;
        private readonly Panel _dropOverlay;

        public CookieSelectionForm(Browser browser)
        {
            _browser = browser;

            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.White;
            ClientSize = new Size(360, 460);
            ControlBox = true;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MaximizeBox = false;
            MinimizeBox = false;
            MinimumSize = new Size(320, 360);
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Cookie 设置";
            AllowDrop = true;

            var rootLayout = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(rootLayout);

            _cookiePanel = new FlowLayoutPanel
            {
                AutoScroll = true,
                BackColor = Color.White,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Margin = Padding.Empty,
                Padding = new Padding(14),
                WrapContents = false
            };
            rootLayout.Controls.Add(_cookiePanel, 0, 1);

            var titlePanel = new Panel
            {
                BackColor = Color.FromArgb(247, 250, 252),
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 12, 16, 12)
            };
            rootLayout.Controls.Add(titlePanel, 0, 0);

            var titleLayout = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            titleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            titlePanel.Controls.Add(titleLayout);

            var titleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                Text = "选择要应用的 Cookie",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            titleLayout.Controls.Add(titleLabel, 0, 0);

            var hintLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Color.FromArgb(107, 114, 128),
                Text = "自动读取 cookies 文件夹，可拖入 XML 或 50MB 内 ZIP 导入",
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty
            };
            titleLayout.Controls.Add(hintLabel, 0, 1);

            _emptyLabel = new Label
            {
                Dock = DockStyle.Top,
                Font = new Font("Microsoft YaHei UI", 10F),
                ForeColor = Color.FromArgb(107, 114, 128),
                Height = 80,
                Text = "cookies 文件夹里还没有 XML 文件",
                TextAlign = ContentAlignment.MiddleCenter
            };

            _dropOverlay = new Panel
            {
                BackColor = Color.FromArgb(225, 239, 254),
                Dock = DockStyle.Fill,
                Visible = false
            };
            _dropOverlay.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 64, 175),
                Text = "松开导入 Cookie XML / ZIP",
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(_dropOverlay);
            _dropOverlay.BringToFront();

            EnableDropTarget(this);
        }

        public void SetCookieFiles(IEnumerable<CookieDisplayEntry> files)
        {
            _cookiePanel.SuspendLayout();
            _cookiePanel.Controls.Clear();

            List<CookieDisplayEntry> fileList = files?.ToList() ?? new List<CookieDisplayEntry>();
            if (fileList.Count == 0)
            {
                _cookiePanel.Controls.Add(_emptyLabel);
            }
            else
            {
                foreach (CookieDisplayEntry file in fileList)
                {
                    _cookiePanel.Controls.Add(new CookieItem(file, _browser)
                    {
                        Margin = new Padding(0, 0, 0, 10)
                    });
                }
            }

            _cookiePanel.ResumeLayout();
        }

        internal void SetDropOverlayVisible(bool visible)
        {
            _dropOverlay.Visible = visible;
            if (visible)
            {
                _dropOverlay.BringToFront();
            }
        }

        private void EnableDropTarget(Control control)
        {
            if (control == null)
            {
                return;
            }

            control.AllowDrop = true;
            control.DragEnter += DropTarget_DragEnter;
            control.DragOver += DropTarget_DragOver;
            control.DragLeave += DropTarget_DragLeave;
            control.DragDrop += DropTarget_DragDrop;

            foreach (Control child in control.Controls)
            {
                EnableDropTarget(child);
            }
        }

        private void DropTarget_DragEnter(object sender, DragEventArgs e)
        {
            UpdateDropState(e);
        }

        private void DropTarget_DragOver(object sender, DragEventArgs e)
        {
            UpdateDropState(e);
        }

        private void DropTarget_DragLeave(object sender, EventArgs e)
        {
            if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position))
            {
                SetDropOverlayVisible(false);
            }
        }

        private void DropTarget_DragDrop(object sender, DragEventArgs e)
        {
            SetDropOverlayVisible(false);
            if (!_browser.TryGetDroppedCookieXmlFiles(e.Data, out string[] filePaths))
            {
                return;
            }

            _browser.ImportCookieProfileFiles(filePaths);
        }

        private void UpdateDropState(DragEventArgs e)
        {
            bool canImport = _browser.TryGetDroppedCookieXmlFiles(e.Data, out _);
            e.Effect = canImport ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropOverlayVisible(canImport);
        }
    }

    public class CookieItem : UserControl
    {
        private readonly Browser _mainForm;

        public string FilePath { get; }

        private readonly CookieDisplayEntry _entry;

        public CookieItem(CookieDisplayEntry entry, Browser mainForm)
        {
            _entry = entry;
            FilePath = entry?.FilePath;
            _mainForm = mainForm;
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Size = new Size(300, 88);

            var lblUserId = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                Location = new Point(14, 14),
                Text = _entry?.DisplayName ?? ParseUserId()
            };

            var lblFileName = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Color.FromArgb(107, 114, 128),
                Location = new Point(14, 44),
                Text = (_entry?.GroupedFilePaths?.Count ?? 0) > 1
                    ? "相同 Cookie " + _entry.GroupedFilePaths.Count + " 份"
                    : Path.GetFileName(FilePath)
            };

            var btnApply = new Button
            {
                BackColor = Color.FromArgb(37, 99, 235),
                Cursor = Cursors.Hand,
                FlatAppearance =
                {
                    BorderSize = 0,
                    MouseDownBackColor = Color.FromArgb(29, 78, 216),
                    MouseOverBackColor = Color.FromArgb(59, 130, 246)
                },
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(214, 24),
                Size = new Size(72, 34),
                Text = "应用",
                UseVisualStyleBackColor = false
            };
            btnApply.Click += (s, e) => ApplyCookie();

            Controls.AddRange(new Control[] { lblUserId, lblFileName, btnApply });
        }

        private string ParseUserId()
        {
            CookieProfileManager.CookieProfile profile = new CookieProfileManager(AppDomain.CurrentDomain.BaseDirectory).LoadProfile(FilePath);
            if (profile != null)
            {
                if (!string.IsNullOrWhiteSpace(profile.UserName))
                {
                    return profile.UserName;
                }

                return "用户 " + profile.UserId;
            }

            return "未知用户";
        }

        private void ApplyCookie()
        {
            try
            {
                _mainForm.ApplyCookieProfileFile(FilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析 Cookie 文件失败：{ex.Message}");
            }
        }
    }
}
