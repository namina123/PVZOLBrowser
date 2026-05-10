#include "MainWindow.h"

#include "BrowserTab.h"
#include "network/ProxyManager.h"

#include <QAction>
#include <QComboBox>
#include <QFrame>
#include <QHBoxLayout>
#include <QLabel>
#include <QLineEdit>
#include <QSignalBlocker>
#include <QSize>
#include <QTabWidget>
#include <QToolButton>
#include <QToolBar>
#include <QUrl>
#include <QVBoxLayout>

MainWindow::MainWindow(QWidget *parent)
    : QMainWindow(parent)
{
    buildUi();
    connectSignals();

    openHomeInNewTab();
}

void MainWindow::buildUi()
{
    resize(1280, 800);
    setMinimumSize(960, 640);
    setObjectName(QStringLiteral("mainWindow"));
    setWindowTitle(tr("PVZOL \u6d4f\u89c8\u5668"));

    m_tabWidget = new QTabWidget(this);
    m_tabWidget->setObjectName(QStringLiteral("browserTabs"));
    m_tabWidget->setDocumentMode(true);
    m_tabWidget->setTabsClosable(true);
    m_tabWidget->setMovable(true);
    setCentralWidget(m_tabWidget);

    auto *toolbar = addToolBar(tr("\u5bfc\u822a\u680f"));
    toolbar->setObjectName(QStringLiteral("navigationBar"));
    toolbar->setMovable(false);
    toolbar->setIconSize(QSize(18, 18));
    toolbar->setToolButtonStyle(Qt::ToolButtonTextOnly);

    m_backAction = toolbar->addAction(tr("\u540e\u9000"));
    m_forwardAction = toolbar->addAction(tr("\u524d\u8fdb"));
    m_reloadAction = toolbar->addAction(tr("\u5237\u65b0"));
    m_homeAction = toolbar->addAction(tr("\u9996\u9875"));
    m_newTabAction = toolbar->addAction(tr("\u65b0\u5efa\u6807\u7b7e"));
    m_closeTabAction = toolbar->addAction(tr("\u5173\u95ed\u6807\u7b7e"));

    m_addressBar = new QLineEdit(this);
    m_addressBar->setObjectName(QStringLiteral("addressBar"));
    m_addressBar->setClearButtonEnabled(true);
    m_addressBar->setPlaceholderText(tr("\u8f93\u5165\u7f51\u5740\uff0c\u4f8b\u5982 http://www.baidu.com"));
    toolbar->addWidget(m_addressBar);

    m_proxyButton = new QToolButton(this);
    m_proxyButton->setObjectName(QStringLiteral("proxyEntryButton"));
    m_proxyButton->setText(tr("\u4ee3\u7406"));
    toolbar->addWidget(m_proxyButton);

    m_proxyPopup = new QFrame(this, Qt::Popup | Qt::FramelessWindowHint);
    m_proxyPopup->setObjectName(QStringLiteral("proxyPopup"));
    m_proxyPopup->setMinimumWidth(320);
    auto *popupLayout = new QVBoxLayout(m_proxyPopup);
    popupLayout->setContentsMargins(16, 16, 16, 16);
    popupLayout->setSpacing(10);

    auto *popupTitle = new QLabel(tr("\u7f51\u7edc\u4ee3\u7406"), m_proxyPopup);
    popupTitle->setObjectName(QStringLiteral("proxyPopupTitle"));
    popupLayout->addWidget(popupTitle);

    m_proxyModeBox = new QComboBox(m_proxyPopup);
    m_proxyModeBox->setObjectName(QStringLiteral("proxyModeBox"));
    m_proxyModeBox->addItem(tr("\u7cfb\u7edf\u4ee3\u7406"));
    m_proxyModeBox->addItem(tr("\u56fa\u5b9a\u4ee3\u7406"));
    popupLayout->addWidget(m_proxyModeBox);

    m_proxyInput = new QLineEdit(m_proxyPopup);
    m_proxyInput->setObjectName(QStringLiteral("proxyInput"));
    m_proxyInput->setClearButtonEnabled(true);
    m_proxyInput->setPlaceholderText(tr("\u4f8b\u5982 127.0.0.1:7890 \u6216 http://127.0.0.1:7890"));
    popupLayout->addWidget(m_proxyInput);

    auto *actionRow = new QHBoxLayout();
    actionRow->setContentsMargins(0, 2, 0, 0);
    actionRow->setSpacing(8);

    m_proxyApplyButton = new QToolButton(m_proxyPopup);
    m_proxyApplyButton->setObjectName(QStringLiteral("proxyApplyButton"));
    m_proxyApplyButton->setText(tr("\u5e94\u7528\u4ee3\u7406"));
    actionRow->addWidget(m_proxyApplyButton);
    actionRow->addStretch(1);
    popupLayout->addLayout(actionRow);

    m_proxyStatusLabel = new QLabel(m_proxyPopup);
    m_proxyStatusLabel->setObjectName(QStringLiteral("proxyStatusLabel"));
    m_proxyStatusLabel->setWordWrap(true);
    popupLayout->addWidget(m_proxyStatusLabel);

    applyVisualStyle();
    syncProxyUi();
}

void MainWindow::connectSignals()
{
    connect(m_addressBar, &QLineEdit::returnPressed, this, &MainWindow::navigateFromAddressBar);
    connect(m_tabWidget, &QTabWidget::currentChanged, this, [this](int) {
        updateUiFromCurrentTab();
    });
    connect(m_tabWidget, &QTabWidget::tabCloseRequested, this, [this](int index) {
        if (m_tabWidget->count() == 1) {
            return;
        }
        QWidget *page = m_tabWidget->widget(index);
        m_tabWidget->removeTab(index);
        page->deleteLater();
        updateUiFromCurrentTab();
    });

    connect(m_backAction, &QAction::triggered, this, [this]() {
        if (auto *tab = currentTab()) {
            tab->goBack();
        }
    });
    connect(m_forwardAction, &QAction::triggered, this, [this]() {
        if (auto *tab = currentTab()) {
            tab->goForward();
        }
    });
    connect(m_reloadAction, &QAction::triggered, this, [this]() {
        if (auto *tab = currentTab()) {
            tab->reloadPage();
        }
    });
    connect(m_homeAction, &QAction::triggered, this, [this]() {
        if (auto *tab = currentTab()) {
            tab->goHome();
        }
    });
    connect(m_newTabAction, &QAction::triggered, this, &MainWindow::openHomeInNewTab);
    connect(m_closeTabAction, &QAction::triggered, this, &MainWindow::closeCurrentTab);
    connect(m_proxyButton, &QToolButton::clicked, this, &MainWindow::toggleProxyPopup);
    connect(m_proxyModeBox, &QComboBox::currentIndexChanged, this, [this](int) {
        syncProxyUi();
    });
    connect(m_proxyInput, &QLineEdit::returnPressed, this, &MainWindow::applyProxySelection);
    connect(m_proxyApplyButton, &QToolButton::clicked, this, &MainWindow::applyProxySelection);
    connect(&ProxyManager::instance(), &ProxyManager::stateChanged, this, &MainWindow::syncProxyUi);
}

void MainWindow::applyVisualStyle()
{
    setStyleSheet(QStringLiteral(R"(
        QMainWindow#mainWindow {
            background: qlineargradient(
                x1: 0, y1: 0, x2: 1, y2: 1,
                stop: 0 #eef6ff,
                stop: 0.45 #f8fbff,
                stop: 1 #edf8f5
            );
        }
        QToolBar#navigationBar {
            background: rgba(255, 255, 255, 0.86);
            border: none;
            spacing: 8px;
            padding: 12px 14px 10px 14px;
        }
        QToolButton {
            color: #1f2937;
            background: #f8fafc;
            border: 1px solid #d8e1ec;
            border-radius: 12px;
            padding: 7px 14px;
            font-size: 13px;
            font-weight: 600;
        }
        QToolButton:hover {
            background: #ffffff;
            border-color: #94a3b8;
        }
        QToolButton:pressed {
            background: #e6edf6;
        }
        QToolButton#proxyEntryButton {
            margin-left: 10px;
            min-width: 62px;
        }
        QLineEdit#addressBar {
            min-height: 40px;
            margin-left: 10px;
            padding: 0 16px;
            background: #ffffff;
            color: #0f172a;
            border: 1px solid #cbd5e1;
            border-radius: 20px;
            selection-background-color: #0f766e;
            font-size: 14px;
        }
        QLineEdit#addressBar:focus {
            border: 1px solid #0f766e;
        }
        QFrame#proxyPopup {
            background: rgba(255, 255, 255, 0.98);
            border: 1px solid rgba(148, 163, 184, 0.35);
            border-radius: 18px;
        }
        QLabel#proxyPopupTitle {
            color: #0f172a;
            font-size: 15px;
            font-weight: 700;
        }
        QComboBox#proxyModeBox,
        QLineEdit#proxyInput {
            min-height: 38px;
            padding: 0 12px;
            background: #ffffff;
            color: #0f172a;
            border: 1px solid #cbd5e1;
            border-radius: 18px;
            font-size: 13px;
        }
        QComboBox#proxyModeBox {
            min-width: 110px;
        }
        QLineEdit#proxyInput {
            min-width: 260px;
        }
        QToolButton#proxyApplyButton {
            min-width: 88px;
        }
        QLabel#proxyStatusLabel {
            color: #0f766e;
            font-size: 12px;
            font-weight: 600;
            min-width: 220px;
        }
        QTabWidget#browserTabs::pane {
            border: none;
            top: -1px;
        }
        QTabBar::tab {
            background: rgba(255, 255, 255, 0.74);
            color: #475569;
            border: 1px solid rgba(148, 163, 184, 0.45);
            border-bottom: none;
            border-top-left-radius: 14px;
            border-top-right-radius: 14px;
            padding: 11px 18px;
            margin-right: 6px;
            min-width: 120px;
            max-width: 220px;
            font-size: 13px;
            font-weight: 600;
        }
        QTabBar::tab:selected {
            background: #ffffff;
            color: #0f172a;
            border-color: rgba(100, 116, 139, 0.55);
        }
        QTabBar::tab:hover:!selected {
            background: rgba(255, 255, 255, 0.9);
            color: #1e293b;
        }
        QTabBar::close-button {
            margin-left: 8px;
        }
    )"));
}

BrowserTab *MainWindow::createTab(const QUrl &initialUrl)
{
    auto *tab = new BrowserTab(this);
    const int index = m_tabWidget->addTab(tab, tr("\u65b0\u6807\u7b7e\u9875"));
    m_tabWidget->setCurrentIndex(index);

    connect(tab, &BrowserTab::urlChanged, this, [this, tab](const QUrl &url) {
        if (tab == currentTab()) {
            m_addressBar->setText(url.toString());
        }
    });
    connect(tab, &BrowserTab::titleChanged, this, [this, tab](const QString &title) {
        refreshTabCaption(tab, title);
        if (tab == currentTab()) {
            syncWindowTitle(title);
        }
    });
    connect(tab, &BrowserTab::loadingStateChanged, this, [this, tab](bool) {
        if (tab == currentTab()) {
            updateUiFromCurrentTab();
        }
    });

    if (initialUrl.isValid()) {
        tab->loadUrl(initialUrl);
    } else {
        tab->goHome();
    }

    return tab;
}

BrowserTab *MainWindow::currentTab() const
{
    return qobject_cast<BrowserTab *>(m_tabWidget->currentWidget());
}

void MainWindow::updateUiFromCurrentTab()
{
    if (auto *tab = currentTab()) {
        m_addressBar->setText(tab->currentUrl().toString());
        syncWindowTitle(tab->currentTitle());
        m_closeTabAction->setEnabled(m_tabWidget->count() > 1);
        return;
    }

    m_addressBar->clear();
    syncWindowTitle(QString());
    m_closeTabAction->setEnabled(false);
}

void MainWindow::navigateFromAddressBar()
{
    const QUrl url = QUrl::fromUserInput(m_addressBar->text().trimmed());
    if (url.isValid()) {
        if (auto *tab = currentTab()) {
            tab->loadUrl(url);
        }
    }
}

void MainWindow::syncWindowTitle(const QString &pageTitle)
{
    if (pageTitle.isEmpty()) {
        setWindowTitle(tr("PVZOL \u6d4f\u89c8\u5668"));
        return;
    }

    setWindowTitle(tr("%1 - PVZOL \u6d4f\u89c8\u5668").arg(pageTitle));
}

void MainWindow::closeCurrentTab()
{
    const int index = m_tabWidget->currentIndex();
    if (index < 0 || m_tabWidget->count() <= 1) {
        return;
    }

    QWidget *page = m_tabWidget->widget(index);
    m_tabWidget->removeTab(index);
    page->deleteLater();
    updateUiFromCurrentTab();
}

void MainWindow::openHomeInNewTab()
{
    createTab();
    updateUiFromCurrentTab();
}

void MainWindow::refreshTabCaption(BrowserTab *tab, const QString &pageTitle)
{
    const int index = m_tabWidget->indexOf(tab);
    if (index < 0) {
        return;
    }

    const QString title = pageTitle.isEmpty() ? tr("\u65b0\u6807\u7b7e\u9875") : pageTitle;
    m_tabWidget->setTabText(index, title.left(20));
}

void MainWindow::toggleProxyPopup()
{
    if (m_proxyPopup->isVisible()) {
        m_proxyPopup->hide();
        return;
    }

    syncProxyUi();
    positionProxyPopup();
    m_proxyPopup->show();
    m_proxyPopup->raise();
    if (m_proxyModeBox->currentIndex() == 1) {
        m_proxyInput->setFocus();
        m_proxyInput->selectAll();
    }
}

void MainWindow::positionProxyPopup() const
{
    const QPoint anchor = m_proxyButton->mapToGlobal(QPoint(0, m_proxyButton->height() + 10));
    m_proxyPopup->move(anchor);
}

void MainWindow::syncProxyUi()
{
    const ProxyManager &proxyManager = ProxyManager::instance();
    const bool prefersSystemMode = proxyManager.preferredMode() == ProxyManager::Mode::System;
    const bool activeSystemMode = proxyManager.mode() == ProxyManager::Mode::System;
    const QSignalBlocker blocker(m_proxyModeBox);
    m_proxyModeBox->setCurrentIndex(prefersSystemMode ? 0 : 1);
    if (!proxyManager.manualProxyText().isEmpty()) {
        m_proxyInput->setText(proxyManager.manualProxyText());
    }
    m_proxyInput->setEnabled(!prefersSystemMode);
    m_proxyApplyButton->setEnabled(prefersSystemMode || !m_proxyInput->text().trimmed().isEmpty());
    m_proxyStatusLabel->setText(proxyManager.statusText());
    m_proxyButton->setToolTip(proxyManager.statusText());
    m_proxyButton->setText(activeSystemMode
        ? tr("\u4ee3\u7406")
        : tr("\u4ee3\u7406*"));
}

void MainWindow::applyProxySelection()
{
    if (m_proxyModeBox->currentIndex() == 0) {
        ProxyManager::instance().useSystemProxy();
        m_proxyPopup->hide();
        return;
    }

    ProxyManager::instance().applyFixedProxy(m_proxyInput->text());
}
