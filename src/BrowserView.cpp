#include "BrowserView.h"

#include "browser/BrowserBackendFactory.h"
#include "browser/IBrowserBackend.h"

#include <QVBoxLayout>

BrowserView::BrowserView(const BrowserConfig &config, QWidget *parent)
    : QWidget(parent)
{
    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);

    m_backend = createBrowserBackend(config, this, this);
    layout->addWidget(m_backend->widget());

    connect(m_backend, &IBrowserBackend::urlChanged, this, &BrowserView::urlChanged);
    connect(m_backend, &IBrowserBackend::titleChanged, this, &BrowserView::titleChanged);
    connect(m_backend, &IBrowserBackend::loadStarted, this, &BrowserView::loadStarted);
    connect(m_backend, &IBrowserBackend::loadProgressChanged, this, &BrowserView::loadProgressChanged);
    connect(m_backend, &IBrowserBackend::loadFinished, this, &BrowserView::loadFinished);
}

void BrowserView::loadUrl(const QUrl &url)
{
    m_backend->loadUrl(url);
}

void BrowserView::goBack()
{
    m_backend->goBack();
}

void BrowserView::goForward()
{
    m_backend->goForward();
}

void BrowserView::reloadPage()
{
    m_backend->reloadPage();
}

void BrowserView::goHome()
{
    m_backend->goHome();
}

QUrl BrowserView::currentUrl() const
{
    return m_backend->currentUrl();
}

QString BrowserView::currentTitle() const
{
    return m_backend->currentTitle();
}
