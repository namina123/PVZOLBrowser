using System;
using System.Runtime.InteropServices;

namespace WebBrowserApp
{
    internal static class FlashRuntimeDetector
    {
        internal static FlashRuntimeInfo Detect()
        {
            var info = new FlashRuntimeInfo();

            try
            {
                Type flashType = Type.GetTypeFromProgID("ShockwaveFlash.ShockwaveFlash", false);
                if (flashType == null)
                {
                    info.IsAvailable = false;
                    info.Version = "未注册";
                    info.Diagnostic = "COM ProgID 未注册";
                    return info;
                }

                object instance = Activator.CreateInstance(flashType);
                if (instance == null)
                {
                    info.IsAvailable = false;
                    info.Version = "无法创建";
                    info.Diagnostic = "ActiveX 实例创建失败";
                    return info;
                }

                try
                {
                    object result = flashType.InvokeMember(
                        "GetVariable",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        instance,
                        new object[] { "$version" });

                    info.IsAvailable = true;
                    info.Version = Convert.ToString(result) ?? "未知版本";
                    info.Diagnostic = "已检测到本机 Flash ActiveX";
                    return info;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(instance);
                }
            }
            catch (Exception ex)
            {
                info.IsAvailable = false;
                info.Version = $"不可用: {ex.GetType().Name}";
                info.Diagnostic = ex.Message;
                return info;
            }
        }
    }
}
