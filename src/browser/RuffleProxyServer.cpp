#include "RuffleProxyServer.h"

#include <QCoreApplication>
#include <QDir>
#include <QEventLoop>
#include <QFile>
#include <QFileInfo>
#include <QNetworkAccessManager>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QRegularExpression>
#include <QTcpServer>
#include <QTcpSocket>
#include <QUrlQuery>

namespace {

QByteArray reasonPhraseFor(int statusCode)
{
    switch (statusCode) {
    case 200:
        return "OK";
    case 204:
        return "No Content";
    case 400:
        return "Bad Request";
    case 404:
        return "Not Found";
    case 502:
        return "Bad Gateway";
    default:
        return "OK";
    }
}

QByteArray headerValue(
    const QList<QPair<QByteArray, QByteArray>> &headers,
    const QByteArray &name)
{
    for (const auto &header : headers) {
        if (header.first.compare(name, Qt::CaseInsensitive) == 0) {
            return header.second;
        }
    }
    return {};
}

qint64 contentLengthFromHeaders(const QList<QPair<QByteArray, QByteArray>> &headers)
{
    bool ok = false;
    const qint64 contentLength = headerValue(headers, "Content-Length").trimmed().toLongLong(&ok);
    return ok && contentLength > 0 ? contentLength : 0;
}

void setOrReplaceHeader(
    QList<QPair<QByteArray, QByteArray>> &headers,
    const QByteArray &name,
    const QByteArray &value)
{
    for (auto &header : headers) {
        if (header.first.compare(name, Qt::CaseInsensitive) == 0) {
            header.second = value;
            return;
        }
    }

    headers.append(qMakePair(name, value));
}

QList<QPair<QByteArray, QByteArray>> stripSecurityHeaders(
    const QList<QPair<QByteArray, QByteArray>> &headers)
{
    QList<QPair<QByteArray, QByteArray>> filtered;
    for (const auto &header : headers) {
        const QByteArray lower = header.first.toLower();
        if (lower == "content-security-policy"
            || lower == "content-security-policy-report-only"
            || lower == "x-frame-options"
            || lower == "content-length") {
            continue;
        }
        filtered.append(header);
    }
    return filtered;
}

bool isHtmlContentType(const QByteArray &contentType)
{
    return contentType.toLower().contains("text/html");
}

bool isSwfUrl(const QUrl &url)
{
    return url.path().toLower().endsWith(QStringLiteral(".swf"));
}

QString extractCharset(const QByteArray &contentType)
{
    const QRegularExpression pattern(QStringLiteral("charset=([^;]+)"), QRegularExpression::CaseInsensitiveOption);
    const QRegularExpressionMatch match = pattern.match(QString::fromLatin1(contentType));
    return match.hasMatch() ? match.captured(1).trimmed().remove('"') : QStringLiteral("utf-8");
}

QByteArray addOrReplaceBaseHref(const QByteArray &html, const QUrl &originalUrl)
{
    QString htmlText = QString::fromUtf8(html);
    const QString baseTag = QStringLiteral("<base href=\"%1\">").arg(QString::fromUtf8(originalUrl.toEncoded()));
    if (htmlText.contains(QRegularExpression(QStringLiteral("<base\\b"), QRegularExpression::CaseInsensitiveOption))) {
        return html;
    }

    QRegularExpression headClose(QStringLiteral("</head>"), QRegularExpression::CaseInsensitiveOption);
    if (htmlText.contains(headClose)) {
        htmlText.replace(headClose, baseTag + QStringLiteral("</head>"));
        return htmlText.toUtf8();
    }

    QRegularExpression htmlOpen(QStringLiteral("<html[^>]*>"), QRegularExpression::CaseInsensitiveOption);
    const QRegularExpressionMatch match = htmlOpen.match(htmlText);
    if (match.hasMatch()) {
        const int insertPos = match.capturedEnd(0);
        htmlText.insert(insertPos, baseTag);
        return htmlText.toUtf8();
    }

    return baseTag.toUtf8() + html;
}

}

RuffleProxyServer::RuffleProxyServer(QObject *parent)
    : QObject(parent)
    , m_server(new QTcpServer(this))
    , m_networkManager(new QNetworkAccessManager(this))
{
    connect(m_server, &QTcpServer::newConnection, this, &RuffleProxyServer::handleNewConnection);
}

bool RuffleProxyServer::start()
{
    if (m_server->isListening()) {
        return true;
    }

    return m_server->listen(QHostAddress::LocalHost, 0);
}

QUrl RuffleProxyServer::proxyUrlFor(const QUrl &url) const
{
    QUrl proxy = baseUrl();
    proxy.setPath(QStringLiteral("/__proxy__/%1/%2%3")
                      .arg(url.scheme(), url.authority(), url.path(QUrl::FullyEncoded)));
    proxy.setQuery(url.query(QUrl::FullyEncoded));
    return proxy;
}

QUrl RuffleProxyServer::playerUrlFor(const QUrl &url) const
{
    QUrl player = baseUrl();
    player.setPath(QStringLiteral("/__player__/"));
    player.setQuery(QStringLiteral("url=%1").arg(QString::fromLatin1(QUrl::toPercentEncoding(url.toString(QUrl::FullyEncoded)))));
    return player;
}

bool RuffleProxyServer::isProxyUrl(const QUrl &url) const
{
    return url.host() == baseUrl().host()
        && url.port() == baseUrl().port()
        && url.path().startsWith(QStringLiteral("/__proxy__/"));
}

bool RuffleProxyServer::isPlayerUrl(const QUrl &url) const
{
    return url.host() == baseUrl().host()
        && url.port() == baseUrl().port()
        && url.path().startsWith(QStringLiteral("/__player__/"));
}

bool RuffleProxyServer::isManagedUrl(const QUrl &url) const
{
    return isProxyUrl(url) || isPlayerUrl(url);
}

QUrl RuffleProxyServer::originalUrlFor(const QUrl &proxyUrl) const
{
    if (isPlayerUrl(proxyUrl)) {
        const QUrlQuery query(proxyUrl);
        const QString encodedOriginal = query.queryItemValue(QStringLiteral("url"));
        return QUrl::fromEncoded(QUrl::fromPercentEncoding(encodedOriginal.toLatin1()).toUtf8());
    }

    if (!isProxyUrl(proxyUrl)) {
        return proxyUrl;
    }

    const QString path = proxyUrl.path();
    const QString prefix = QStringLiteral("/__proxy__/");
    const QString remainder = path.mid(prefix.size());
    const int slash = remainder.indexOf('/');
    if (slash <= 0) {
        return {};
    }

    const QString scheme = remainder.left(slash);
    const QString authorityAndPath = remainder.mid(slash + 1);
    const int nextSlash = authorityAndPath.indexOf('/');
    const QString authority = nextSlash >= 0 ? authorityAndPath.left(nextSlash) : authorityAndPath;
    const QString decodedPath = nextSlash >= 0 ? authorityAndPath.mid(nextSlash) : QStringLiteral("/");

    QUrl url;
    url.setScheme(scheme);
    url.setAuthority(authority);
    url.setPath(decodedPath);
    url.setQuery(proxyUrl.query(QUrl::FullyDecoded));
    return url;
}

QUrl RuffleProxyServer::baseUrl() const
{
    return QUrl(QStringLiteral("http://127.0.0.1:%1").arg(m_server->serverPort()));
}

void RuffleProxyServer::handleNewConnection()
{
    while (QTcpSocket *socket = m_server->nextPendingConnection()) {
        connect(socket, &QTcpSocket::readyRead, this, [this, socket]() {
            handleSocket(socket);
        });
        connect(socket, &QTcpSocket::disconnected, socket, &QObject::deleteLater);
    }
}

RuffleProxyServer::ParsedRequest RuffleProxyServer::parseRequest(const QByteArray &requestData) const
{
    ParsedRequest request;
    const int headerEnd = requestData.indexOf("\r\n\r\n");
    const QByteArray headerBytes = headerEnd >= 0 ? requestData.left(headerEnd) : requestData;
    const QList<QByteArray> lines = headerBytes.split('\n');
    if (lines.isEmpty()) {
        return request;
    }

    const QList<QByteArray> requestLine = lines.first().trimmed().split(' ');
    if (requestLine.size() >= 2) {
        request.method = QString::fromLatin1(requestLine.at(0));
        request.path = QString::fromLatin1(requestLine.at(1));
    }

    for (int i = 1; i < lines.size(); ++i) {
        const QByteArray line = lines.at(i).trimmed();
        if (line.isEmpty()) {
            break;
        }

        const int separator = line.indexOf(':');
        if (separator > 0) {
            request.headers.append({line.left(separator), line.mid(separator + 1).trimmed()});
        }
    }

    if (headerEnd >= 0) {
        request.body = requestData.mid(headerEnd + 4);
    }

    return request;
}

void RuffleProxyServer::handleSocket(QTcpSocket *socket)
{
    QByteArray buffer = socket->property("requestBuffer").toByteArray();
    buffer += socket->readAll();
    socket->setProperty("requestBuffer", buffer);

    const int headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd < 0) {
        return;
    }

    const ParsedRequest partialRequest = parseRequest(buffer.left(headerEnd + 4));
    const qint64 bodyLength = contentLengthFromHeaders(partialRequest.headers);
    const qint64 totalLength = headerEnd + 4 + bodyLength;
    if (buffer.size() < totalLength) {
        return;
    }

    const ParsedRequest request = parseRequest(buffer.left(totalLength));
    socket->setProperty("requestBuffer", QByteArray());

    if (request.path.startsWith(QStringLiteral("/__ruffle__/"))) {
        if (request.method.compare(QStringLiteral("OPTIONS"), Qt::CaseInsensitive) == 0) {
            writeResponse(socket, 204, "No Content", corsHeaders(), {});
            return;
        }
        serveAsset(socket, request);
        return;
    }

    const QUrl requestedUrl(baseUrl().toString() + request.path);
    if (request.path.startsWith(QStringLiteral("/__player__/"))) {
        const QUrl originalUrl = originalUrlFor(requestedUrl);
        if (!originalUrl.isValid() || !isSwfUrl(originalUrl)) {
            writeResponse(socket, 400, "Bad Request", defaultHeaders("text/plain"), "Invalid SWF target");
            return;
        }

        writeResponse(socket, 200, "OK", defaultHeaders("text/html; charset=utf-8"), buildSwfPlayerHtml(originalUrl), request.method == QStringLiteral("HEAD"));
        return;
    }

    if (request.path.startsWith(QStringLiteral("/__proxy__/"))) {
        const QUrl originalUrl = originalUrlFor(requestedUrl);
        serveProxiedContent(socket, request, originalUrl);
        return;
    }

    writeResponse(socket, 404, "Not Found", defaultHeaders("text/plain"), "Not Found");
}

void RuffleProxyServer::serveAsset(QTcpSocket *socket, const ParsedRequest &request)
{
    const QString assetPath = assetRootPath() + QDir::separator() + request.path.mid(QStringLiteral("/__ruffle__/").size());
    bool ok = false;
    const QByteArray body = readFile(assetPath, &ok);
    if (!ok) {
        writeResponse(socket, 404, "Not Found", defaultHeaders("text/plain"), "Missing asset", request.method == QStringLiteral("HEAD"));
        return;
    }

    QList<QPair<QByteArray, QByteArray>> headers = defaultHeaders(guessMimeType(assetPath));
    headers.append(corsHeaders());
    headers.append(qMakePair(QByteArray("Cross-Origin-Resource-Policy"), QByteArray("cross-origin")));
    writeResponse(socket, 200, "OK", headers, body, request.method == QStringLiteral("HEAD"));
}

void RuffleProxyServer::serveProxiedContent(QTcpSocket *socket, const ParsedRequest &request, const QUrl &targetUrl)
{
    if (!targetUrl.isValid()) {
        writeResponse(socket, 400, "Bad Request", defaultHeaders("text/plain"), "Invalid target");
        return;
    }

    QNetworkRequest networkRequest(targetUrl);
    for (const auto &header : request.headers) {
        const QByteArray lower = header.first.toLower();
        if (lower == "host" || lower == "connection" || lower == "content-length") {
            continue;
        }
        networkRequest.setRawHeader(header.first, header.second);
    }

    QNetworkReply *reply = nullptr;
    const QByteArray method = request.method.toLatin1().toUpper();
    if (method == "HEAD") {
        reply = m_networkManager->head(networkRequest);
    } else if (method == "GET") {
        reply = m_networkManager->get(networkRequest);
    } else if (method == "POST") {
        reply = m_networkManager->post(networkRequest, request.body);
    } else if (method == "PUT") {
        reply = m_networkManager->put(networkRequest, request.body);
    } else if (method == "DELETE" && request.body.isEmpty()) {
        reply = m_networkManager->deleteResource(networkRequest);
    } else {
        reply = m_networkManager->sendCustomRequest(networkRequest, method, request.body);
    }

    QEventLoop loop;
    connect(reply, &QNetworkReply::finished, &loop, &QEventLoop::quit);
    loop.exec();

    const int statusCode = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
    QByteArray reason = reply->attribute(QNetworkRequest::HttpReasonPhraseAttribute).toByteArray();
    if (reason.isEmpty()) {
        reason = reasonPhraseFor(statusCode > 0 ? statusCode : 502);
    }

    QList<QPair<QByteArray, QByteArray>> headers;
    const auto rawHeaders = reply->rawHeaderPairs();
    for (const auto &header : rawHeaders) {
        headers.append(header);
    }
    headers = stripSecurityHeaders(headers);
    headers.append(corsHeaders());

    QByteArray body = reply->readAll();
    const QByteArray contentType = headerValue(headers, "Content-Type");
    if (isHtmlContentType(contentType)) {
        body = injectBootstrap(body, targetUrl, contentType);
        setOrReplaceHeader(headers, "Content-Type", QByteArray("text/html; charset=") + extractCharset(contentType).toUtf8());
    }

    writeResponse(socket, statusCode > 0 ? statusCode : 502, reason, headers, body, request.method == QStringLiteral("HEAD"));
    reply->deleteLater();
}

void RuffleProxyServer::writeResponse(
    QTcpSocket *socket,
    int statusCode,
    const QByteArray &reason,
    QList<QPair<QByteArray, QByteArray>> headers,
    const QByteArray &body,
    bool skipBody)
{
    setOrReplaceHeader(headers, "Content-Length", QByteArray::number(body.size()));
    setOrReplaceHeader(headers, "Connection", "close");

    QByteArray response;
    response += "HTTP/1.1 " + QByteArray::number(statusCode) + ' ' + reason + "\r\n";
    for (const auto &header : headers) {
        response += header.first + ": " + header.second + "\r\n";
    }
    response += "\r\n";
    if (!skipBody) {
        response += body;
    }

    socket->write(response);
    socket->disconnectFromHost();
}

QList<QPair<QByteArray, QByteArray>> RuffleProxyServer::defaultHeaders(const QByteArray &mimeType) const
{
    return {
        {"Content-Type", mimeType},
        {"Cache-Control", "no-cache"}
    };
}

QList<QPair<QByteArray, QByteArray>> RuffleProxyServer::corsHeaders() const
{
    return {
        {"Access-Control-Allow-Origin", "*"},
        {"Access-Control-Allow-Methods", "GET, HEAD, OPTIONS"},
        {"Access-Control-Allow-Headers", "*"}
    };
}

QByteArray RuffleProxyServer::guessMimeType(const QString &path) const
{
    const QString lower = path.toLower();
    if (lower.endsWith(QStringLiteral(".html")) || lower.endsWith(QStringLiteral(".htm"))) {
        return "text/html; charset=utf-8";
    }
    if (lower.endsWith(QStringLiteral(".js"))) {
        return "application/javascript; charset=utf-8";
    }
    if (lower.endsWith(QStringLiteral(".css"))) {
        return "text/css; charset=utf-8";
    }
    if (lower.endsWith(QStringLiteral(".json"))) {
        return "application/json; charset=utf-8";
    }
    if (lower.endsWith(QStringLiteral(".wasm"))) {
        return "application/wasm";
    }
    if (lower.endsWith(QStringLiteral(".swf"))) {
        return "application/x-shockwave-flash";
    }
    return "application/octet-stream";
}

QString RuffleProxyServer::assetRootPath() const
{
    return QCoreApplication::applicationDirPath() + QStringLiteral("/assets/ruffle");
}

QByteArray RuffleProxyServer::injectBootstrap(const QByteArray &body, const QUrl &originalUrl, const QByteArray &contentType) const
{
    Q_UNUSED(contentType);

    QString sanitizedHtml = QString::fromUtf8(body);
    sanitizedHtml.remove(QRegularExpression(
        QStringLiteral("<meta[^>]+http-equiv\\s*=\\s*(['\"])content-security-policy(?:-report-only)?\\1[^>]*>"),
        QRegularExpression::CaseInsensitiveOption));

    QByteArray html = addOrReplaceBaseHref(sanitizedHtml.toUtf8(), originalUrl);

    const QByteArray scriptTag = QByteArray("<script>")
        + buildRuffleConfigScript()
        + QByteArray("</script><script src=\"/__ruffle__/bootstrap.js\"></script>");

    if (html.contains(scriptTag)) {
        return html;
    }

    QRegularExpression headClose(QStringLiteral("</head>"), QRegularExpression::CaseInsensitiveOption);
    QString htmlTextWithBootstrap = QString::fromUtf8(html);
    if (htmlTextWithBootstrap.contains(headClose)) {
        htmlTextWithBootstrap.replace(headClose, QString::fromUtf8(scriptTag) + QStringLiteral("</head>"));
        return htmlTextWithBootstrap.toUtf8();
    }

    return scriptTag + html;
}

QByteArray RuffleProxyServer::buildSwfPlayerHtml(const QUrl &originalUrl) const
{
    const QString sourceUrl = proxyUrlFor(originalUrl).toString(QUrl::FullyEncoded);

    QString html;
    html += QStringLiteral("<!doctype html><html><head><meta charset='utf-8'>");
    html += QStringLiteral("<meta name='viewport' content='width=device-width, initial-scale=1'>");
    html += QStringLiteral("<title>PVZOL Flash \u64ad\u653e\u5668</title>");
    html += QStringLiteral("<style>"
                           "html,body{margin:0;padding:0;height:100%;background:#07111f;color:#e2e8f0;font-family:'Microsoft YaHei UI','PingFang SC','Noto Sans CJK SC',sans-serif;overflow:hidden;}"
                           "body{display:flex;flex-direction:column;}"
                           ".topbar{padding:12px 16px;background:linear-gradient(135deg,#0f172a,#134e4a);font-size:12px;line-height:1.6;word-break:break-all;box-shadow:0 10px 30px rgba(0,0,0,0.2);}"
                           ".status{color:#7dd3fc;}"
                           ".host{flex:1;min-height:0;background:#000;}"
                           "#player-host,#player-host ruffle-player{width:100%;height:100%;}"
                           "ruffle-player,ruffle-embed,ruffle-object{width:100%!important;height:100%!important;max-width:100%!important;max-height:100%!important;}"
                           "</style>");
    html += QStringLiteral("<script>%1</script>").arg(QString::fromUtf8(buildRuffleConfigScript()));
    html += QStringLiteral("<script src='/__ruffle__/ruffle.js'></script>");
    html += QStringLiteral("</head><body>");
    html += QStringLiteral("<div class='topbar'>");
    html += QStringLiteral("<div>\u5f53\u524d\u6a21\u5f0f\uff1aRuffle SWF \u64ad\u653e</div>");
    html += QStringLiteral("<div>\u6e90\u5730\u5740\uff1a%1</div>").arg(originalUrl.toString(QUrl::FullyEncoded).toHtmlEscaped());
    html += QStringLiteral("<div class='status' id='status'>\u72b6\u6001\uff1a\u7b49\u5f85\u52a0\u8f7d</div>");
    html += QStringLiteral("</div>");
    html += QStringLiteral("<div class='host'><div id='player-host'></div></div>");
    html += QStringLiteral("<script>"
                           "(function(){"
                           "function setStatus(message){var node=document.getElementById('status');if(node){node.textContent='\\u72b6\\u6001\\uff1a'+message;}}"
                           "function boot(){"
                           "try{"
                           "if(!window.RufflePlayer||typeof window.RufflePlayer.newest!=='function'){setStatus('Ruffle \\u8fd0\\u884c\\u65f6\\u4e0d\\u53ef\\u7528');return;}"
                           "var factory=window.RufflePlayer.newest();"
                           "if(!factory||typeof factory.createPlayer!=='function'){setStatus('Ruffle \\u5de5\\u5382\\u4e0d\\u53ef\\u7528');return;}"
                           "var player=factory.createPlayer();"
                           "player.style.width='100%';"
                           "player.style.height='100%';"
                           "var host=document.getElementById('player-host');"
                           "host.innerHTML='';"
                           "host.appendChild(player);"
                           "setStatus('\\u6b63\\u5728\\u52a0\\u8f7d');"
                           "var result=player.load('%1');"
                           "if(result&&typeof result.then==='function'){result.then(function(){setStatus('\\u5df2\\u52a0\\u8f7d');}).catch(function(error){setStatus('\\u52a0\\u8f7d\\u5931\\u8d25\\uff1a'+String(error));});}"
                           "else{setStatus('\\u5df2\\u53d1\\u8d77\\u52a0\\u8f7d');}"
                           "}catch(error){setStatus('\\u5f02\\u5e38\\uff1a'+String(error));}"
                           "}"
                           "if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',boot,{once:true});}else{boot();}"
                           "})();"
                           "</script>").arg(sourceUrl.toHtmlEscaped());
    html += QStringLiteral("</body></html>");

    return html.toUtf8();
}

QByteArray RuffleProxyServer::buildRuffleConfigScript() const
{
    return QByteArrayLiteral(
        "(function(){"
        "var ieUa='Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.1; Trident/6.0)';"
        "try{Object.defineProperty(navigator,'userAgent',{get:function(){return ieUa;},configurable:true});}catch(e){}"
        "try{Object.defineProperty(navigator,'appVersion',{get:function(){return ieUa;},configurable:true});}catch(e){}"
        "try{Object.defineProperty(navigator,'appName',{get:function(){return 'Microsoft Internet Explorer';},configurable:true});}catch(e){}"
        "try{Object.defineProperty(navigator,'platform',{get:function(){return 'Win32';},configurable:true});}catch(e){}"
        "try{Object.defineProperty(navigator,'vendor',{get:function(){return '';},configurable:true});}catch(e){}"
        "try{Object.defineProperty(document,'documentMode',{get:function(){return 10;},configurable:true});}catch(e){}"
        "window.RufflePlayer=window.RufflePlayer||{};"
        "window.RufflePlayer.config=window.RufflePlayer.config||{};"
        "var c=window.RufflePlayer.config;"
        "c.allowScriptAccess=true;"
        "c.allowNetworking='all';"
        "c.openUrlMode='allow';"
        "c.logLevel='error';"
        "if(window.navigator&&('gpu' in navigator)){c.preferredRenderer='webgpu';}"
        "else if(window.WebGLRenderingContext||window.WebGL2RenderingContext){c.preferredRenderer='wgpu-webgl';}"
        "c.deviceFontRenderer='canvas';"
        "c.defaultFonts={"
        "sans:['Noto Sans CJK SC','Noto Sans SC','Source Han Sans SC','Droid Sans Fallback','sans-serif'],"
        "serif:['Noto Serif CJK SC','Noto Serif SC','Source Han Serif SC','serif'],"
        "typewriter:['monospace'],"
        "japaneseGothic:['Noto Sans CJK SC','Noto Sans SC','Source Han Sans SC','Droid Sans Fallback','sans-serif'],"
        "japaneseGothicMono:['monospace'],"
        "japaneseMincho:['Noto Serif CJK SC','Noto Serif SC','Source Han Serif SC','serif']"
        "};"
        "})();");
}

QByteArray RuffleProxyServer::readFile(const QString &path, bool *ok) const
{
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) {
        if (ok != nullptr) {
            *ok = false;
        }
        return {};
    }

    if (ok != nullptr) {
        *ok = true;
    }
    return file.readAll();
}
