using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using BroswerWebBroswer.Properties;

namespace WebBrowserApp
{
    public partial class Browser : Form
    {
        private const string DefaultHome = "http://pvzol.org/pvz/index.php/default/main";
        private static readonly string[] MappingHosts =
        {
            "pvzol.org",
            "youkia.pvz",
            "pvz.youkia",
            "youkia.com"
        };
        private static readonly string[] MappingUrlKeywords =
        {
            "/pvz/",
            "/youkia/",
            "youkia.pvz",
            "pvz.youkia",
            ".youkia.com"
        };

        private readonly CookieManager _cookieManager;
        private readonly ProxyManager _proxyManager;
        private readonly List<string> _cookieFiles = new List<string>();

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

        public Browser()
        {
            InitializeComponent();

            _cookieManager = new CookieManager();
            _proxyManager = new ProxyManager();
            _originalProxySnapshot = _proxyManager.CaptureCurrentProxy();
            _legacyDirectMode = BrowserBackendSelector.IsLegacyWindowsOnly();

            InitializeCookieLibrary();
            if (!_legacyDirectMode)
            {
                InitializeProxySystem();
            }
            InitializeBrowserSettings();

            Shown += Browser_Shown;
            FormClosing += Browser_FormClosing;
        }

        private void Browser_Shown(object sender, EventArgs e)
        {
            if (_cookieSelectionForm == null || _cookieSelectionForm.IsDisposed)
            {
                BtnCookieTool_Click(btnCookieTool, EventArgs.Empty);
            }
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
                _ruffleProxy.ConfigureLocalMapping(
                    Path.Combine(Application.StartupPath, "cache"),
                    MappingHosts,
                    MappingUrlKeywords);
                RuntimeDiagnostics.Write("ruffle", "webview request handler ready");
            }

            if (_ruffleHost == null)
            {
                _ruffleHost = CreateRuffleHost();
                _ruffleHost.SourceChanged += RuffleHost_SourceChanged;
                _ruffleHost.NavigationCompleted += RuffleHost_NavigationCompleted;
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
            Type hostType = typeof(Browser).Assembly.GetType("WebBrowserApp.RuffleWebViewHost", throwOnError: false);
            if (hostType == null)
            {
                throw new InvalidOperationException("未找到 Ruffle WebView2 宿主类型。");
            }

            object instance = Activator.CreateInstance(hostType, pnlBrowserHost, _ruffleProxy);
            if (!(instance is IRuffleBrowserHost host))
            {
                throw new InvalidOperationException("Ruffle WebView2 宿主初始化失败。");
            }

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
            string cookieDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cookies");
            if (!Directory.Exists(cookieDirectory))
            {
                try
                {
                    Directory.CreateDirectory(cookieDirectory);
                }
                catch
                {
                }
            }

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
                string cookieDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cookies");
                _cookieFiles.Clear();

                if (Directory.Exists(cookieDirectory))
                {
                    _cookieFiles.AddRange(Directory.GetFiles(cookieDirectory, "*.xml").OrderBy(Path.GetFileName));
                }

                if (_cookieSelectionForm != null && !_cookieSelectionForm.IsDisposed)
                {
                    _cookieSelectionForm.SetCookieFiles(_cookieFiles);
                }
            });
        }

        public void ApplyCookiesAndRedirect(string cookieString, string domain, string redirectUrl)
        {
            if (string.IsNullOrWhiteSpace(cookieString) || string.IsNullOrWhiteSpace(domain))
            {
                return;
            }

            foreach (string cookie in cookieString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = cookie.Trim();
                if (!trimmed.Contains("="))
                {
                    continue;
                }

                SetCurrentCookie(domain, trimmed);
            }

            Uri uri = new Uri(domain);
            if (_browserMode == BrowserBackendMode.RuffleWebView2)
            {
                ClearRuffleCookies();
                ApplyRuffleCookies(uri, cookieString);
                _ruffleProxy?.SetCookieHeader(uri, cookieString);
                RuntimeDiagnostics.Write("cookie", $"apply via ruffle proxy domain={domain} redirect={redirectUrl}");
            }
            else
            {
                _cookieManager.SetCookies(uri);
                RuntimeDiagnostics.Write("cookie", $"apply via IE cookie store domain={domain} redirect={redirectUrl}");
            }

            NavigateToAddress(redirectUrl);
            txtUrl.Text = redirectUrl;
            UpdateStatus($"已应用 Cookie 并跳转到 {redirectUrl}");
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
            _cookieSelectionForm.SetCookieFiles(_cookieFiles);

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

        private void Browser_FormClosing(object sender, FormClosingEventArgs e)
        {
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
                Control browserSurface = GetActiveBrowserSurface();
                Point browserOrigin = browserSurface.PointToScreen(Point.Empty);
                int desiredHeight = Math.Max(360, browserSurface.Height);
                popup.Height = Math.Min(desiredHeight, workingArea.Height - 24);
                if (!TryGetSavedCookiePopupLocation(popup.Size, out Point savedLocation))
                {
                    x = Right;
                    y = Math.Max(workingArea.Top + 12, browserOrigin.Y);
                }
                else
                {
                    x = savedLocation.X;
                    y = savedLocation.Y;
                }
            }

            popup.Location = ClampPopupLocation(new Point(x, y), popup.Size);
            popup.Show(this);
            popup.BringToFront();
        }

        private bool TryGetSavedCookiePopupLocation(Size popupSize, out Point location)
        {
            int savedLeft = Settings.Default.CookiePanelLeft;
            int savedTop = Settings.Default.CookiePanelTop;
            if (savedLeft < 0 || savedTop < 0)
            {
                location = Point.Empty;
                return false;
            }

            location = ClampPopupLocation(new Point(savedLeft, savedTop), popupSize);
            return true;
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

            Settings.Default.CookiePanelLeft = location.X;
            Settings.Default.CookiePanelTop = location.Y;
            Settings.Default.Save();
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
            string cacheRoot = Path.Combine(Application.StartupPath, "cache");
            Directory.CreateDirectory(cacheRoot);

            if (FlashProxyNative.flash_proxy_set_cache_root(_nativeProxy, cacheRoot) == 0)
            {
                throw new InvalidOperationException("设置本地 cache 目录失败。");
            }

            FlashProxyNative.flash_proxy_clear_mapping_hosts(_nativeProxy);
            foreach (string host in MappingHosts)
            {
                FlashProxyNative.flash_proxy_add_mapping_host(_nativeProxy, host);
            }

            FlashProxyNative.flash_proxy_clear_mapping_url_keywords(_nativeProxy);
            foreach (string keyword in MappingUrlKeywords)
            {
                FlashProxyNative.flash_proxy_add_mapping_url_keyword(_nativeProxy, keyword);
            }

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
    function request(node){
        if(!node){return false;}
        var methods=['requestFullscreen','webkitRequestFullscreen','mozRequestFullScreen','msRequestFullscreen'];
        for(var i=0;i<methods.length;i++){
            var fn=node[methods[i]];
            if(typeof fn==='function'){
                try{fn.call(node);return true;}catch(e){}
            }
        }
        return false;
    }
    if(document.fullscreenElement||document.webkitFullscreenElement||document.msFullscreenElement){
        if(document.exitFullscreen){document.exitFullscreen();return 'exit';}
        if(document.webkitExitFullscreen){document.webkitExitFullscreen();return 'exit';}
        if(document.msExitFullscreen){document.msExitFullscreen();return 'exit';}
    }
    var node=document.querySelector('ruffle-player,ruffle-embed,ruffle-object,object,embed');
    return request(node)?'ok':'missing';
})();";
                    string result = await _ruffleHost.ExecuteScriptAsync(script).ConfigureAwait(true);
                    if ((result ?? string.Empty).Contains("ok"))
                    {
                        UpdateStatus("已尝试让页面内 Flash 进入全屏");
                    }
                    else if ((result ?? string.Empty).Contains("exit"))
                    {
                        UpdateStatus("已尝试退出页面内全屏");
                    }
                    else
                    {
                        UpdateStatus("当前页面没有可全屏的 Ruffle/Flash 容器");
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
    function tryRequest(node) {
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

    var nodes = document.querySelectorAll('object, embed');
    for (var i = 0; i < nodes.length; i++) {
        if (tryRequest(nodes[i])) return 'ok';
    }

    if (document.fullscreenElement || document.msFullscreenElement || document.webkitFullscreenElement) {
        if (document.exitFullscreen) { document.exitFullscreen(); return 'exit'; }
        if (document.msExitFullscreen) { document.msExitFullscreen(); return 'exit'; }
        if (document.webkitExitFullscreen) { document.webkitExitFullscreen(); return 'exit'; }
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

        private sealed class CookieManager
        {
            [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern bool InternetSetCookie(string lpszUrlName, string lpszCookieName, string lpszCookieData);

            private Uri _currentDomain;
            private string _cookies = string.Empty;

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

    public class CookieSelectionForm : Form
    {
        private readonly Browser _browser;
        private readonly FlowLayoutPanel _cookiePanel;
        private readonly Label _emptyLabel;

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
                Text = "自动读取程序同目录 cookies 文件夹内的 XML 文件",
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
        }

        public void SetCookieFiles(IEnumerable<string> files)
        {
            _cookiePanel.SuspendLayout();
            _cookiePanel.Controls.Clear();

            List<string> fileList = files?.ToList() ?? new List<string>();
            if (fileList.Count == 0)
            {
                _cookiePanel.Controls.Add(_emptyLabel);
            }
            else
            {
                foreach (string file in fileList)
                {
                    _cookiePanel.Controls.Add(new CookieItem(file, _browser)
                    {
                        Margin = new Padding(0, 0, 0, 10)
                    });
                }
            }

            _cookiePanel.ResumeLayout();
        }
    }

    public class CookieItem : UserControl
    {
        private readonly Browser _mainForm;

        public string FilePath { get; }

        public CookieItem(string filePath, Browser mainForm)
        {
            FilePath = filePath;
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
                Text = ParseUserId()
            };

            var lblFileName = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Color.FromArgb(107, 114, 128),
                Location = new Point(14, 44),
                Text = Path.GetFileName(FilePath)
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
            try
            {
                var doc = XDocument.Load(FilePath);
                return doc.Element("UserSetting")?.Element("UserID")?.Value ?? "未知用户";
            }
            catch
            {
                return "未知用户";
            }
        }

        private void ApplyCookie()
        {
            try
            {
                var doc = XDocument.Load(FilePath);
                string cookies = doc.Element("UserSetting")?.Element("UserCookies")?.Value;
                string userDomain = doc.Element("UserSetting")?.Element("UserDomain")?.Value;

                if (!string.IsNullOrEmpty(userDomain))
                {
                    string redirectUrl = $"{userDomain.TrimEnd('/')}/pvz/index.php/default/main";
                    _mainForm.ApplyCookiesAndRedirect(cookies, userDomain, redirectUrl);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析 Cookie 文件失败：{ex.Message}");
            }
        }
    }
}
