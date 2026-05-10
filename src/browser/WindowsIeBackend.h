#pragma once

#include "IBrowserBackend.h"

class QLabel;
class QAxWidget;
class QTimer;
class QWidget;

class WindowsIeBackend : public IBrowserBackend
{
    Q_OBJECT

public:
    WindowsIeBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent = nullptr);

    QWidget *widget() const override;

    void loadUrl(const QUrl &url) override;
    void goBack() override;
    void goForward() override;
    void reloadPage() override;
    void goHome() override;

    QUrl currentUrl() const override;
    QString currentTitle() const override;

private slots:
    void handleAxEvent(const QString &name, int argc, void *argv);

private:
    void buildUi(QWidget *hostWidget);
    void updateState();
    void enableIeBrowserEmulation();
    void finishLoading(bool ok);
    bool isAllowedUrl(const QUrl &url) const;

    QWidget *m_root = nullptr;
    QAxWidget *m_ieWidget = nullptr;
    QTimer *m_pollTimer = nullptr;
    QLabel *m_errorLabel = nullptr;
    QUrl m_currentUrl;
    QString m_currentTitle;
    int m_progress = 0;
    bool m_isLoading = false;
};
