#pragma once

#include <QString>
#include <QUrl>
#include <QWidget>

#include "browser/BrowserConfig.h"

class IBrowserBackend;

class BrowserView : public QWidget
{
    Q_OBJECT

public:
    explicit BrowserView(const BrowserConfig &config = BrowserConfig(), QWidget *parent = nullptr);

    void loadUrl(const QUrl &url);
    void goBack();
    void goForward();
    void reloadPage();
    void goHome();

    QUrl currentUrl() const;
    QString currentTitle() const;

signals:
    void urlChanged(const QUrl &url);
    void titleChanged(const QString &title);
    void loadStarted();
    void loadProgressChanged(int progress);
    void loadFinished(bool ok);

private:
    IBrowserBackend *m_backend = nullptr;
};
