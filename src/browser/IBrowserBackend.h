#pragma once

#include "BrowserConfig.h"

#include <QObject>
#include <QString>
#include <QUrl>

class QWidget;

class IBrowserBackend : public QObject
{
    Q_OBJECT

public:
    explicit IBrowserBackend(const BrowserConfig &config, QObject *parent = nullptr)
        : QObject(parent)
        , m_config(config)
    {
    }

    ~IBrowserBackend() override = default;

    virtual QWidget *widget() const = 0;

    virtual void loadUrl(const QUrl &url) = 0;
    virtual void goBack() = 0;
    virtual void goForward() = 0;
    virtual void reloadPage() = 0;
    virtual void goHome() = 0;

    virtual QUrl currentUrl() const = 0;
    virtual QString currentTitle() const = 0;

signals:
    void urlChanged(const QUrl &url);
    void titleChanged(const QString &title);
    void loadStarted();
    void loadProgressChanged(int progress);
    void loadFinished(bool ok);

protected:
    BrowserConfig m_config;
};
