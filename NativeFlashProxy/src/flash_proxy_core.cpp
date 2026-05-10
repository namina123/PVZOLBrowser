#include "flash_proxy_core.h"
#include "native_error.h"

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cctype>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <netdb.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

namespace {

#ifdef _WIN32
using SocketHandle = SOCKET;
constexpr SocketHandle kInvalidSocket = INVALID_SOCKET;
#else
using SocketHandle = int;
constexpr SocketHandle kInvalidSocket = -1;
#endif

struct ParsedRequest {
    std::string method;
    std::string raw_target;
    std::string http_version;
    std::vector<std::pair<std::string, std::string>> headers;
    std::string body;
};

struct ParsedUrl {
    std::string scheme;
    std::string host;
    std::string path_and_query;
    int port = 80;
};

struct HostPort {
    std::string host;
    int port = 80;
};

std::once_flag g_socket_init_once;

std::string trim(const std::string& value) {
    const auto first = std::find_if_not(value.begin(), value.end(), [](unsigned char ch) {
        return std::isspace(ch) != 0;
    });
    const auto last = std::find_if_not(value.rbegin(), value.rend(), [](unsigned char ch) {
        return std::isspace(ch) != 0;
    }).base();
    if (first >= last) {
        return {};
    }
    return std::string(first, last);
}

std::string toLower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

std::string narrowAscii(const std::wstring& value) {
    std::string result;
    result.reserve(value.size());
    for (wchar_t ch : value) {
        result.push_back(ch >= 0 && ch <= 0x7f ? static_cast<char>(ch) : '?');
    }
    return result;
}

void closeSocket(SocketHandle socket_handle) {
    if (socket_handle == kInvalidSocket) {
        return;
    }
#ifdef _WIN32
    closesocket(socket_handle);
#else
    close(socket_handle);
#endif
}

std::string lastSocketErrorMessage() {
#ifdef _WIN32
    return "socket error " + std::to_string(WSAGetLastError());
#else
    return std::strerror(errno);
#endif
}

void initializeSockets() {
    std::call_once(g_socket_init_once, []() {
#ifdef _WIN32
        WSADATA data{};
        WSAStartup(MAKEWORD(2, 2), &data);
#endif
    });
}

bool sendAll(SocketHandle socket_handle, const char* data, std::size_t size) {
    std::size_t sent_total = 0;
    while (sent_total < size) {
        const int sent_now = send(socket_handle, data + sent_total, static_cast<int>(size - sent_total), 0);
        if (sent_now <= 0) {
            return false;
        }
        sent_total += static_cast<std::size_t>(sent_now);
    }
    return true;
}

bool sendAll(SocketHandle socket_handle, const std::string& data) {
    return sendAll(socket_handle, data.data(), data.size());
}

bool recvToDelimiter(SocketHandle socket_handle, std::string& output, const std::string& delimiter, std::size_t max_bytes) {
    char buffer[4096];
    while (output.find(delimiter) == std::string::npos) {
        const int received = recv(socket_handle, buffer, static_cast<int>(sizeof(buffer)), 0);
        if (received <= 0) {
            return false;
        }
        output.append(buffer, buffer + received);
        if (output.size() > max_bytes) {
            return false;
        }
    }
    return true;
}

bool recvExact(SocketHandle socket_handle, std::string& output, std::size_t size) {
    char buffer[4096];
    while (output.size() < size) {
        const auto wanted = static_cast<int>(std::min<std::size_t>(sizeof(buffer), size - output.size()));
        const int received = recv(socket_handle, buffer, wanted, 0);
        if (received <= 0) {
            return false;
        }
        output.append(buffer, buffer + received);
    }
    return true;
}

bool parseRequest(const std::string& raw_request, ParsedRequest& request, std::string& error) {
    const auto header_end = raw_request.find("\r\n\r\n");
    if (header_end == std::string::npos) {
        error = "invalid HTTP request";
        return false;
    }

    std::istringstream stream(raw_request.substr(0, header_end));
    std::string request_line;
    if (!std::getline(stream, request_line)) {
        error = "missing request line";
        return false;
    }
    if (!request_line.empty() && request_line.back() == '\r') {
        request_line.pop_back();
    }

    std::istringstream line_stream(request_line);
    if (!(line_stream >> request.method >> request.raw_target >> request.http_version)) {
        error = "malformed request line";
        return false;
    }

    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        const auto colon = line.find(':');
        if (colon == std::string::npos) {
            continue;
        }
        request.headers.emplace_back(trim(line.substr(0, colon)), trim(line.substr(colon + 1)));
    }

    request.body = raw_request.substr(header_end + 4);
    return true;
}

int contentLengthFromHeaders(const std::vector<std::pair<std::string, std::string>>& headers) {
    for (const auto& header : headers) {
        if (toLower(header.first) == "content-length") {
            try {
                return std::stoi(header.second);
            } catch (...) {
                return -1;
            }
        }
    }
    return 0;
}

bool parseHostPort(const std::string& value, HostPort& host_port) {
    auto host = trim(value);
    if (host.empty()) {
        return false;
    }

    const auto colon = host.rfind(':');
    if (colon != std::string::npos && host.find(']') == std::string::npos) {
        host_port.host = host.substr(0, colon);
        try {
            host_port.port = std::stoi(host.substr(colon + 1));
        } catch (...) {
            return false;
        }
        return true;
    }

    host_port.host = host;
    host_port.port = 80;
    return true;
}

bool parseAbsoluteUrl(const std::string& value, ParsedUrl& parsed) {
    const auto scheme_sep = value.find("://");
    if (scheme_sep == std::string::npos) {
        return false;
    }

    parsed.scheme = toLower(value.substr(0, scheme_sep));
    if (parsed.scheme != "http") {
        return false;
    }

    const auto authority_start = scheme_sep + 3;
    const auto path_start = value.find('/', authority_start);
    const auto authority = value.substr(authority_start, path_start == std::string::npos ? std::string::npos : path_start - authority_start);

    HostPort host_port;
    if (!parseHostPort(authority, host_port)) {
        return false;
    }

    parsed.host = host_port.host;
    parsed.port = host_port.port;
    parsed.path_and_query = path_start == std::string::npos ? "/" : value.substr(path_start);
    if (parsed.path_and_query.empty()) {
        parsed.path_and_query = "/";
    }
    return true;
}

std::string guessMimeType(const std::filesystem::path& path) {
    const auto ext = toLower(path.extension().string());
    if (ext == ".swf") return "application/x-shockwave-flash";
    if (ext == ".html" || ext == ".htm") return "text/html; charset=utf-8";
    if (ext == ".js") return "application/javascript; charset=utf-8";
    if (ext == ".css") return "text/css; charset=utf-8";
    if (ext == ".png") return "image/png";
    if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
    if (ext == ".gif") return "image/gif";
    if (ext == ".svg") return "image/svg+xml";
    if (ext == ".xml") return "application/xml; charset=utf-8";
    return "application/octet-stream";
}

std::string sanitizeRelativePath(std::string raw_path) {
    while (!raw_path.empty() && raw_path.front() == '/') {
        raw_path.erase(raw_path.begin());
    }
    if (raw_path.empty()) {
        return {};
    }

    const auto query = raw_path.find('?');
    if (query != std::string::npos) {
        raw_path = raw_path.substr(0, query);
    }

    std::replace(raw_path.begin(), raw_path.end(), '\\', '/');

    std::string cleaned;
    cleaned.reserve(raw_path.size());
    for (char ch : raw_path) {
        const bool invalid_char =
            ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|';
        if (!invalid_char) {
            cleaned.push_back(ch);
        }
    }
    return cleaned;
}

SocketHandle connectTcp(const std::string& host, int port, std::string& error) {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* result = nullptr;
    const auto port_text = std::to_string(port);
    const int gai_result = getaddrinfo(host.c_str(), port_text.c_str(), &hints, &result);
    if (gai_result != 0) {
        error = "getaddrinfo failed for " + host;
        return kInvalidSocket;
    }

    SocketHandle connected = kInvalidSocket;
    for (addrinfo* current = result; current != nullptr; current = current->ai_next) {
        SocketHandle candidate = socket(current->ai_family, current->ai_socktype, current->ai_protocol);
        if (candidate == kInvalidSocket) {
            continue;
        }

        if (connect(candidate, current->ai_addr, static_cast<int>(current->ai_addrlen)) == 0) {
            connected = candidate;
            break;
        }

        closeSocket(candidate);
    }

    freeaddrinfo(result);

    if (connected == kInvalidSocket) {
        error = "connect failed for " + host + ":" + std::to_string(port) + " (" + lastSocketErrorMessage() + ")";
    }
    return connected;
}

class FlashProxyCore {
public:
    bool setCacheRoot(const std::filesystem::path& path) {
        std::scoped_lock lock(mutex_);
        cache_root_ = path;
        return true;
    }

    bool clearMappingHosts() {
        std::scoped_lock lock(mutex_);
        mapping_hosts_.clear();
        return true;
    }

    bool addMappingHost(const std::wstring& host) {
        std::scoped_lock lock(mutex_);
        mapping_hosts_.push_back(toLower(narrowAscii(host)));
        return true;
    }

    bool clearMappingUrlKeywords() {
        std::scoped_lock lock(mutex_);
        mapping_url_keywords_.clear();
        return true;
    }

    bool addMappingUrlKeyword(const std::wstring& value) {
        std::scoped_lock lock(mutex_);
        mapping_url_keywords_.push_back(toLower(narrowAscii(value)));
        return true;
    }

    bool setUpstreamProxy(const std::wstring& proxy) {
        std::scoped_lock lock(mutex_);
        upstream_proxy_ = narrowAscii(proxy);
        return true;
    }

    bool start(int preferred_port, int* actual_port) {
        stop();
        initializeSockets();

        SocketHandle listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener == kInvalidSocket) {
            setLastError("failed to create listening socket");
            return false;
        }

        const int reuse_value = 1;
        setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse_value), sizeof(reuse_value));

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(static_cast<unsigned short>(preferred_port));

        if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
            setLastError("failed to bind port " + std::to_string(preferred_port) + ": " + lastSocketErrorMessage());
            closeSocket(listener);
            return false;
        }

        if (listen(listener, SOMAXCONN) != 0) {
            setLastError("failed to listen: " + lastSocketErrorMessage());
            closeSocket(listener);
            return false;
        }

        sockaddr_in bound_address{};
        int bound_length = sizeof(bound_address);
        if (getsockname(listener, reinterpret_cast<sockaddr*>(&bound_address), &bound_length) != 0) {
            setLastError("failed to query bound port");
            closeSocket(listener);
            return false;
        }

        {
            std::scoped_lock lock(mutex_);
            listener_ = listener;
            running_ = true;
        }

        if (actual_port != nullptr) {
            *actual_port = ntohs(bound_address.sin_port);
        }

        accept_thread_ = std::thread([this]() { acceptLoop(); });
        return true;
    }

    void stop() {
        SocketHandle listener_to_close = kInvalidSocket;
        {
            std::scoped_lock lock(mutex_);
            if (!running_) {
                return;
            }
            running_ = false;
            listener_to_close = listener_;
            listener_ = kInvalidSocket;
        }

        closeSocket(listener_to_close);

        if (accept_thread_.joinable()) {
            accept_thread_.join();
        }

        std::vector<std::thread> workers;
        {
            std::scoped_lock lock(mutex_);
            workers.swap(worker_threads_);
        }
        for (auto& worker : workers) {
            if (worker.joinable()) {
                worker.join();
            }
        }
    }

    int getLastError(char* buffer, int buffer_size) {
        std::string value;
        {
            std::scoped_lock lock(mutex_);
            value = last_error_;
        }
        if (buffer != nullptr && buffer_size > 0) {
            const auto copy_size = std::min<std::size_t>(value.size(), static_cast<std::size_t>(buffer_size - 1));
            std::memcpy(buffer, value.data(), copy_size);
            buffer[copy_size] = '\0';
        }
        return static_cast<int>(value.size());
    }

    ~FlashProxyCore() {
        stop();
    }

private:
    void setLastError(const std::string& value) {
        std::scoped_lock lock(mutex_);
        last_error_ = value;
        native_core::setLastError(value);
    }

    void acceptLoop() {
        while (isRunning()) {
            sockaddr_storage client_address{};
            int client_length = sizeof(client_address);
            SocketHandle client = accept(listener_, reinterpret_cast<sockaddr*>(&client_address), &client_length);
            if (client == kInvalidSocket) {
                if (isRunning()) {
                    setLastError("accept failed: " + lastSocketErrorMessage());
                }
                break;
            }

            std::scoped_lock lock(mutex_);
            worker_threads_.emplace_back([this, client]() {
                handleClient(client);
                closeSocket(client);
            });
        }
    }

    bool isRunning() {
        std::scoped_lock lock(mutex_);
        return running_;
    }

    void handleClient(SocketHandle client) {
        std::string raw_request;
        if (!recvToDelimiter(client, raw_request, "\r\n\r\n", 65536)) {
            return;
        }

        ParsedRequest request;
        std::string error;
        if (!parseRequest(raw_request, request, error)) {
            sendSimpleError(client, 400, "Bad Request", error);
            return;
        }

        if (toLower(request.method) == "connect") {
            sendSimpleError(client, 501, "Not Implemented", "CONNECT is not supported");
            return;
        }

        const int declared_length = contentLengthFromHeaders(request.headers);
        if (declared_length < 0) {
            sendSimpleError(client, 400, "Bad Request", "invalid Content-Length");
            return;
        }
        if (declared_length > static_cast<int>(request.body.size())) {
            std::string remainder = request.body;
            if (!recvExact(client, remainder, static_cast<std::size_t>(declared_length))) {
                return;
            }
            request.body.swap(remainder);
        } else if (declared_length >= 0) {
            request.body.resize(static_cast<std::size_t>(declared_length));
        }

        ParsedUrl target_url;
        HostPort host_port;
        if (!parseAbsoluteUrl(request.raw_target, target_url)) {
            std::string host_header;
            for (const auto& header : request.headers) {
                if (toLower(header.first) == "host") {
                    host_header = header.second;
                    break;
                }
            }
            if (!parseHostPort(host_header, host_port)) {
                sendSimpleError(client, 400, "Bad Request", "missing Host header");
                return;
            }
            target_url.scheme = "http";
            target_url.host = host_port.host;
            target_url.port = host_port.port;
            target_url.path_and_query = request.raw_target.empty() ? "/" : request.raw_target;
        }

        if (tryServeLocalFile(client, target_url)) {
            return;
        }

        forwardRequest(client, request, target_url);
    }

    bool tryServeLocalFile(SocketHandle client, const ParsedUrl& target_url) {
        std::filesystem::path cache_root;
        std::vector<std::string> mapping_hosts;
        std::vector<std::string> mapping_url_keywords;
        {
            std::scoped_lock lock(mutex_);
            cache_root = cache_root_;
            mapping_hosts = mapping_hosts_;
            mapping_url_keywords = mapping_url_keywords_;
        }

        const auto host_lower = toLower(target_url.host);
        const auto url_lower = toLower(buildAbsoluteUrl(target_url));
        bool matches = false;
        for (const auto& mapping : mapping_hosts) {
            if (!mapping.empty() && host_lower.find(mapping) != std::string::npos) {
                matches = true;
                break;
            }
        }
        if (!matches) {
            for (const auto& keyword : mapping_url_keywords) {
                if (!keyword.empty() && url_lower.find(keyword) != std::string::npos) {
                    matches = true;
                    break;
                }
            }
        }
        if (!matches) {
            return false;
        }

        const auto relative = sanitizeRelativePath(target_url.path_and_query);
        if (relative.empty()) {
            return false;
        }

        const auto local_file = cache_root / std::filesystem::path(relative);
        if (!std::filesystem::exists(local_file) || !std::filesystem::is_regular_file(local_file)) {
            return false;
        }

        std::ifstream input(local_file, std::ios::binary);
        if (!input) {
            return false;
        }

        std::string content((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
        std::ostringstream response;
        response << "HTTP/1.1 200 OK\r\n";
        response << "Content-Type: " << guessMimeType(local_file) << "\r\n";
        response << "Content-Length: " << content.size() << "\r\n";
        response << "Connection: close\r\n";
        response << "Proxy-Connection: close\r\n\r\n";

        return sendAll(client, response.str()) && sendAll(client, content);
    }

    void forwardRequest(SocketHandle client, const ParsedRequest& request, const ParsedUrl& target_url) {
        std::string upstream_proxy;
        {
            std::scoped_lock lock(mutex_);
            upstream_proxy = upstream_proxy_;
        }

        HostPort destination;
        bool use_upstream = false;
        if (!trim(upstream_proxy).empty()) {
            if (!parseHostPort(upstream_proxy, destination)) {
                sendSimpleError(client, 500, "Proxy Error", "invalid upstream proxy");
                return;
            }
            use_upstream = true;
        } else {
            destination.host = target_url.host;
            destination.port = target_url.port;
        }

        std::string error;
        const SocketHandle upstream = connectTcp(destination.host, destination.port, error);
        if (upstream == kInvalidSocket) {
            sendSimpleError(client, 502, "Bad Gateway", error);
            return;
        }

        std::ostringstream outbound;
        const auto outbound_target = use_upstream
            ? buildAbsoluteUrl(target_url)
            : target_url.path_and_query;

        outbound << request.method << ' ' << outbound_target << ' ' << request.http_version << "\r\n";
        bool has_host = false;
        for (const auto& header : request.headers) {
            const auto lower_name = toLower(header.first);
            if (lower_name == "proxy-connection" || lower_name == "connection") {
                continue;
            }
            if (lower_name == "host") {
                has_host = true;
            }
            outbound << header.first << ": " << header.second << "\r\n";
        }
        if (!has_host) {
            outbound << "Host: " << target_url.host;
            if (target_url.port != 80) {
                outbound << ':' << target_url.port;
            }
            outbound << "\r\n";
        }
        outbound << "Connection: close\r\n\r\n";

        const auto header_blob = outbound.str();
        const bool sent =
            sendAll(upstream, header_blob) &&
            (request.body.empty() || sendAll(upstream, request.body));

        if (!sent) {
            closeSocket(upstream);
            sendSimpleError(client, 502, "Bad Gateway", "failed to send upstream request");
            return;
        }

        char buffer[8192];
        while (true) {
            const int received = recv(upstream, buffer, static_cast<int>(sizeof(buffer)), 0);
            if (received <= 0) {
                break;
            }
            if (!sendAll(client, buffer, static_cast<std::size_t>(received))) {
                break;
            }
        }

        closeSocket(upstream);
    }

    std::string buildAbsoluteUrl(const ParsedUrl& target_url) const {
        std::ostringstream url;
        url << "http://" << target_url.host;
        if (target_url.port != 80) {
            url << ':' << target_url.port;
        }
        url << target_url.path_and_query;
        return url.str();
    }

    void sendSimpleError(SocketHandle client, int status_code, const std::string& reason, const std::string& message) {
        std::ostringstream response;
        response << "HTTP/1.1 " << status_code << ' ' << reason << "\r\n";
        response << "Content-Type: text/plain; charset=utf-8\r\n";
        response << "Content-Length: " << message.size() << "\r\n";
        response << "Connection: close\r\n\r\n";
        response << message;
        sendAll(client, response.str());
    }

    std::mutex mutex_;
    std::filesystem::path cache_root_;
    std::vector<std::string> mapping_hosts_;
    std::vector<std::string> mapping_url_keywords_;
    std::string upstream_proxy_;
    std::string last_error_;
    bool running_ = false;
    SocketHandle listener_ = kInvalidSocket;
    std::thread accept_thread_;
    std::vector<std::thread> worker_threads_;
};

}  // namespace

struct FlashProxyHandle {
    FlashProxyCore core;
};

FlashProxyHandle* FLASH_PROXY_CALL flash_proxy_create() {
    return new FlashProxyHandle();
}

void FLASH_PROXY_CALL flash_proxy_destroy(FlashProxyHandle* handle) {
    delete handle;
}

int FLASH_PROXY_CALL flash_proxy_set_cache_root(FlashProxyHandle* handle, const wchar_t* path) {
    if (handle == nullptr || path == nullptr) {
        return 0;
    }
    return handle->core.setCacheRoot(std::filesystem::path(path)) ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_clear_mapping_hosts(FlashProxyHandle* handle) {
    if (handle == nullptr) {
        return 0;
    }
    return handle->core.clearMappingHosts() ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_add_mapping_host(FlashProxyHandle* handle, const wchar_t* host) {
    if (handle == nullptr || host == nullptr) {
        return 0;
    }
    return handle->core.addMappingHost(host) ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_clear_mapping_url_keywords(FlashProxyHandle* handle) {
    if (handle == nullptr) {
        return 0;
    }
    return handle->core.clearMappingUrlKeywords() ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_add_mapping_url_keyword(FlashProxyHandle* handle, const wchar_t* value) {
    if (handle == nullptr || value == nullptr) {
        return 0;
    }
    return handle->core.addMappingUrlKeyword(value) ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_set_upstream_proxy(FlashProxyHandle* handle, const wchar_t* proxy) {
    if (handle == nullptr) {
        return 0;
    }
    return handle->core.setUpstreamProxy(proxy != nullptr ? proxy : L"") ? 1 : 0;
}

int FLASH_PROXY_CALL flash_proxy_start(FlashProxyHandle* handle, int preferred_port, int* actual_port) {
    if (handle == nullptr) {
        return 0;
    }
    return handle->core.start(preferred_port, actual_port) ? 1 : 0;
}

void FLASH_PROXY_CALL flash_proxy_stop(FlashProxyHandle* handle) {
    if (handle != nullptr) {
        handle->core.stop();
    }
}

int FLASH_PROXY_CALL flash_proxy_get_last_error(FlashProxyHandle* handle, char* buffer, int buffer_size) {
    if (handle == nullptr) {
        return native_core::copyLastError(buffer, buffer_size);
    }
    return handle->core.getLastError(buffer, buffer_size);
}
