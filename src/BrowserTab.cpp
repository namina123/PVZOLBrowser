#include "BrowserTab.h"

#include "BrowserView.h"

#include <QProgressBar>
#include <QVBoxLayout>

BrowserTab::BrowserTab(QWidget *parent)
    : QWidget(parent)
{
    setObjectName(QStringLiteral("browserTab"));

    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->setSpacing(0);

    m_progressBar = new QProgressBar(this);
    m_progressBar->setRange(0, 100);
    m_progressBar->setTextVisible(false);
    m_progressBar->setFixedHeight(3);
    m_progressBar->hide();
    applyProgressStyle();

    m_browserView = new BrowserView({}, this);

    layout->addWidget(m_progressBar);
    layout->addWidget(m_browserView);

    connect(m_browserView, &BrowserView::urlChanged, this, &BrowserTab::urlChanged);
    connect(m_browserView, &BrowserView::titleChanged, this, &BrowserTab::titleChanged);
    connect(m_browserView, &BrowserView::loadStarted, this, [this]() {
        m_isLoading = true;
        m_progressBar->setValue(5);
        m_progressBar->show();
        emit loadingStateChanged(true);
    });
    connect(m_browserView, &BrowserView::loadProgressChanged, this, [this](int progress) {
        m_progressBar->setValue(progress);
    });
    connect(m_browserView, &BrowserView::loadFinished, this, [this](bool) {
        m_isLoading = false;
        m_progressBar->setValue(100);
        m_progressBar->hide();
        emit loadingStateChanged(false);
    });
}

void BrowserTab::loadUrl(const QUrl &url)
{
    m_browserView->loadUrl(url);
}

void BrowserTab::goBack()
{
    m_browserView->goBack();
}

void BrowserTab::goForward()
{
    m_browserView->goForward();
}

void BrowserTab::reloadPage()
{
    m_browserView->reloadPage();
}

void BrowserTab::goHome()
{
    m_browserView->goHome();
}

QUrl BrowserTab::currentUrl() const
{
    return m_browserView->currentUrl();
}

QString BrowserTab::currentTitle() const
{
    return m_browserView->currentTitle();
}

bool BrowserTab::isLoading() const
{
    return m_isLoading;
}

void BrowserTab::applyProgressStyle()
{
    m_progressBar->setStyleSheet(QStringLiteral(R"(
        QProgressBar {
            background: transparent;
            border: none;
        }
        QProgressBar::chunk {
            background: qlineargradient(
                x1: 0, y1: 0, x2: 1, y2: 0,
                stop: 0 #0f766e,
                stop: 1 #38bdf8
            );
            border-radius: 1px;
        }
    )"));
}
