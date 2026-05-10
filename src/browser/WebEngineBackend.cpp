#include "WebEngineBackend.h"

#include "RuffleProxyServer.h"

#include <QWebEnginePage>
#include <QWebEngineProfile>
#include <QWebEngineScript>
#include <QWebEngineScriptCollection>
#include <QWebEngineSettings>
#include <QWebEngineUrlRequestInfo>
#include <QWebEngineUrlRequestInterceptor>
#include <QWebEngineView>

namespace {

bool isSwfUrl(const QUrl &url)
{
    return url.path().toLower().endsWith(QStringLiteral(".swf"));
}

QString buildRuffleInjectionScript(const QUrl &localBaseUrl)
{
    return QStringLiteral(R"JS(
(function() {
    if (window.__pvzolRuffleInjected) {
        return;
    }
    window.__pvzolRuffleInjected = true;

    var localBase = '%1';
    var managedPathPrefixes = ['/__proxy__/', '/__player__/', '/__ruffle__/'];
    for (var i = 0; i < managedPathPrefixes.length; i += 1) {
        if (window.location.origin === localBase && window.location.pathname.indexOf(managedPathPrefixes[i]) === 0) {
            return;
        }
    }

    var ieUa = 'Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.1; Trident/6.0)';
    try { Object.defineProperty(navigator, 'userAgent', { get: function() { return ieUa; }, configurable: true }); } catch (e) {}
    try { Object.defineProperty(navigator, 'appVersion', { get: function() { return ieUa; }, configurable: true }); } catch (e) {}
    try { Object.defineProperty(navigator, 'appName', { get: function() { return 'Microsoft Internet Explorer'; }, configurable: true }); } catch (e) {}
    try { Object.defineProperty(navigator, 'platform', { get: function() { return 'Win32'; }, configurable: true }); } catch (e) {}
    try { Object.defineProperty(navigator, 'vendor', { get: function() { return ''; }, configurable: true }); } catch (e) {}
    try { Object.defineProperty(document, 'documentMode', { get: function() { return 10; }, configurable: true }); } catch (e) {}

    window.RufflePlayer = window.RufflePlayer || {};
    window.RufflePlayer.config = window.RufflePlayer.config || {};
    var c = window.RufflePlayer.config;
    c.allowScriptAccess = true;
    c.allowNetworking = 'all';
    c.openUrlMode = 'allow';
    c.logLevel = 'error';
    if (window.navigator && ('gpu' in navigator)) {
        c.preferredRenderer = 'webgpu';
    } else if (window.WebGLRenderingContext || window.WebGL2RenderingContext) {
        c.preferredRenderer = 'wgpu-webgl';
    }
    c.deviceFontRenderer = 'canvas';
    c.defaultFonts = {
        sans: ['Noto Sans CJK SC', 'Noto Sans SC', 'Source Han Sans SC', 'Droid Sans Fallback', 'sans-serif'],
        serif: ['Noto Serif CJK SC', 'Noto Serif SC', 'Source Han Serif SC', 'serif'],
        typewriter: ['monospace'],
        japaneseGothic: ['Noto Sans CJK SC', 'Noto Sans SC', 'Source Han Sans SC', 'Droid Sans Fallback', 'sans-serif'],
        japaneseGothicMono: ['monospace'],
        japaneseMincho: ['Noto Serif CJK SC', 'Noto Serif SC', 'Source Han Serif SC', 'serif']
    };

    function injectBootstrap() {
        if (document.getElementById('__pvzol_ruffle_bootstrap__')) {
            return;
        }

        var script = document.createElement('script');
        script.id = '__pvzol_ruffle_bootstrap__';
        script.src = '/__ruffle__/bootstrap.js';
        (document.head || document.documentElement).appendChild(script);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectBootstrap, { once: true });
    } else {
        injectBootstrap();
    }
})();
)JS").arg(localBaseUrl.toString(QUrl::FullyEncoded));
}

class RuffleRequestInterceptor final : public QWebEngineUrlRequestInterceptor
{
public:
    explicit RuffleRequestInterceptor(RuffleProxyServer *proxyServer, QObject *parent = nullptr)
        : QWebEngineUrlRequestInterceptor(parent)
        , m_proxyServer(proxyServer)
    {
    }

    void interceptRequest(QWebEngineUrlRequestInfo &info) override
    {
        if (m_proxyServer == nullptr) {
            return;
        }

        const QUrl requestUrl = info.requestUrl();
        if (!requestUrl.isValid()) {
            return;
        }

        if (m_proxyServer->isManagedUrl(requestUrl)) {
            return;
        }

        const QString scheme = requestUrl.scheme().toLower();
        if (scheme != QStringLiteral("http") && scheme != QStringLiteral("https")) {
            return;
        }

        const QString path = requestUrl.path();
        if (path.startsWith(QStringLiteral("/__ruffle__/")) || path.startsWith(QStringLiteral("/__proxy__/"))) {
            QUrl redirected = m_proxyServer->baseUrl();
            redirected.setPath(path);
            redirected.setQuery(requestUrl.query(QUrl::FullyEncoded));
            info.redirect(redirected);
        }
    }

private:
    RuffleProxyServer *m_proxyServer = nullptr;
};

class LockedDownWebEnginePage final : public QWebEnginePage
{
public:
    explicit LockedDownWebEnginePage(
        QWebEngineProfile *profile,
        RuffleProxyServer *proxyServer,
        QObject *parent = nullptr)
        : QWebEnginePage(profile, parent)
        , m_proxyServer(proxyServer)
    {
    }

protected:
    QWebEnginePage *createWindow(WebWindowType) override
    {
        return nullptr;
    }

    bool acceptNavigationRequest(const QUrl &url, NavigationType type, bool isMainFrame) override
    {
        Q_UNUSED(type);

        if (!url.isValid()) {
            return false;
        }

        if (isMainFrame && isSwfUrl(url) && url.scheme().startsWith(QStringLiteral("http"), Qt::CaseInsensitive)) {
            if (m_proxyServer != nullptr) {
                setUrl(m_proxyServer->playerUrlFor(url));
            }
            return false;
        }

        const QString scheme = url.scheme().toLower();
        return scheme == QStringLiteral("http")
            || scheme == QStringLiteral("https")
            || scheme == QStringLiteral("about")
            || scheme == QStringLiteral("data");
    }

    void javaScriptAlert(const QUrl &, const QString &) override
    {
    }

    bool javaScriptConfirm(const QUrl &, const QString &) override
    {
        return false;
    }

    bool javaScriptPrompt(const QUrl &, const QString &, const QString &, QString *) override
    {
        return false;
    }

    QStringList chooseFiles(FileSelectionMode, const QStringList &, const QStringList &) override
    {
        return {};
    }

private:
    RuffleProxyServer *m_proxyServer = nullptr;
};

}

WebEngineBackend::WebEngineBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent)
    : IBrowserBackend(config, parent)
    , m_proxyServer(new RuffleProxyServer(this))
{
    m_proxyServer->start();

    m_profile = new QWebEngineProfile(this);
    m_profile->setUrlRequestInterceptor(new RuffleRequestInterceptor(m_proxyServer, m_profile));

    QWebEngineScript bootstrapScript;
    bootstrapScript.setName(QStringLiteral("pvzol-ruffle-bootstrap"));
    bootstrapScript.setWorldId(QWebEngineScript::MainWorld);
    bootstrapScript.setInjectionPoint(QWebEngineScript::DocumentCreation);
    bootstrapScript.setRunsOnSubFrames(true);
    bootstrapScript.setSourceCode(buildRuffleInjectionScript(m_proxyServer->baseUrl()));
    m_profile->scripts()->insert(bootstrapScript);

    m_webView = new QWebEngineView(hostWidget);
    auto *page = new LockedDownWebEnginePage(m_profile, m_proxyServer, m_webView);
    m_webView->setPage(page);
    m_webView->settings()->setAttribute(QWebEngineSettings::JavascriptCanOpenWindows, false);
    m_webView->settings()->setAttribute(QWebEngineSettings::FullScreenSupportEnabled, false);
    m_webView->settings()->setAttribute(QWebEngineSettings::PluginsEnabled, false);
    m_webView->settings()->setUnknownUrlSchemePolicy(QWebEngineSettings::DisallowUnknownUrlSchemes);
    connect(m_webView, &QWebEngineView::urlChanged, this, &WebEngineBackend::updateCurrentUrl);
    connect(m_webView, &QWebEngineView::titleChanged, this, &WebEngineBackend::titleChanged);
    connect(m_webView, &QWebEngineView::loadStarted, this, &WebEngineBackend::loadStarted);
    connect(m_webView, &QWebEngineView::loadProgress, this, &WebEngineBackend::loadProgressChanged);
    connect(m_webView, &QWebEngineView::loadFinished, this, &WebEngineBackend::loadFinished);
}

QWidget *WebEngineBackend::widget() const
{
    return m_webView;
}

void WebEngineBackend::loadUrl(const QUrl &url)
{
    if (url.isValid()) {
        m_currentUrl = url;
        m_webView->load(isSwfUrl(url) ? m_proxyServer->playerUrlFor(url) : url);
    }
}

void WebEngineBackend::goBack()
{
    m_webView->back();
}

void WebEngineBackend::goForward()
{
    m_webView->forward();
}

void WebEngineBackend::reloadPage()
{
    m_webView->reload();
}

void WebEngineBackend::goHome()
{
    loadUrl(m_config.homeUrl);
}

QUrl WebEngineBackend::currentUrl() const
{
    return m_currentUrl;
}

QString WebEngineBackend::currentTitle() const
{
    return m_webView->title();
}

void WebEngineBackend::updateCurrentUrl(const QUrl &url)
{
    const QUrl translated = m_proxyServer->isManagedUrl(url) ? m_proxyServer->originalUrlFor(url) : url;
    if (translated.isValid()) {
        m_currentUrl = translated;
    }
    emit urlChanged(m_currentUrl);
}
