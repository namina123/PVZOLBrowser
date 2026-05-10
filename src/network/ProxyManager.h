#pragma once

#include <QObject>
#include <QNetworkProxy>

class QNetworkAccessManager;
class QNetworkReply;

class ProxyManager : public QObject
{
    Q_OBJECT

public:
    enum class Mode {
        System,
        Fixed
    };
    Q_ENUM(Mode)

    static ProxyManager &instance();

    Mode mode() const;
    Mode preferredMode() const;
    QString manualProxyText() const;
    QString statusText() const;
    bool shouldForceChromiumBackend() const;

public slots:
    void useSystemProxy();
    void applyFixedProxy(const QString &proxyText);

signals:
    void stateChanged();

private:
    ProxyManager();
    Q_DISABLE_COPY_MOVE(ProxyManager)

    void applySystemProxyNow(const QString &statusText, bool rememberPreference);
    void loadSettings();
    void saveSettings() const;
    QString settingsFilePath() const;
    void beginManualValidation(const QNetworkProxy &proxy, const QString &proxyText);
    void applyManualProxyNow();
    void finishWithStatus(const QString &statusText);
    bool tryBuildHttpProxy(const QString &proxyText, QNetworkProxy *proxy) const;

    Mode m_mode = Mode::System;
    Mode m_preferredMode = Mode::System;
    QNetworkProxy m_manualProxy;
    QString m_manualProxyText;
    QString m_statusText;
    QNetworkAccessManager *m_testManager = nullptr;
    QNetworkReply *m_testReply = nullptr;
    QNetworkProxy m_pendingProxy;
    QString m_pendingProxyText;
};
