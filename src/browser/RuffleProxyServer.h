#pragma once

#include <QObject>
#include <QUrl>

class QNetworkAccessManager;
class QTcpServer;
class QTcpSocket;

class RuffleProxyServer : public QObject
{
    Q_OBJECT

public:
    explicit RuffleProxyServer(QObject *parent = nullptr);

    bool start();

    QUrl proxyUrlFor(const QUrl &url) const;
    QUrl playerUrlFor(const QUrl &url) const;
    bool isProxyUrl(const QUrl &url) const;
    bool isPlayerUrl(const QUrl &url) const;
    bool isManagedUrl(const QUrl &url) const;
    QUrl originalUrlFor(const QUrl &proxyUrl) const;
    QUrl baseUrl() const;

private slots:
    void handleNewConnection();

private:
    struct ParsedRequest
    {
        QString method;
        QString path;
        QList<QPair<QByteArray, QByteArray>> headers;
        QByteArray body;
    };

    ParsedRequest parseRequest(const QByteArray &requestData) const;
    void handleSocket(QTcpSocket *socket);
    void serveAsset(QTcpSocket *socket, const ParsedRequest &request);
    void serveProxiedContent(QTcpSocket *socket, const ParsedRequest &request, const QUrl &targetUrl);
    void writeResponse(
        QTcpSocket *socket,
        int statusCode,
        const QByteArray &reason,
        QList<QPair<QByteArray, QByteArray>> headers,
        const QByteArray &body,
        bool skipBody = false);

    QList<QPair<QByteArray, QByteArray>> defaultHeaders(const QByteArray &mimeType) const;
    QList<QPair<QByteArray, QByteArray>> corsHeaders() const;
    QByteArray guessMimeType(const QString &path) const;
    QString assetRootPath() const;
    QByteArray injectBootstrap(const QByteArray &body, const QUrl &originalUrl, const QByteArray &contentType) const;
    QByteArray buildSwfPlayerHtml(const QUrl &originalUrl) const;
    QByteArray buildRuffleConfigScript() const;
    QByteArray readFile(const QString &path, bool *ok) const;

    QTcpServer *m_server = nullptr;
    QNetworkAccessManager *m_networkManager = nullptr;
};
