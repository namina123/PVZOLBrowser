namespace WebBrowserApp
{
    internal sealed class BrowserBackendDecision
    {
        internal BrowserBackendMode Mode { get; set; }

        internal string Policy { get; set; } = "auto";

        internal string Reason { get; set; } = string.Empty;

        internal bool WebView2Available { get; set; }
    }
}
