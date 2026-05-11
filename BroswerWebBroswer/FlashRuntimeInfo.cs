namespace WebBrowserApp
{
    internal sealed class FlashRuntimeInfo
    {
        internal bool IsAvailable { get; set; }

        internal string Version { get; set; } = "未知";

        internal string Diagnostic { get; set; } = string.Empty;
    }
}
