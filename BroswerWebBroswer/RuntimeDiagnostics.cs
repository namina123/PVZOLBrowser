using System;
using System.Configuration;
using System.Globalization;
using System.IO;

namespace WebBrowserApp
{
    internal static class RuntimeDiagnostics
    {
        private static readonly object SyncRoot = new object();
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser_runtime.log");
        private static readonly string RotatedLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser_runtime.prev.log");
        private static readonly bool VerboseEnabled = ReadVerboseEnabled();
        private static readonly long MaxLogBytes = ReadMaxLogBytes();

        internal static void Write(string category, string message)
        {
            try
            {
                if (!ShouldWrite(category ?? string.Empty, message ?? string.Empty))
                {
                    return;
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {message}{Environment.NewLine}";
                lock (SyncRoot)
                {
                    RotateIfNeeded(line.Length * sizeof(char));
                    File.AppendAllText(LogPath, line);
                }
            }
            catch
            {
            }
        }

        private static bool ShouldWrite(string category, string message)
        {
            if (VerboseEnabled)
            {
                return true;
            }

            switch ((category ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "backend":
                case "cookie":
                case "ruffle":
                case "ruffle-amf":
                case "ruffle-cookie":
                case "ruffle-nav":
                case "ruffle-player":
                    return true;
                case "ruffle-proxy":
                    return ContainsAny(message, "invalid", "web exception", "context error", "write skipped");
                case "ruffle-upstream":
                    return ContainsAny(message, "status=4", "status=5", "inject bootstrap", "invalid upstream proxy");
                case "ruffle-localmap":
                case "ruffle-asset":
                    return false;
                default:
                    return true;
            }
        }

        private static bool ContainsAny(string message, params string[] fragments)
        {
            string value = message ?? string.Empty;
            foreach (string fragment in fragments)
            {
                if (value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RotateIfNeeded(int pendingBytes)
        {
            try
            {
                if (MaxLogBytes <= 0)
                {
                    return;
                }

                long currentBytes = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                if (currentBytes + pendingBytes <= MaxLogBytes)
                {
                    return;
                }

                if (File.Exists(RotatedLogPath))
                {
                    File.Delete(RotatedLogPath);
                }

                if (File.Exists(LogPath))
                {
                    File.Move(LogPath, RotatedLogPath);
                }
            }
            catch
            {
            }
        }

        private static bool ReadVerboseEnabled()
        {
            string value = Environment.GetEnvironmentVariable("PVZOL_VERBOSE_RUNTIME_LOG")
                ?? ConfigurationManager.AppSettings["VerboseRuntimeLog"]
                ?? "false";
            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static long ReadMaxLogBytes()
        {
            string value = Environment.GetEnvironmentVariable("PVZOL_RUNTIME_LOG_MAX_BYTES")
                ?? ConfigurationManager.AppSettings["RuntimeLogMaxBytes"]
                ?? "1048576";
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
            {
                return parsed;
            }

            return 1048576;
        }
    }
}
