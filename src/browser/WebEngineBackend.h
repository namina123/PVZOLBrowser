#pragma once

#include "IBrowserBackend.h"

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
    QWebEngineView *m_webView = nullptr;
};
