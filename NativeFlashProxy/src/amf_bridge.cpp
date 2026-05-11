#include "flash_proxy_core.h"

#include "native_error.h"
#include "simple_json.h"

#include "../third_party/amf-cpp/src/amfpacket.hpp"
#include "../third_party/amf-cpp/src/serializationcontext.hpp"
#include "../third_party/amf-cpp/src/types/amfarray.hpp"
#include "../third_party/amf-cpp/src/types/amfbool.hpp"
#include "../third_party/amf-cpp/src/types/amfbytearray.hpp"
#include "../third_party/amf-cpp/src/types/amfdate.hpp"
#include "../third_party/amf-cpp/src/types/amfdictionary.hpp"
#include "../third_party/amf-cpp/src/types/amfdouble.hpp"
#include "../third_party/amf-cpp/src/types/amfinteger.hpp"
#include "../third_party/amf-cpp/src/types/amfnull.hpp"
#include "../third_party/amf-cpp/src/types/amfobject.hpp"
#include "../third_party/amf-cpp/src/types/amfstring.hpp"
#include "../third_party/amf-cpp/src/types/amfundefined.hpp"
#include "../third_party/amf-cpp/src/types/amfvector.hpp"
#include "../third_party/amf-cpp/src/types/amfxml.hpp"
#include "../third_party/amf-cpp/src/types/amfxmldocument.hpp"

#include <algorithm>
#include <cerrno>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
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

using JsonValue = simple_json::Value;
using JsonArray = JsonValue::Array;
using JsonObject = JsonValue::Object;

#ifdef _WIN32
using SocketHandle = SOCKET;
constexpr SocketHandle kInvalidSocket = INVALID_SOCKET;
#else
using SocketHandle = int;
constexpr SocketHandle kInvalidSocket = -1;
#endif

struct ParsedUrl {
    std::string scheme;
    std::string host;
    std::string path_and_query;
    int port = 80;
};

struct HttpResponse {
    int status_code = 0;
    std::string reason;
    std::map<std::string, std::string> headers;
    std::vector<std::uint8_t> body;
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

#ifdef _WIN32
SocketHandle connectTcpLegacyIpv4(const std::string& host, int port) {
    hostent* host_entry = gethostbyname(host.c_str());
    if (host_entry == nullptr || host_entry->h_addr_list == nullptr || host_entry->h_addr_list[0] == nullptr) {
        unsigned long address = inet_addr(host.c_str());
        if (address == INADDR_NONE) {
            return kInvalidSocket;
        }

        sockaddr_in endpoint{};
        endpoint.sin_family = AF_INET;
        endpoint.sin_port = htons(static_cast<unsigned short>(port));
        endpoint.sin_addr.s_addr = address;

        SocketHandle candidate = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (candidate == kInvalidSocket) {
            return kInvalidSocket;
        }

        if (connect(candidate, reinterpret_cast<const sockaddr*>(&endpoint), sizeof(endpoint)) == 0) {
            return candidate;
        }

        closeSocket(candidate);
        return kInvalidSocket;
    }

    for (char** current = host_entry->h_addr_list; *current != nullptr; ++current) {
        sockaddr_in endpoint{};
        endpoint.sin_family = AF_INET;
        endpoint.sin_port = htons(static_cast<unsigned short>(port));
        std::memcpy(&endpoint.sin_addr, *current, sizeof(endpoint.sin_addr));

        SocketHandle candidate = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (candidate == kInvalidSocket) {
            continue;
        }

        if (connect(candidate, reinterpret_cast<const sockaddr*>(&endpoint), sizeof(endpoint)) == 0) {
            return candidate;
        }

        closeSocket(candidate);
    }

    return kInvalidSocket;
}
#endif

SocketHandle connectTcp(const std::string& host, int port) {
#ifdef _WIN32
    std::call_once(g_socket_init_once, []() {
        WSADATA data{};
        WSAStartup(MAKEWORD(2, 2), &data);
    });
#endif

    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* result = nullptr;
    const std::string port_text = std::to_string(port);
    if (getaddrinfo(host.c_str(), port_text.c_str(), &hints, &result) != 0) {
#ifdef _WIN32
        return connectTcpLegacyIpv4(host, port);
#else
        return kInvalidSocket;
#endif
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
    return connected;
}

bool sendAll(SocketHandle socket_handle, const std::uint8_t* data, std::size_t size) {
    std::size_t sent_total = 0;
    while (sent_total < size) {
        const int sent_now = send(socket_handle, reinterpret_cast<const char*>(data + sent_total), static_cast<int>(size - sent_total), 0);
        if (sent_now <= 0) {
            return false;
        }
        sent_total += static_cast<std::size_t>(sent_now);
    }
    return true;
}

bool sendAll(SocketHandle socket_handle, const std::string& data) {
    return sendAll(socket_handle, reinterpret_cast<const std::uint8_t*>(data.data()), data.size());
}

std::string readUntilHeadersComplete(SocketHandle socket_handle) {
    std::string buffer;
    char chunk[4096];
    while (buffer.find("\r\n\r\n") == std::string::npos) {
        const int received = recv(socket_handle, chunk, static_cast<int>(sizeof(chunk)), 0);
        if (received <= 0) {
            throw std::runtime_error("Failed to read HTTP response headers");
        }
        buffer.append(chunk, chunk + received);
        if (buffer.size() > 1024 * 1024) {
            throw std::runtime_error("HTTP response headers too large");
        }
    }
    return buffer;
}

std::vector<std::uint8_t> decodeChunkedBody(const std::string& initial_body, SocketHandle socket_handle) {
    std::string data = initial_body;
    char chunk[4096];

    auto ensureData = [&](std::size_t size_needed) {
        while (data.size() < size_needed) {
            const int received = recv(socket_handle, chunk, static_cast<int>(sizeof(chunk)), 0);
            if (received <= 0) {
                throw std::runtime_error("Unexpected end of chunked HTTP response");
            }
            data.append(chunk, chunk + received);
        }
    };

    std::vector<std::uint8_t> decoded;
    std::size_t offset = 0;
    while (true) {
        while (true) {
            const auto line_end = data.find("\r\n", offset);
            if (line_end != std::string::npos) {
                const auto size_text = data.substr(offset, line_end - offset);
                const auto semicolon = size_text.find(';');
                const std::string hex_size = size_text.substr(0, semicolon);
                const std::size_t chunk_size = static_cast<std::size_t>(std::stoul(hex_size, nullptr, 16));
                offset = line_end + 2;
                ensureData(offset + chunk_size + 2);
                if (chunk_size == 0) {
                    return decoded;
                }
                decoded.insert(decoded.end(), data.begin() + static_cast<std::ptrdiff_t>(offset), data.begin() + static_cast<std::ptrdiff_t>(offset + chunk_size));
                offset += chunk_size + 2;
                break;
            }
            const int received = recv(socket_handle, chunk, static_cast<int>(sizeof(chunk)), 0);
            if (received <= 0) {
                throw std::runtime_error("Unexpected end of chunked HTTP response");
            }
            data.append(chunk, chunk + received);
        }
    }
}

ParsedUrl parseAbsoluteUrl(const std::string& url) {
    const auto scheme_sep = url.find("://");
    if (scheme_sep == std::string::npos) {
        throw std::runtime_error("URL must be absolute");
    }

    ParsedUrl parsed;
    parsed.scheme = toLower(url.substr(0, scheme_sep));
    if (parsed.scheme != "http") {
        throw std::runtime_error("Only http:// AMF endpoints are supported right now");
    }

    const auto authority_start = scheme_sep + 3;
    const auto path_start = url.find('/', authority_start);
    std::string authority = url.substr(authority_start, path_start == std::string::npos ? std::string::npos : path_start - authority_start);
    parsed.path_and_query = path_start == std::string::npos ? "/" : url.substr(path_start);

    const auto colon = authority.rfind(':');
    if (colon != std::string::npos) {
        parsed.host = authority.substr(0, colon);
        parsed.port = std::stoi(authority.substr(colon + 1));
    } else {
        parsed.host = authority;
        parsed.port = 80;
    }

    if (parsed.host.empty()) {
        throw std::runtime_error("URL host is empty");
    }
    return parsed;
}

HttpResponse httpPostBinary(
    const std::string& url,
    const std::map<std::string, std::string>& headers,
    const std::vector<std::uint8_t>& body) {
    const ParsedUrl parsed = parseAbsoluteUrl(url);
    SocketHandle socket_handle = connectTcp(parsed.host, parsed.port);
    if (socket_handle == kInvalidSocket) {
        throw std::runtime_error("Failed to connect to " + parsed.host + ":" + std::to_string(parsed.port) + " (" + lastSocketErrorMessage() + ")");
    }

    std::ostringstream request;
    request << "POST " << parsed.path_and_query << " HTTP/1.1\r\n";
    request << "Host: " << parsed.host;
    if (parsed.port != 80) {
        request << ':' << parsed.port;
    }
    request << "\r\n";

    bool has_content_type = false;
    bool has_content_length = false;
    bool has_connection = false;
    bool has_accept_encoding = false;
    for (const auto& header : headers) {
        const auto lower_name = toLower(header.first);
        if (lower_name == "host") {
            continue;
        }
        if (lower_name == "content-type") has_content_type = true;
        if (lower_name == "content-length") has_content_length = true;
        if (lower_name == "connection") has_connection = true;
        if (lower_name == "accept-encoding") has_accept_encoding = true;
        request << header.first << ": " << header.second << "\r\n";
    }

    if (!has_content_type) {
        request << "Content-Type: application/x-amf\r\n";
    }
    if (!has_content_length) {
        request << "Content-Length: " << body.size() << "\r\n";
    }
    if (!has_accept_encoding) {
        request << "Accept-Encoding: identity\r\n";
    }
    if (!has_connection) {
        request << "Connection: close\r\n";
    }
    request << "\r\n";

    const auto request_header = request.str();
    if (!sendAll(socket_handle, request_header) || (!body.empty() && !sendAll(socket_handle, body.data(), body.size()))) {
        closeSocket(socket_handle);
        throw std::runtime_error("Failed to send AMF HTTP request");
    }

    std::string raw_response = readUntilHeadersComplete(socket_handle);
    const auto header_end = raw_response.find("\r\n\r\n");
    const std::string header_text = raw_response.substr(0, header_end);
    const std::string initial_body = raw_response.substr(header_end + 4);

    std::istringstream header_stream(header_text);
    std::string status_line;
    std::getline(header_stream, status_line);
    if (!status_line.empty() && status_line.back() == '\r') {
        status_line.pop_back();
    }
    std::istringstream status_stream(status_line);
    std::string http_version;
    HttpResponse response;
    status_stream >> http_version >> response.status_code;
    std::getline(status_stream, response.reason);
    response.reason = trim(response.reason);

    std::string line;
    while (std::getline(header_stream, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        const auto colon = line.find(':');
        if (colon == std::string::npos) {
            continue;
        }
        response.headers.emplace(trim(line.substr(0, colon)), trim(line.substr(colon + 1)));
    }

    const auto transfer_encoding_it = std::find_if(response.headers.begin(), response.headers.end(), [](const auto& entry) {
        return toLower(entry.first) == "transfer-encoding";
    });
    if (transfer_encoding_it != response.headers.end() && toLower(transfer_encoding_it->second).find("chunked") != std::string::npos) {
        response.body = decodeChunkedBody(initial_body, socket_handle);
    } else {
        std::size_t content_length = 0;
        bool has_content_length_header = false;
        const auto content_length_it = std::find_if(response.headers.begin(), response.headers.end(), [](const auto& entry) {
            return toLower(entry.first) == "content-length";
        });
        if (content_length_it != response.headers.end()) {
            has_content_length_header = true;
            content_length = static_cast<std::size_t>(std::stoull(content_length_it->second));
        }

        response.body.assign(initial_body.begin(), initial_body.end());
        char chunk[4096];
        if (has_content_length_header) {
            while (response.body.size() < content_length) {
                const int received = recv(socket_handle, chunk, static_cast<int>(sizeof(chunk)), 0);
                if (received <= 0) {
                    closeSocket(socket_handle);
                    throw std::runtime_error("Unexpected end of HTTP response body");
                }
                response.body.insert(response.body.end(), chunk, chunk + received);
            }
            response.body.resize(content_length);
        } else {
            while (true) {
                const int received = recv(socket_handle, chunk, static_cast<int>(sizeof(chunk)), 0);
                if (received <= 0) {
                    break;
                }
                response.body.insert(response.body.end(), chunk, chunk + received);
            }
        }
    }

    closeSocket(socket_handle);
    return response;
}

const JsonValue* findField(const JsonObject& object, const std::string& key) {
    const auto it = object.find(key);
    return it == object.end() ? nullptr : &it->second;
}

std::string requireString(const JsonObject& object, const std::string& key) {
    const JsonValue* value = findField(object, key);
    if (value == nullptr || !value->isString()) {
        throw std::runtime_error("Field '" + key + "' must be a string");
    }
    return value->asString();
}

bool getBoolOrDefault(const JsonObject& object, const std::string& key, bool default_value) {
    const JsonValue* value = findField(object, key);
    if (value == nullptr) {
        return default_value;
    }
    if (!value->isBool()) {
        throw std::runtime_error("Field '" + key + "' must be a boolean");
    }
    return value->asBool();
}

std::int64_t requireInt(const JsonObject& object, const std::string& key) {
    const JsonValue* value = findField(object, key);
    if (value == nullptr || !value->isNumber()) {
        throw std::runtime_error("Field '" + key + "' must be numeric");
    }
    return value->isInt() ? value->asInt() : static_cast<std::int64_t>(value->asDouble());
}

std::string jsonScalarToString(const JsonValue& value) {
    if (value.isString()) return value.asString();
    if (value.isBool()) return value.asBool() ? "true" : "false";
    if (value.isInt()) return std::to_string(value.asInt());
    if (value.isDouble()) {
        std::ostringstream stream;
        stream << value.asDouble();
        return stream.str();
    }
    if (value.isNull()) return "null";
    throw std::runtime_error("Header values must be scalar");
}

std::string base64Encode(const std::vector<std::uint8_t>& data) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string encoded;
    encoded.reserve(((data.size() + 2) / 3) * 4);

    for (std::size_t index = 0; index < data.size(); index += 3) {
        const std::uint32_t chunk =
            (static_cast<std::uint32_t>(data[index]) << 16) |
            (static_cast<std::uint32_t>(index + 1 < data.size() ? data[index + 1] : 0) << 8) |
            static_cast<std::uint32_t>(index + 2 < data.size() ? data[index + 2] : 0);

        encoded.push_back(alphabet[(chunk >> 18) & 0x3F]);
        encoded.push_back(alphabet[(chunk >> 12) & 0x3F]);
        encoded.push_back(index + 1 < data.size() ? alphabet[(chunk >> 6) & 0x3F] : '=');
        encoded.push_back(index + 2 < data.size() ? alphabet[chunk & 0x3F] : '=');
    }

    return encoded;
}

std::vector<std::uint8_t> base64Decode(const std::string& text) {
    static const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::vector<std::uint8_t> decoded;
    std::uint32_t accumulator = 0;
    int bits = 0;

    for (char ch : text) {
        if (std::isspace(static_cast<unsigned char>(ch)) != 0) {
            continue;
        }
        if (ch == '=') {
            break;
        }
        const auto position = alphabet.find(ch);
        if (position == std::string::npos) {
            throw std::runtime_error("Invalid base64 text");
        }
        accumulator = (accumulator << 6) | static_cast<std::uint32_t>(position);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            decoded.push_back(static_cast<std::uint8_t>((accumulator >> bits) & 0xFF));
        }
    }
    return decoded;
}

amf::AmfItemPtr jsonToAmf(const JsonValue& value);
JsonValue amfToJson(const amf::AmfItemPtr& value);

amf::AmfItemPtr encodeSpecialObject(const JsonObject& object) {
    const std::string type = requireString(object, "$amfType");

    if (type == "undefined") {
        return amf::AmfItemPtr(new amf::AmfUndefined());
    }
    if (type == "bytearray") {
        return amf::AmfItemPtr(new amf::AmfByteArray(base64Decode(requireString(object, "base64"))));
    }
    if (type == "date") {
        return amf::AmfItemPtr(new amf::AmfDate(requireInt(object, "milliseconds")));
    }
    if (type == "xml") {
        return amf::AmfItemPtr(new amf::AmfXml(requireString(object, "value")));
    }
    if (type == "xmldocument") {
        return amf::AmfItemPtr(new amf::AmfXmlDocument(requireString(object, "value")));
    }
    if (type == "array") {
        amf::AmfArray array;
        if (const JsonValue* dense = findField(object, "dense")) {
            if (!dense->isArray()) {
                throw std::runtime_error("'dense' must be an array");
            }
            for (const auto& item : dense->asArray()) {
                array.dense.push_back(jsonToAmf(item));
            }
        }
        if (const JsonValue* assoc = findField(object, "assoc")) {
            if (!assoc->isObject()) {
                throw std::runtime_error("'assoc' must be an object");
            }
            for (const auto& entry : assoc->asObject()) {
                array.associative[entry.first] = jsonToAmf(entry.second);
            }
        }
        return amf::AmfItemPtr(new amf::AmfArray(array));
    }
    if (type == "object") {
        amf::AmfObject result(
            findField(object, "className") != nullptr ? requireString(object, "className") : "",
            getBoolOrDefault(object, "dynamic", true),
            getBoolOrDefault(object, "externalizable", false));

        if (const JsonValue* sealed = findField(object, "sealed")) {
            if (!sealed->isObject()) {
                throw std::runtime_error("'sealed' must be an object");
            }
            for (const auto& entry : sealed->asObject()) {
                result.addSealedProperty(entry.first, jsonToAmf(entry.second));
            }
        }
        if (const JsonValue* dynamic_props = findField(object, "dynamicProperties")) {
            if (!dynamic_props->isObject()) {
                throw std::runtime_error("'dynamicProperties' must be an object");
            }
            for (const auto& entry : dynamic_props->asObject()) {
                result.addDynamicProperty(entry.first, jsonToAmf(entry.second));
            }
        }
        return amf::AmfItemPtr(new amf::AmfObject(result));
    }
    if (type == "vector-int") {
        std::vector<int> values;
        for (const auto& item : findField(object, "values")->asArray()) {
            values.push_back(static_cast<int>(item.isInt() ? item.asInt() : item.asDouble()));
        }
        return amf::AmfItemPtr(new amf::AmfVector<int>(values, getBoolOrDefault(object, "fixed", false)));
    }
    if (type == "vector-uint") {
        std::vector<unsigned int> values;
        for (const auto& item : findField(object, "values")->asArray()) {
            values.push_back(static_cast<unsigned int>(item.isInt() ? item.asInt() : item.asDouble()));
        }
        return amf::AmfItemPtr(new amf::AmfVector<unsigned int>(values, getBoolOrDefault(object, "fixed", false)));
    }
    if (type == "vector-double") {
        std::vector<double> values;
        for (const auto& item : findField(object, "values")->asArray()) {
            values.push_back(item.asDouble());
        }
        return amf::AmfItemPtr(new amf::AmfVector<double>(values, getBoolOrDefault(object, "fixed", false)));
    }
    if (type == "vector-object") {
        const JsonValue* values_json = findField(object, "values");
        if (values_json == nullptr || !values_json->isArray()) {
            throw std::runtime_error("'values' must be an array");
        }
        amf::AmfVector<amf::AmfItem> vector(requireString(object, "className"), getBoolOrDefault(object, "fixed", false));
        for (const auto& item : values_json->asArray()) {
            vector.values.push_back(jsonToAmf(item));
        }
        return amf::AmfItemPtr(new amf::AmfVector<amf::AmfItem>(vector));
    }
    if (type == "dictionary") {
        amf::AmfDictionary dictionary(getBoolOrDefault(object, "asString", false), getBoolOrDefault(object, "weak", false));
        const JsonValue* entries = findField(object, "entries");
        if (entries == nullptr || !entries->isArray()) {
            throw std::runtime_error("'entries' must be an array");
        }
        for (const auto& entry : entries->asArray()) {
            if (!entry.isObject()) {
                throw std::runtime_error("Dictionary entry must be an object");
            }
            const JsonObject& entry_object = entry.asObject();
            const JsonValue* key = findField(entry_object, "key");
            const JsonValue* mapped = findField(entry_object, "value");
            if (key == nullptr || mapped == nullptr) {
                throw std::runtime_error("Dictionary entry must contain key and value");
            }
            dictionary.values[jsonToAmf(*key)] = jsonToAmf(*mapped);
        }
        return amf::AmfItemPtr(new amf::AmfDictionary(dictionary));
    }

    throw std::runtime_error("Unsupported $amfType: " + type);
}

amf::AmfItemPtr jsonToAmf(const JsonValue& value) {
    constexpr std::int64_t kAmfIntMin = -(1 << 28);
    constexpr std::int64_t kAmfIntMax = (1 << 28) - 1;

    if (value.isNull()) {
        return amf::AmfItemPtr(new amf::AmfNull());
    }
    if (value.isBool()) {
        return amf::AmfItemPtr(new amf::AmfBool(value.asBool()));
    }
    if (value.isInt()) {
        const auto number = value.asInt();
        if (number >= kAmfIntMin && number <= kAmfIntMax) {
            return amf::AmfItemPtr(new amf::AmfInteger(static_cast<int>(number)));
        }
        return amf::AmfItemPtr(new amf::AmfDouble(static_cast<double>(number)));
    }
    if (value.isDouble()) {
        return amf::AmfItemPtr(new amf::AmfDouble(value.asDouble()));
    }
    if (value.isString()) {
        return amf::AmfItemPtr(new amf::AmfString(value.asString()));
    }
    if (value.isArray()) {
        amf::AmfArray array;
        for (const auto& item : value.asArray()) {
            array.dense.push_back(jsonToAmf(item));
        }
        return amf::AmfItemPtr(new amf::AmfArray(array));
    }

    const JsonObject& object = value.asObject();
    if (findField(object, "$amfType") != nullptr) {
        return encodeSpecialObject(object);
    }

    amf::AmfObject result("", true, false);
    for (const auto& entry : object) {
        result.addDynamicProperty(entry.first, jsonToAmf(entry.second));
    }
    return amf::AmfItemPtr(new amf::AmfObject(result));
}

JsonValue amfToJson(const amf::AmfItemPtr& value) {
    if (value.asPtr<amf::AmfNull>() != nullptr) {
        return JsonValue(nullptr);
    }
    if (value.asPtr<amf::AmfUndefined>() != nullptr) {
        return JsonObject{ {"$amfType", "undefined"} };
    }
    if (const auto* bool_value = value.asPtr<amf::AmfBool>()) {
        return JsonValue(bool_value->value);
    }
    if (const auto* int_value = value.asPtr<amf::AmfInteger>()) {
        return JsonValue(static_cast<std::int64_t>(int_value->value));
    }
    if (const auto* double_value = value.asPtr<amf::AmfDouble>()) {
        return JsonValue(double_value->value);
    }
    if (const auto* string_value = value.asPtr<amf::AmfString>()) {
        return JsonValue(string_value->value);
    }
    if (const auto* xml_value = value.asPtr<amf::AmfXml>()) {
        return JsonObject{ {"$amfType", "xml"}, {"value", xml_value->value} };
    }
    if (const auto* xml_doc_value = value.asPtr<amf::AmfXmlDocument>()) {
        return JsonObject{ {"$amfType", "xmldocument"}, {"value", xml_doc_value->value} };
    }
    if (const auto* date_value = value.asPtr<amf::AmfDate>()) {
        return JsonObject{ {"$amfType", "date"}, {"milliseconds", JsonValue(static_cast<std::int64_t>(date_value->value))} };
    }
    if (const auto* byte_array = value.asPtr<amf::AmfByteArray>()) {
        return JsonObject{ {"$amfType", "bytearray"}, {"base64", base64Encode(byte_array->value)} };
    }
    if (const auto* array_value = value.asPtr<amf::AmfArray>()) {
        if (array_value->associative.empty()) {
            JsonArray dense;
            for (const auto& item : array_value->dense) {
                dense.push_back(amfToJson(item));
            }
            return JsonValue(std::move(dense));
        }
        JsonArray dense;
        JsonObject assoc;
        for (const auto& item : array_value->dense) {
            dense.push_back(amfToJson(item));
        }
        for (const auto& entry : array_value->associative) {
            assoc.emplace(entry.first, amfToJson(entry.second));
        }
        return JsonObject{
            {"$amfType", "array"},
            {"dense", JsonValue(std::move(dense))},
            {"assoc", JsonValue(std::move(assoc))}
        };
    }
    if (const auto* object_value = value.asPtr<amf::AmfObject>()) {
        const auto& traits = object_value->objectTraits();
        const bool can_flatten = traits.className.empty() && traits.dynamic && !traits.externalizable && object_value->sealedProperties.empty();
        if (can_flatten) {
            JsonObject object;
            for (const auto& entry : object_value->dynamicProperties) {
                object.emplace(entry.first, amfToJson(entry.second));
            }
            return JsonValue(std::move(object));
        }

        JsonObject sealed;
        JsonObject dynamic_props;
        for (const auto& name : traits.getUniqueAttributes()) {
            const auto it = object_value->sealedProperties.find(name);
            if (it != object_value->sealedProperties.end()) {
                sealed.emplace(name, amfToJson(it->second));
            }
        }
        for (const auto& entry : object_value->dynamicProperties) {
            dynamic_props.emplace(entry.first, amfToJson(entry.second));
        }
        return JsonObject{
            {"$amfType", "object"},
            {"className", traits.className},
            {"dynamic", traits.dynamic},
            {"externalizable", traits.externalizable},
            {"sealed", JsonValue(std::move(sealed))},
            {"dynamicProperties", JsonValue(std::move(dynamic_props))}
        };
    }
    if (const auto* vector_int = value.asPtr<amf::AmfVector<int>>()) {
        JsonArray values;
        for (int item : vector_int->values) values.emplace_back(static_cast<std::int64_t>(item));
        return JsonObject{ {"$amfType", "vector-int"}, {"fixed", vector_int->fixed}, {"values", JsonValue(std::move(values))} };
    }
    if (const auto* vector_uint = value.asPtr<amf::AmfVector<unsigned int>>()) {
        JsonArray values;
        for (unsigned int item : vector_uint->values) values.emplace_back(static_cast<std::int64_t>(item));
        return JsonObject{ {"$amfType", "vector-uint"}, {"fixed", vector_uint->fixed}, {"values", JsonValue(std::move(values))} };
    }
    if (const auto* vector_double = value.asPtr<amf::AmfVector<double>>()) {
        JsonArray values;
        for (double item : vector_double->values) values.emplace_back(item);
        return JsonObject{ {"$amfType", "vector-double"}, {"fixed", vector_double->fixed}, {"values", JsonValue(std::move(values))} };
    }
    if (const auto* vector_object = value.asPtr<amf::AmfVector<amf::AmfItem>>()) {
        JsonArray values;
        for (const auto& item : vector_object->values) values.push_back(amfToJson(item));
        return JsonObject{
            {"$amfType", "vector-object"},
            {"fixed", vector_object->fixed},
            {"className", vector_object->type},
            {"values", JsonValue(std::move(values))}
        };
    }
    if (const auto* dictionary = value.asPtr<amf::AmfDictionary>()) {
        JsonArray entries;
        for (const auto& entry : dictionary->values) {
            entries.emplace_back(JsonObject{
                {"key", amfToJson(entry.first)},
                {"value", amfToJson(entry.second)}
            });
        }
        return JsonObject{
            {"$amfType", "dictionary"},
            {"asString", dictionary->asString},
            {"weak", dictionary->weak},
            {"entries", JsonValue(std::move(entries))}
        };
    }

    throw std::runtime_error("Unsupported AMF type during JSON conversion");
}

amf::AmfPacket packetFromJson(const JsonObject& root) {
    amf::AmfPacket packet;

    if (const JsonValue* headers = findField(root, "headers")) {
        if (!headers->isArray()) {
            throw std::runtime_error("'headers' must be an array");
        }
        for (const auto& item : headers->asArray()) {
            if (!item.isObject()) {
                throw std::runtime_error("Packet header entry must be an object");
            }
            const JsonObject& entry = item.asObject();
            const JsonValue* value = findField(entry, "value");
            if (value == nullptr) {
                throw std::runtime_error("Packet header entry missing value");
            }
            packet.headers.emplace_back(
                requireString(entry, "name"),
                getBoolOrDefault(entry, "mustUnderstand", false),
                jsonToAmf(*value));
        }
    }

    const JsonValue* messages = findField(root, "messages");
    if (messages == nullptr || !messages->isArray()) {
        throw std::runtime_error("'messages' must be an array");
    }
    for (const auto& item : messages->asArray()) {
        if (!item.isObject()) {
            throw std::runtime_error("Packet message entry must be an object");
        }
        const JsonObject& entry = item.asObject();
        const JsonValue* value = findField(entry, "value");
        if (value == nullptr) {
            throw std::runtime_error("Packet message entry missing value");
        }
        packet.messages.emplace_back(
            requireString(entry, "target"),
            findField(entry, "response") != nullptr ? requireString(entry, "response") : "/1",
            jsonToAmf(*value));
    }

    return packet;
}

JsonValue packetToJson(const amf::AmfPacket& packet) {
    JsonArray headers;
    for (const auto& header : packet.headers) {
        headers.emplace_back(JsonObject{
            {"name", header.name},
            {"mustUnderstand", header.mustUnderstand},
            {"value", amfToJson(header.getValuePtr())}
        });
    }

    JsonArray messages;
    for (const auto& message : packet.messages) {
        messages.emplace_back(JsonObject{
            {"target", message.target},
            {"response", message.response},
            {"value", amfToJson(message.getValuePtr())}
        });
    }

    return JsonObject{
        {"headers", JsonValue(std::move(headers))},
        {"messages", JsonValue(std::move(messages))}
    };
}

char* allocateUtf8String(const std::string& text) {
    char* result = static_cast<char*>(std::malloc(text.size() + 1));
    if (result == nullptr) {
        throw std::bad_alloc();
    }
    std::memcpy(result, text.data(), text.size());
    result[text.size()] = '\0';
    return result;
}

unsigned char* allocateBinary(const std::vector<std::uint8_t>& data) {
    unsigned char* result = static_cast<unsigned char*>(std::malloc(data.size()));
    if (result == nullptr && !data.empty()) {
        throw std::bad_alloc();
    }
    if (!data.empty()) {
        std::memcpy(result, data.data(), data.size());
    }
    return result;
}

std::vector<std::uint8_t> encodePacketJsonInternal(const std::string& packet_json) {
    const JsonValue parsed = simple_json::parse(packet_json);
    if (!parsed.isObject()) {
        throw std::runtime_error("AMF packet JSON root must be an object");
    }
    amf::SerializationContext context;
    return packetFromJson(parsed.asObject()).serialize(context);
}

std::string decodePacketJsonInternal(const std::uint8_t* data, int data_size) {
    if (data == nullptr || data_size < 0) {
        throw std::runtime_error("Invalid AMF packet buffer");
    }
    amf::v8 bytes(data, data + data_size);
    auto it = bytes.cbegin();
    amf::SerializationContext context;
    const amf::AmfPacket packet = amf::AmfPacket::deserialize(it, bytes.cend(), context);
    return simple_json::stringify(packetToJson(packet));
}

std::map<std::string, std::string> headersFromJsonText(const char* headers_json_utf8) {
    std::map<std::string, std::string> headers;
    if (headers_json_utf8 == nullptr || std::strlen(headers_json_utf8) == 0) {
        return headers;
    }

    const JsonValue parsed = simple_json::parse(headers_json_utf8);
    if (!parsed.isObject()) {
        throw std::runtime_error("Headers JSON must be an object");
    }
    for (const auto& entry : parsed.asObject()) {
        headers.emplace(entry.first, jsonScalarToString(entry.second));
    }
    return headers;
}

}  // namespace

FLASH_PROXY_API void FLASH_PROXY_CALL flash_proxy_free_memory(void* ptr) {
    std::free(ptr);
}

FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_encode_packet_json(const char* packet_json_utf8, unsigned char** out_data, int* out_size) {
    try {
        if (packet_json_utf8 == nullptr || out_data == nullptr || out_size == nullptr) {
            throw std::runtime_error("flash_amf_encode_packet_json received null arguments");
        }
        const auto bytes = encodePacketJsonInternal(packet_json_utf8);
        *out_data = allocateBinary(bytes);
        *out_size = static_cast<int>(bytes.size());
        native_core::setLastError("");
        return 1;
    } catch (const std::exception& ex) {
        native_core::setLastError(ex.what());
        return 0;
    }
}

FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_decode_packet_json(const unsigned char* data, int data_size, char** out_json_utf8) {
    try {
        if (out_json_utf8 == nullptr) {
            throw std::runtime_error("flash_amf_decode_packet_json requires out_json_utf8");
        }
        const auto json = decodePacketJsonInternal(data, data_size);
        *out_json_utf8 = allocateUtf8String(json);
        native_core::setLastError("");
        return 1;
    } catch (const std::exception& ex) {
        native_core::setLastError(ex.what());
        return 0;
    }
}

FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_post_json(const char* url_utf8, const char* packet_json_utf8, const char* headers_json_utf8, char** out_response_json_utf8) {
    try {
        if (url_utf8 == nullptr || packet_json_utf8 == nullptr || out_response_json_utf8 == nullptr) {
            throw std::runtime_error("flash_amf_post_json received null arguments");
        }

        const auto payload = encodePacketJsonInternal(packet_json_utf8);
        auto headers = headersFromJsonText(headers_json_utf8);
        const HttpResponse response = httpPostBinary(url_utf8, headers, payload);

        JsonObject root;
        root.emplace("statusCode", static_cast<std::int64_t>(response.status_code));

        JsonObject header_object;
        for (const auto& entry : response.headers) {
            header_object.emplace(entry.first, entry.second);
        }
        root.emplace("headers", JsonValue(std::move(header_object)));

        try {
            root.emplace("packet", simple_json::parse(decodePacketJsonInternal(response.body.data(), static_cast<int>(response.body.size()))));
            root.emplace("amfDecoded", true);
        } catch (const std::exception& decode_error) {
            root.emplace("amfDecoded", false);
            root.emplace("decodeError", decode_error.what());
            root.emplace("rawBodyBase64", base64Encode(response.body));
        }

        *out_response_json_utf8 = allocateUtf8String(simple_json::stringify(JsonValue(std::move(root))));
        native_core::setLastError("");
        return 1;
    } catch (const std::exception& ex) {
        native_core::setLastError(ex.what());
        return 0;
    }
}

FLASH_PROXY_API int FLASH_PROXY_CALL flash_amf_post_pvzol_json(
    const char* url_utf8,
    const char* target_utf8,
    const char* body_json_utf8,
    const char* cookie_utf8,
    const char* referer_utf8,
    const char* extra_headers_json_utf8,
    char** out_response_json_utf8) {
    try {
        if (target_utf8 == nullptr || body_json_utf8 == nullptr || out_response_json_utf8 == nullptr) {
            throw std::runtime_error("flash_amf_post_pvzol_json received null arguments");
        }

        const std::string url = (url_utf8 == nullptr || std::strlen(url_utf8) == 0)
            ? "http://pvzol.org/pvz/amf/"
            : std::string(url_utf8);

        JsonObject packet;
        packet.emplace("headers", JsonArray{});
        packet.emplace("messages", JsonArray{
            JsonObject{
                {"target", std::string(target_utf8)},
                {"response", "/1"},
                {"value", simple_json::parse(body_json_utf8)}
            }
        });

        std::map<std::string, std::string> headers = {
            {"User-Agent", "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:54.0) Gecko/20100101 Firefox/54.0"},
            {"Accept", "*/*"},
            {"Referer", (referer_utf8 == nullptr || std::strlen(referer_utf8) == 0) ? "http://pvzol.org/youkia/main.swf" : std::string(referer_utf8)},
            {"Accept-Language", "zh-CN"},
            {"x-flash-version", "34,0,0,282"},
            {"Content-Type", "application/x-amf"},
            {"Accept-Encoding", "identity"},
            {"Pragma", "no-cache"},
            {"Connection", "close"}
        };
        if (cookie_utf8 != nullptr && std::strlen(cookie_utf8) > 0) {
            headers["Cookie"] = cookie_utf8;
        }
        for (const auto& entry : headersFromJsonText(extra_headers_json_utf8)) {
            headers[entry.first] = entry.second;
        }

        const auto payload = encodePacketJsonInternal(simple_json::stringify(JsonValue(std::move(packet))));
        const HttpResponse response = httpPostBinary(url, headers, payload);

        JsonObject root;
        root.emplace("statusCode", static_cast<std::int64_t>(response.status_code));

        JsonObject header_object;
        for (const auto& entry : response.headers) {
            header_object.emplace(entry.first, entry.second);
        }
        root.emplace("headers", JsonValue(std::move(header_object)));

        try {
            root.emplace("packet", simple_json::parse(decodePacketJsonInternal(response.body.data(), static_cast<int>(response.body.size()))));
            root.emplace("amfDecoded", true);
        } catch (const std::exception& decode_error) {
            root.emplace("amfDecoded", false);
            root.emplace("decodeError", decode_error.what());
            root.emplace("rawBodyBase64", base64Encode(response.body));
        }

        *out_response_json_utf8 = allocateUtf8String(simple_json::stringify(JsonValue(std::move(root))));
        native_core::setLastError("");
        return 1;
    } catch (const std::exception& ex) {
        native_core::setLastError(ex.what());
        return 0;
    }
}
