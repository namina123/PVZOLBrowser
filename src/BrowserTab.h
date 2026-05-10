#pragma once

#include <QString>
#include <QUrl>
#include <QWidget>

class BrowserView;
class QProgressBar;

class BrowserTab : public QWidget
{
    Q_OBJECT

public:
    explicit BrowserTab(QWidget *parent = nullptr);

    void loadUrl(const QUrl &url);
    void goBack();
    void goForward();
    void reloadPage();
    void goHome();

    QUrl currentUrl() const;
    QString currentTitle() const;
    bool isLoading() const;

signals:
    void urlChanged(const QUrl &url);
    void titleChanged(const QString &title);
    void loadingStateChanged(bool loading);

private:
    void applyProgressStyle();

    BrowserView *m_browserView = nullptr;
    QProgressBar *m_progressBar = nullptr;
    bool m_isLoading = false;
};
