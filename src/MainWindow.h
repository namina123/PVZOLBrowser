#pragma once

#include <QMainWindow>
#include <QString>
#include <QUrl>

class BrowserTab;
class QComboBox;
class QFrame;
class QLabel;
class QLineEdit;
class QTabWidget;
class QAction;
class QToolButton;

class MainWindow : public QMainWindow
{
    Q_OBJECT

public:
    explicit MainWindow(QWidget *parent = nullptr);

private:
    void applyVisualStyle();
    void buildUi();
    void connectSignals();
    BrowserTab *createTab(const QUrl &initialUrl = QUrl());
    BrowserTab *currentTab() const;
    void updateUiFromCurrentTab();
    void navigateFromAddressBar();
    void syncWindowTitle(const QString &pageTitle);
    void closeCurrentTab();
    void openHomeInNewTab();
    void refreshTabCaption(BrowserTab *tab, const QString &pageTitle);
    void toggleProxyPopup();
    void positionProxyPopup() const;
    void syncProxyUi();
    void applyProxySelection();

    QTabWidget *m_tabWidget = nullptr;
    QLineEdit *m_addressBar = nullptr;
    QToolButton *m_proxyButton = nullptr;
    QFrame *m_proxyPopup = nullptr;
    QComboBox *m_proxyModeBox = nullptr;
    QLineEdit *m_proxyInput = nullptr;
    QToolButton *m_proxyApplyButton = nullptr;
    QLabel *m_proxyStatusLabel = nullptr;
    QAction *m_backAction = nullptr;
    QAction *m_forwardAction = nullptr;
    QAction *m_reloadAction = nullptr;
    QAction *m_homeAction = nullptr;
    QAction *m_newTabAction = nullptr;
    QAction *m_closeTabAction = nullptr;
};
