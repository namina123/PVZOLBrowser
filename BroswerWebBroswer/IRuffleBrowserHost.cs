using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebBrowserApp
{
    internal sealed class RuffleSourceChangedEventArgs : EventArgs
    {
        internal RuffleSourceChangedEventArgs(Uri source)
        {
            Source = source;
        }

        internal Uri Source { get; }
    }

    internal sealed class RuffleNavigationCompletedEventArgs : EventArgs
    {
        internal RuffleNavigationCompletedEventArgs(Uri source, bool isSuccess, string webErrorStatus)
        {
            Source = source;
            IsSuccess = isSuccess;
            WebErrorStatus = webErrorStatus ?? string.Empty;
        }

        internal Uri Source { get; }

        internal bool IsSuccess { get; }

        internal string WebErrorStatus { get; }
    }

    internal interface IRuffleBrowserHost : IDisposable
    {
        Control ViewControl { get; }

        bool IsInitialized { get; }

        event EventHandler<RuffleSourceChangedEventArgs> SourceChanged;

        event EventHandler<RuffleNavigationCompletedEventArgs> NavigationCompleted;

        Task InitializeAsync();

        void Navigate(string url);

        void Reload();

        Task<string> ExecuteScriptAsync(string script);

        void ClearCookies();

        void ApplyCookies(Uri targetUri, string cookieHeader);
    }
}
