using System;
using System.Drawing;
using System.Windows.Forms;

namespace WebBrowserApp
{
    public partial class ProxySettingsUserControl : Form
    {
        public event EventHandler SettingsSaved;
        public event EventHandler SettingsCanceled;

        private ProxySettings _currentSettings;
        private readonly SystemProxySnapshot _originalProxySnapshot;

        public ProxySettings CurrentSettings
        {
            get => _currentSettings;
            set
            {
                _currentSettings = value ?? new ProxySettings();
                HydrateLegacyCustomProxy(_currentSettings);
                UpdateControls();
            }
        }

        public ProxySettingsUserControl()
            : this(new ProxySettings(), null)
        {
        }

        public ProxySettingsUserControl(ProxySettings settings)
            : this(settings, null)
        {
        }

        public ProxySettingsUserControl(ProxySettings settings, SystemProxySnapshot originalProxySnapshot)
        {
            _originalProxySnapshot = originalProxySnapshot;
            InitializeComponent();
            CurrentSettings = settings ?? new ProxySettings();
        }

        private void UpdateControls()
        {
            bool useCustom = _currentSettings.UseCustomProxy;
            bool useSystem = !useCustom && _currentSettings.UseSystemProxy;

            rbDirect.Checked = !useCustom && !useSystem;
            rbSystemProxy.Checked = useSystem;
            rbCustomProxy.Checked = useCustom;
            cmbProxyScheme.SelectedItem = string.IsNullOrWhiteSpace(_currentSettings.CustomProxyScheme)
                ? "http"
                : _currentSettings.CustomProxyScheme;
            txtProxyHost.Text = string.IsNullOrWhiteSpace(_currentSettings.CustomProxyHost)
                ? "127.0.0.1"
                : _currentSettings.CustomProxyHost;
            txtProxyPort.Text = string.IsNullOrWhiteSpace(_currentSettings.CustomProxyPort)
                ? string.Empty
                : _currentSettings.CustomProxyPort;

            UpdateProxyModeUi();
            UpdateSystemProxySummary();
        }

        private void UpdateProxyModeUi()
        {
            cmbProxyScheme.Enabled = rbCustomProxy.Checked;
            txtProxyHost.Enabled = rbCustomProxy.Checked;
            txtProxyPort.Enabled = rbCustomProxy.Checked;
            lblCustomProxyHint.Enabled = rbCustomProxy.Checked;

            if (rbDirect.Checked)
            {
                lblModeHint.Text = "当前模式：不使用上游代理，失败时直接访问目标地址。";
            }
            else if (rbSystemProxy.Checked)
            {
                lblModeHint.Text = "当前模式：沿用系统原始代理作为上游代理。";
            }
            else
            {
                lblModeHint.Text = "当前模式：使用你指定的上游代理地址。";
            }
        }

        private void UpdateSystemProxySummary()
        {
            if (_originalProxySnapshot == null)
            {
                lblSystemProxyValue.Text = "未能读取系统代理快照";
                return;
            }

            if (!_originalProxySnapshot.Enabled || string.IsNullOrWhiteSpace(_originalProxySnapshot.ProxyServer))
            {
                lblSystemProxyValue.Text = "系统当前未启用代理";
                return;
            }

            string bypass = string.IsNullOrWhiteSpace(_originalProxySnapshot.ProxyBypass)
                ? "local"
                : _originalProxySnapshot.ProxyBypass;
            lblSystemProxyValue.Text = $"{_originalProxySnapshot.ProxyServer}  |  bypass={bypass}";
        }

        private void ProxyMode_CheckedChanged(object sender, EventArgs e)
        {
            UpdateProxyModeUi();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            string proxyScheme = (cmbProxyScheme.SelectedItem?.ToString() ?? "http").Trim();
            string proxyHost = txtProxyHost.Text.Trim();
            string proxyPort = txtProxyPort.Text.Trim();
            if (rbCustomProxy.Checked && !TryBuildCustomProxy(proxyHost, proxyPort, out string normalizedProxy, out string errorMessage))
            {
                MessageBox.Show(this, errorMessage, "代理设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (string.IsNullOrWhiteSpace(proxyHost))
                {
                    txtProxyHost.Focus();
                    txtProxyHost.SelectAll();
                }
                else
                {
                    txtProxyPort.Focus();
                    txtProxyPort.SelectAll();
                }
                return;
            }

            var newSettings = new ProxySettings
            {
                UseSystemProxy = rbSystemProxy.Checked,
                UseCustomProxy = rbCustomProxy.Checked,
                CustomProxyScheme = proxyScheme,
                CustomProxyHost = proxyHost,
                CustomProxyPort = proxyPort
            };

            bool changed = !_currentSettings.Equals(newSettings);
            _currentSettings = newSettings;
            SettingsSaved?.Invoke(this, EventArgs.Empty);

            if (!changed)
            {
                Close();
                return;
            }

            Close();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            SettingsCanceled?.Invoke(this, EventArgs.Empty);
            Close();
        }

        private static bool TryBuildCustomProxy(string host, string portValue, out string normalizedProxy, out string errorMessage)
        {
            normalizedProxy = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(host))
            {
                errorMessage = "请输入代理 IP 或主机名。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(portValue))
            {
                errorMessage = "请输入代理端口。";
                return false;
            }

            if (!int.TryParse(portValue, out int port) || port < 1 || port > 65535)
            {
                errorMessage = "代理端口必须在 1 到 65535 之间。";
                return false;
            }

            normalizedProxy = $"{host}:{port}";
            return true;
        }

        private static void HydrateLegacyCustomProxy(ProxySettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.CustomProxyScheme = string.IsNullOrWhiteSpace(settings.CustomProxyScheme)
                ? "http"
                : settings.CustomProxyScheme;
            settings.CustomProxyHost = string.IsNullOrWhiteSpace(settings.CustomProxyHost)
                ? "127.0.0.1"
                : settings.CustomProxyHost;

            if (!string.IsNullOrWhiteSpace(settings.CustomProxyPort))
            {
                return;
            }

            string legacyValue = settings.CustomProxy;
            if (string.IsNullOrWhiteSpace(legacyValue))
            {
                settings.CustomProxyPort = "8888";
                return;
            }

            string withoutScheme = legacyValue.Trim();
            int schemeIndex = withoutScheme.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
            {
                settings.CustomProxyScheme = withoutScheme.Substring(0, schemeIndex);
                withoutScheme = withoutScheme.Substring(schemeIndex + 3);
            }

            int lastColon = withoutScheme.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == withoutScheme.Length - 1)
            {
                settings.CustomProxyHost = withoutScheme;
                settings.CustomProxyPort = "8888";
                return;
            }

            settings.CustomProxyHost = withoutScheme.Substring(0, lastColon);
            settings.CustomProxyPort = withoutScheme.Substring(lastColon + 1);
        }
    }

    public class ProxySettings
    {
        public bool UseSystemProxy { get; set; } = true;
        public bool UseCustomProxy { get; set; }
        public string CustomProxyScheme { get; set; } = "http";
        public string CustomProxyHost { get; set; } = "127.0.0.1";
        public string CustomProxyPort { get; set; } = "8888";

        public string CustomProxy
        {
            get
            {
                if (string.IsNullOrWhiteSpace(CustomProxyHost) || string.IsNullOrWhiteSpace(CustomProxyPort))
                {
                    return string.Empty;
                }

                return $"{CustomProxyHost.Trim()}:{CustomProxyPort.Trim()}";
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    CustomProxyHost = "127.0.0.1";
                    CustomProxyPort = "8888";
                    return;
                }

                string proxyValue = value.Trim();
                int schemeIndex = proxyValue.IndexOf("://", StringComparison.Ordinal);
                if (schemeIndex >= 0)
                {
                    CustomProxyScheme = proxyValue.Substring(0, schemeIndex);
                    proxyValue = proxyValue.Substring(schemeIndex + 3);
                }

                int lastColon = proxyValue.LastIndexOf(':');
                if (lastColon > 0 && lastColon < proxyValue.Length - 1)
                {
                    CustomProxyHost = proxyValue.Substring(0, lastColon);
                    CustomProxyPort = proxyValue.Substring(lastColon + 1);
                }
                else
                {
                    CustomProxyHost = proxyValue;
                }
            }
        }

        public override bool Equals(object obj)
        {
            return obj is ProxySettings settings &&
                   UseSystemProxy == settings.UseSystemProxy &&
                   UseCustomProxy == settings.UseCustomProxy &&
                   string.Equals(CustomProxyScheme ?? string.Empty, settings.CustomProxyScheme ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(CustomProxyHost ?? string.Empty, settings.CustomProxyHost ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(CustomProxyPort ?? string.Empty, settings.CustomProxyPort ?? string.Empty, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + UseSystemProxy.GetHashCode();
                hash = (hash * 23) + UseCustomProxy.GetHashCode();
                hash = (hash * 23) + (CustomProxyScheme ?? string.Empty).ToLowerInvariant().GetHashCode();
                hash = (hash * 23) + (CustomProxyHost ?? string.Empty).ToLowerInvariant().GetHashCode();
                hash = (hash * 23) + (CustomProxyPort ?? string.Empty).GetHashCode();
                return hash;
            }
        }
    }
}
