#include "ProxyManager.h"

#include <QCoreApplication>
#include <QDir>
#include <QFileInfo>
#include <QNetworkAccessManager>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QSettings>
#include <QTimer>
#include <QUrl>

namespace {

QString normalizeProxyText(QString proxyText)
{
    proxyText = proxyText.trimmed();
    if (proxyText.isEmpty()) {
        return proxyText;
    }

    if (!proxyText.contains(QStringLiteral("://"))) {
        proxyText.prepend(QStringLiteral("http://"));
    }

    return proxyText;
}

}

ProxyManager &ProxyManager::instance()
{
    static ProxyManager instance;
    return instance;
}

ProxyManager::ProxyManager()
    : m_testManager(new QNetworkAccessManager(this))
{
    loadSettings();
}

ProxyManager::Mode ProxyManager::mode() const
{
    return m_mode;
}

ProxyManager::Mode ProxyManager::preferredMode() const
{
    return m_preferredMode;
}

QString ProxyManager::manualProxyText() const
{
    return m_manualProxyText;
}

QString ProxyManager::statusText() const
{
    return m_statusText;
}

bool ProxyManager::shouldForceChromiumBackend() const
{
    return m_mode == Mode::Fixed;
}

void ProxyManager::useSystemProxy()
{
    m_preferredMode = Mode::System;
    saveSettings();
    applySystemProxyNow(tr("\u4ee3\u7406\uff1a\u5df2\u8ddf\u968f\u7cfb\u7edf"), false);
}

void ProxyManager::applySystemProxyNow(const QString &statusText, bool rememberPreference)
{
    if (m_testReply != nullptr) {
        m_testReply->abort();
        m_testReply->deleteLater();
        m_testReply = nullptr;
    }

    if (rememberPreference) {
        m_preferredMode = Mode::System;
        saveSettings();
    }

    m_mode = Mode::System;
    m_pendingProxy = QNetworkProxy();
    m_pendingProxyText.clear();
    QNetworkProxyFactory::setUseSystemConfiguration(true);
    QNetworkProxy::setApplicationProxy(QNetworkProxy(QNetworkProxy::DefaultProxy));
    finishWithStatus(statusText);
}

void ProxyManager::applyFixedProxy(const QString &proxyText)
{
    QNetworkProxy proxy;
    if (!tryBuildHttpProxy(proxyText, &proxy)) {
        m_preferredMode = Mode::System;
        saveSettings();
        applySystemProxyNow(tr("\u4ee3\u7406\uff1a\u56fa\u5b9a\u4ee3\u7406\u683c\u5f0f\u65e0\u6548\uff0c\u5df2\u56de\u9000\u7cfb\u7edf"), false);
        return;
    }

    m_preferredMode = Mode::Fixed;
    m_manualProxyText = proxyText.trimmed();
    saveSettings();
    beginManualValidation(proxy, proxyText.trimmed());
}

void ProxyManager::beginManualValidation(const QNetworkProxy &proxy, const QString &proxyText)
{
    if (m_testReply != nullptr) {
        m_testReply->abort();
        m_testReply->deleteLater();
        m_testReply = nullptr;
    }

    m_pendingProxy = proxy;
    m_pendingProxyText = proxyText;
    m_testManager->setProxy(proxy);

    QNetworkRequest request(QUrl(QStringLiteral("http://www.baidu.com")));
    request.setTransferTimeout(5000);
    m_testReply = m_testManager->get(request);

    connect(m_testReply, &QNetworkReply::finished, this, [this]() {
        QNetworkReply *reply = m_testReply;
        m_testReply = nullptr;
        const bool ok = reply != nullptr
            && reply->error() == QNetworkReply::NoError
            && reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt() > 0;
        if (reply != nullptr) {
            reply->deleteLater();
        }

        if (ok) {
            applyManualProxyNow();
            return;
        }

        applySystemProxyNow(tr("\u4ee3\u7406\uff1a\u56fa\u5b9a\u4ee3\u7406\u4e0d\u53ef\u7528\uff0c\u5df2\u56de\u9000\u7cfb\u7edf"), false);
    });

    QTimer::singleShot(5500, this, [this]() {
        if (m_testReply != nullptr) {
            m_testReply->abort();
        }
    });

    finishWithStatus(tr("\u4ee3\u7406\uff1a\u6b63\u5728\u68c0\u6d4b\u56fa\u5b9a\u4ee3\u7406..."));
}

void ProxyManager::applyManualProxyNow()
{
    m_mode = Mode::Fixed;
    m_manualProxy = m_pendingProxy;
    m_manualProxyText = m_pendingProxyText;
    QNetworkProxyFactory::setUseSystemConfiguration(false);
    QNetworkProxy::setApplicationProxy(m_manualProxy);
    finishWithStatus(tr("\u4ee3\u7406\uff1a\u56fa\u5b9a\u4ee3\u7406\u5df2\u542f\u7528 %1").arg(m_manualProxyText));
}

void ProxyManager::finishWithStatus(const QString &statusText)
{
    m_statusText = statusText;
    emit stateChanged();
}

void ProxyManager::loadSettings()
{
    const QString filePath = settingsFilePath();
    QSettings settings(filePath, QSettings::IniFormat);
    const QString savedMode = settings.value(QStringLiteral("proxy/mode"), QStringLiteral("system")).toString();
    const QString savedProxy = settings.value(QStringLiteral("proxy/manualProxy")).toString().trimmed();

    m_manualProxyText = savedProxy;
    m_preferredMode = savedMode == QStringLiteral("fixed") ? Mode::Fixed : Mode::System;

    if (m_preferredMode == Mode::Fixed && !m_manualProxyText.isEmpty()) {
        applyFixedProxy(m_manualProxyText);
        return;
    }

    applySystemProxyNow(tr("\u4ee3\u7406\uff1a\u5df2\u8ddf\u968f\u7cfb\u7edf"), false);
}

void ProxyManager::saveSettings() const
{
    const QString filePath = settingsFilePath();
    QFileInfo fileInfo(filePath);
    QDir().mkpath(fileInfo.absolutePath());

    QSettings settings(filePath, QSettings::IniFormat);
    settings.setValue(
        QStringLiteral("proxy/mode"),
        m_preferredMode == Mode::Fixed ? QStringLiteral("fixed") : QStringLiteral("system"));
    settings.setValue(QStringLiteral("proxy/manualProxy"), m_manualProxyText);
    settings.sync();
}

QString ProxyManager::settingsFilePath() const
{
    const QString dataDir = QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("PVZOLBrowserData"));
    return QDir(dataDir).filePath(QStringLiteral("settings.ini"));
}

bool ProxyManager::tryBuildHttpProxy(const QString &proxyText, QNetworkProxy *proxy) const
{
    if (proxy == nullptr) {
        return false;
    }

    const QUrl url = QUrl::fromUserInput(normalizeProxyText(proxyText));
    if (!url.isValid() || url.host().isEmpty() || url.port() <= 0) {
        return false;
    }

    QNetworkProxy parsedProxy(QNetworkProxy::HttpProxy, url.host(), static_cast<quint16>(url.port()));
    if (!url.userName().isEmpty()) {
        parsedProxy.setUser(url.userName());
    }
    if (!url.password().isEmpty()) {
        parsedProxy.setPassword(url.password());
    }

    *proxy = parsedProxy;
    return true;
}
