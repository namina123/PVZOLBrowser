#include "WindowsIeBackend.h"

#include <qt_windows.h>

#include <QAxWidget>
#include <QCoreApplication>
#include <QFileInfo>
#include <QLabel>
#include <QSettings>
#include <QTimer>
#include <QUrl>
#include <QVBoxLayout>
#include <QWidget>

WindowsIeBackend::WindowsIeBackend(const BrowserConfig &config, QWidget *hostWidget, QObject *parent)
    : IBrowserBackend(config, parent)
{
    buildUi(hostWidget);
}

QWidget *WindowsIeBackend::widget() const
{
    return m_root;
}

void WindowsIeBackend::loadUrl(const QUrl &url)
{
    if (!url.isValid() || m_ieWidget == nullptr || m_ieWidget->isNull()) {
        return;
    }

    m_isLoading = true;
    m_progress = 10;
    emit loadStarted();
    emit loadProgressChanged(m_progress);

    m_ieWidget->dynamicCall("Navigate(const QString&)", url.toString());
    updateState();
}

void WindowsIeBackend::goBack()
{
    if (m_ieWidget != nullptr && !m_ieWidget->isNull()) {
        m_ieWidget->dynamicCall("GoBack()");
    }
}

void WindowsIeBackend::goForward()
{
    if (m_ieWidget != nullptr && !m_ieWidget->isNull()) {
        m_ieWidget->dynamicCall("GoForward()");
    }
}

void WindowsIeBackend::reloadPage()
{
    if (m_ieWidget != nullptr && !m_ieWidget->isNull()) {
        m_ieWidget->dynamicCall("Refresh()");
    }
}

void WindowsIeBackend::goHome()
{
    loadUrl(m_config.homeUrl);
}

QUrl WindowsIeBackend::currentUrl() const
{
    return m_currentUrl;
}

QString WindowsIeBackend::currentTitle() const
{
    return m_currentTitle;
}

void WindowsIeBackend::buildUi(QWidget *hostWidget)
{
    m_root = new QWidget(hostWidget);

    auto *layout = new QVBoxLayout(m_root);
    layout->setContentsMargins(0, 0, 0, 0);

    enableIeBrowserEmulation();

    m_ieWidget = new QAxWidget(QStringLiteral("Shell.Explorer"), m_root);
    if (m_ieWidget->isNull()) {
        m_errorLabel = new QLabel(
            tr("IE \u5185\u6838\u63a7\u4ef6\u521d\u59cb\u5316\u5931\u8d25\uff0c\u65e0\u6cd5\u542f\u52a8\u6d4f\u89c8\u5668\u3002"),
            m_root);
        m_errorLabel->setAlignment(Qt::AlignCenter);
        layout->addWidget(m_errorLabel);
        return;
    }

    m_ieWidget->setProperty("Silent", true);
    layout->addWidget(m_ieWidget);
    connect(m_ieWidget, SIGNAL(signal(QString,int,void*)), this, SLOT(handleAxEvent(QString,int,void*)));

    m_pollTimer = new QTimer(this);
    m_pollTimer->setInterval(500);
    connect(m_pollTimer, &QTimer::timeout, this, &WindowsIeBackend::updateState);
    m_pollTimer->start();
}

void WindowsIeBackend::updateState()
{
    if (m_ieWidget == nullptr || m_ieWidget->isNull()) {
        return;
    }

    const QUrl newUrl = QUrl::fromUserInput(m_ieWidget->property("LocationURL").toString());
    if (newUrl.isValid() && newUrl != m_currentUrl) {
        m_currentUrl = newUrl;
        emit urlChanged(m_currentUrl);
    }

    const QString newTitle = m_ieWidget->property("LocationName").toString();
    if (!newTitle.isEmpty() && newTitle != m_currentTitle) {
        m_currentTitle = newTitle;
        emit titleChanged(m_currentTitle);
    }

    if (!m_isLoading) {
        return;
    }

    const int readyState = m_ieWidget->property("ReadyState").toInt();
    if (readyState >= 4) {
        finishLoading(true);
        return;
    }

    if (m_progress < 90) {
        m_progress += 15;
        emit loadProgressChanged(m_progress);
    }
}

void WindowsIeBackend::enableIeBrowserEmulation()
{
    const QString exeName = QFileInfo(QCoreApplication::applicationFilePath()).fileName();
    if (exeName.isEmpty()) {
        return;
    }

    QSettings settings(
        QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_BROWSER_EMULATION"),
        QSettings::NativeFormat);
    settings.setValue(exeName, 11001);
}

void WindowsIeBackend::finishLoading(bool ok)
{
    if (!m_isLoading) {
        return;
    }

    m_isLoading = false;
    m_progress = 100;
    emit loadProgressChanged(m_progress);
    emit loadFinished(ok);
}

void WindowsIeBackend::handleAxEvent(const QString &name, int argc, void *argv)
{
    auto *params = static_cast<VARIANTARG *>(argv);
    if (params == nullptr || argc <= 0) {
        return;
    }

    if (name.startsWith(QStringLiteral("NewWindow3(")) && argc >= 5) {
        VARIANT_BOOL *cancel = params[argc - 2].pboolVal;
        if (cancel != nullptr) {
            *cancel = VARIANT_TRUE;
        }

        const QString rawUrl = params[0].bstrVal != nullptr
            ? QString::fromWCharArray(params[0].bstrVal)
            : QString();
        const QUrl targetUrl = QUrl::fromUserInput(rawUrl);
        if (isAllowedUrl(targetUrl)) {
            loadUrl(targetUrl);
        }
        return;
    }

    if (name.startsWith(QStringLiteral("NewWindow2(")) && argc >= 2) {
        VARIANT_BOOL *cancel = params[argc - 2].pboolVal;
        if (cancel != nullptr) {
            *cancel = VARIANT_TRUE;
        }
        return;
    }

    if (name.startsWith(QStringLiteral("BeforeNavigate2(")) && argc >= 7) {
        const VARIANTARG urlVariant = *params[argc - 2].pvarVal;
        VARIANT_BOOL *cancel = params[argc - 7].pboolVal;

        if (cancel == nullptr || urlVariant.vt != VT_BSTR || urlVariant.bstrVal == nullptr) {
            return;
        }

        const QUrl targetUrl = QUrl::fromUserInput(QString::fromWCharArray(urlVariant.bstrVal));
        if (!isAllowedUrl(targetUrl)) {
            *cancel = VARIANT_TRUE;
        }
    }
}

bool WindowsIeBackend::isAllowedUrl(const QUrl &url) const
{
    if (!url.isValid()) {
        return false;
    }

    const QString scheme = url.scheme().toLower();
    return scheme == QStringLiteral("http")
        || scheme == QStringLiteral("https")
        || scheme == QStringLiteral("about");
}
