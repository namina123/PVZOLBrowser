using System;
using System.Runtime.InteropServices;

namespace WebBrowserApp
{
    internal static class BrowserBackendSelector
    {
        internal static bool IsLegacyWindowsOnly()
        {
            return !IsModernWindows();
        }

        internal static BrowserBackendDecision Decide(FlashRuntimeInfo flashInfo)
        {
            bool flashAvailable = flashInfo != null && flashInfo.IsAvailable;
            bool modernWindows = IsModernWindows();

            if (!modernWindows)
            {
                return new BrowserBackendDecision
                {
                    Mode = BrowserBackendMode.NativeIe,
                    Policy = flashAvailable ? "flash-first-legacy" : "legacy-flash-only",
                    Reason = flashAvailable
                        ? "当前系统低于 Windows 10，禁用 WebView2/Ruffle，使用 IE/Flash 路线"
                        : "当前系统低于 Windows 10，已禁用 WebView2/Ruffle；请安装 Flash 后使用",
                    WebView2Available = false
                };
            }

            return new BrowserBackendDecision
            {
                Mode = flashAvailable ? BrowserBackendMode.NativeIe : BrowserBackendMode.RuffleWebView2,
                Policy = flashAvailable ? "flash-first" : "ruffle-fallback",
                Reason = flashAvailable
                    ? "检测到本机 Flash，优先使用 IE/Flash 路线"
                    : "未检测到本机 Flash，回退到 Ruffle/WebView2 路线",
                WebView2Available = !flashAvailable
            };
        }

        private static bool IsModernWindows()
        {
            Version version = GetRealWindowsVersion();
            if (version == null)
            {
                return false;
            }

            return version.Major >= 10;
        }

        private static Version GetRealWindowsVersion()
        {
            try
            {
                var versionInfo = new OsVersionInfo();
                versionInfo.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref versionInfo) == 0)
                {
                    return new Version(versionInfo.dwMajorVersion, versionInfo.dwMinorVersion, versionInfo.dwBuildNumber);
                }
            }
            catch
            {
            }

            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    return Environment.OSVersion.Version;
                }
            }
            catch
            {
            }

            return null;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref OsVersionInfo versionInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }
    }
}
