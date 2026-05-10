using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WebBrowserApp
{
    internal static class FlashProxyNative
    {
        private static bool _loaded;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern IntPtr flash_proxy_create();

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void flash_proxy_destroy(IntPtr handle);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int flash_proxy_set_cache_root(IntPtr handle, string path);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_proxy_clear_mapping_hosts(IntPtr handle);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int flash_proxy_add_mapping_host(IntPtr handle, string host);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_proxy_clear_mapping_url_keywords(IntPtr handle);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int flash_proxy_add_mapping_url_keyword(IntPtr handle, string value);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int flash_proxy_set_upstream_proxy(IntPtr handle, string proxy);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_proxy_start(IntPtr handle, int preferredPort, out int actualPort);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void flash_proxy_stop(IntPtr handle);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int flash_proxy_get_last_error(IntPtr handle, StringBuilder buffer, int bufferSize);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void flash_proxy_free_memory(IntPtr ptr);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_amf_encode_packet_json(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string packetJson,
            out IntPtr outData,
            out int outSize);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_amf_decode_packet_json(
            IntPtr data,
            int dataSize,
            out IntPtr outJson);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_amf_post_json(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string packetJson,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string headersJson,
            out IntPtr outResponseJson);

        [DllImport("flash_proxy_core.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int flash_amf_post_pvzol_json(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string target,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string bodyJson,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string cookie,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string referer,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string extraHeadersJson,
            out IntPtr outResponseJson);

        internal static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string[] candidates =
            {
                Path.Combine(baseDir, "flash_proxy_core.dll"),
                Path.Combine(baseDir, "native", arch, "Release", "flash_proxy_core.dll"),
                Path.Combine(baseDir, "native", arch, "Debug", "flash_proxy_core.dll"),
                Path.Combine(baseDir, "native", arch, "flash_proxy_core.dll"),
                Path.Combine(baseDir, "..", "..", "native", arch, "Release", "flash_proxy_core.dll"),
                Path.Combine(baseDir, "..", "..", "native", arch, "Debug", "flash_proxy_core.dll"),
                Path.Combine(baseDir, "..", "..", "native", arch, "flash_proxy_core.dll")
            };

            foreach (string candidate in candidates)
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                if (LoadLibrary(fullPath) != IntPtr.Zero)
                {
                    _loaded = true;
                    return;
                }
            }

            throw new DllNotFoundException("flash_proxy_core.dll 未找到，请先构建 NativeFlashProxy 并放入输出目录。");
        }

        internal static string GetLastError(IntPtr handle)
        {
            var buffer = new StringBuilder(512);
            flash_proxy_get_last_error(handle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        internal static string ConsumeUtf8String(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                int length = 0;
                while (Marshal.ReadByte(ptr, length) != 0)
                {
                    length++;
                }

                byte[] buffer = new byte[length];
                Marshal.Copy(ptr, buffer, 0, length);
                return Encoding.UTF8.GetString(buffer);
            }
            finally
            {
                flash_proxy_free_memory(ptr);
            }
        }

        internal static byte[] ConsumeBytes(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero || size <= 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                byte[] buffer = new byte[size];
                Marshal.Copy(ptr, buffer, 0, size);
                return buffer;
            }
            finally
            {
                flash_proxy_free_memory(ptr);
            }
        }
    }
}
