#pragma once

#include "IBrowserBackend.h"

class RuffleProxyServer;
class QWebEngineProfile;
class QWebEngineView;
class QWidget;

class WebEngineBackend : public IBrowserBackend
{
    Q_OBJECT

public:
    WebEngineBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent = nullptr);

    QWidget *widget() const override;

    void loadUrl(const QUrl &url) override;
    void goBack() override;
    void goForward() override;
    void reloadPage() override;
    void goHome() override;

    QUrl currentUrl() const override;
    QString currentTitle() const override;

private:
    void updateCurrentUrl(const QUrl &url);

    RuffleProxyServer *m_proxyServer = nullptr;
    QWebEngineProfile *m_profile = nullptr;
    QWebEngineView *m_webView = nullptr;
    QUrl m_currentUrl;
};
