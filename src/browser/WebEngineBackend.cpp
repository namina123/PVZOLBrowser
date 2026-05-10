#include "WebEngineBackend.h"

#include <QWebEnginePage>
#include <QWebEngineSettings>
#include <QWebEngineView>

namespace {

class LockedDownWebEnginePage final : public QWebEnginePage
{
public:
    explicit LockedDownWebEnginePage(QObject *parent = nullptr)
        : QWebEnginePage(parent)
    {
    }

protected:
    QWebEnginePage *createWindow(WebWindowType) override
    {
        return this;
    }

    bool acceptNavigationRequest(const QUrl &url, NavigationType type, bool isMainFrame) override
    {
        Q_UNUSED(type);
        Q_UNUSED(isMainFrame);

        if (!url.isValid()) {
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
};

}

WebEngineBackend::WebEngineBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent)
    : IBrowserBackend(config, parent)
{
    m_webView = new QWebEngineView(hostWidget);
    m_webView->setPage(new LockedDownWebEnginePage(m_webView));
    m_webView->settings()->setAttribute(QWebEngineSettings::JavascriptCanOpenWindows, false);
    m_webView->settings()->setAttribute(QWebEngineSettings::FullScreenSupportEnabled, false);
    m_webView->settings()->setAttribute(QWebEngineSettings::PluginsEnabled, false);
    m_webView->settings()->setUnknownUrlSchemePolicy(QWebEngineSettings::DisallowUnknownUrlSchemes);
    connect(m_webView, &QWebEngineView::urlChanged, this, &WebEngineBackend::urlChanged);
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
        m_webView->load(url);
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
    return m_webView->url();
}

QString WebEngineBackend::currentTitle() const
{
    return m_webView->title();
}
