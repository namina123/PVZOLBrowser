#include "simple_json.h"

#include <cctype>
#include <cmath>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string_view>

namespace simple_json {
namespace {

class Parser {
public:
    explicit Parser(const std::string& text) : text_(text) {}

    Value parseValue() {
        skipWhitespace();
        if (eof()) {
            throw std::runtime_error("Unexpected end of JSON input");
        }

        const char ch = peek();
        if (ch == 'n') return parseNull();
        if (ch == 't' || ch == 'f') return parseBool();
        if (ch == '"') return Value(parseString());
        if (ch == '[') return parseArray();
        if (ch == '{') return parseObject();
        if (ch == '-' || std::isdigit(static_cast<unsigned char>(ch)) != 0) return parseNumber();

        throw std::runtime_error("Invalid JSON token");
    }

    void ensureFinished() {
        skipWhitespace();
        if (!eof()) {
            throw std::runtime_error("Trailing JSON content");
        }
    }

private:
    Value parseNull() {
        expect("null");
        return Value(nullptr);
    }

    Value parseBool() {
        if (match("true")) {
            return Value(true);
        }
        if (match("false")) {
            return Value(false);
        }
        throw std::runtime_error("Invalid JSON boolean");
    }

    Value parseNumber() {
        const auto start = pos_;
        if (peek() == '-') {
            advance();
        }
        if (eof()) {
            throw std::runtime_error("Invalid JSON number");
        }

        if (peek() == '0') {
            advance();
        } else if (std::isdigit(static_cast<unsigned char>(peek())) != 0) {
            while (!eof() && std::isdigit(static_cast<unsigned char>(peek())) != 0) {
                advance();
            }
        } else {
            throw std::runtime_error("Invalid JSON number");
        }

        bool is_double = false;
        if (!eof() && peek() == '.') {
            is_double = true;
            advance();
            if (eof() || std::isdigit(static_cast<unsigned char>(peek())) == 0) {
                throw std::runtime_error("Invalid JSON number");
            }
            while (!eof() && std::isdigit(static_cast<unsigned char>(peek())) != 0) {
                advance();
            }
        }

        if (!eof() && (peek() == 'e' || peek() == 'E')) {
            is_double = true;
            advance();
            if (!eof() && (peek() == '+' || peek() == '-')) {
                advance();
            }
            if (eof() || std::isdigit(static_cast<unsigned char>(peek())) == 0) {
                throw std::runtime_error("Invalid JSON exponent");
            }
            while (!eof() && std::isdigit(static_cast<unsigned char>(peek())) != 0) {
                advance();
            }
        }

        const auto token = text_.substr(start, pos_ - start);
        if (is_double) {
            return Value(std::stod(token));
        }
        return Value(std::stoll(token));
    }

    Value parseArray() {
        expect("[");
        Value::Array values;
        skipWhitespace();
        if (consume(']')) {
            return Value(std::move(values));
        }

        while (true) {
            values.push_back(parseValue());
            skipWhitespace();
            if (consume(']')) {
                break;
            }
            expect(",");
        }
        return Value(std::move(values));
    }

    Value parseObject() {
        expect("{");
        Value::Object values;
        skipWhitespace();
        if (consume('}')) {
            return Value(std::move(values));
        }

        while (true) {
            skipWhitespace();
            if (peek() != '"') {
                throw std::runtime_error("JSON object key must be a string");
            }
            const auto key = parseString();
            skipWhitespace();
            expect(":");
            values.emplace(key, parseValue());
            skipWhitespace();
            if (consume('}')) {
                break;
            }
            expect(",");
        }
        return Value(std::move(values));
    }

    std::string parseString() {
        expect("\"");
        std::string result;
        while (!eof()) {
            const char ch = advance();
            if (ch == '"') {
                return result;
            }
            if (ch != '\\') {
                result.push_back(ch);
                continue;
            }

            if (eof()) {
                throw std::runtime_error("Invalid JSON escape");
            }
            const char escaped = advance();
            switch (escaped) {
            case '"': result.push_back('"'); break;
            case '\\': result.push_back('\\'); break;
            case '/': result.push_back('/'); break;
            case 'b': result.push_back('\b'); break;
            case 'f': result.push_back('\f'); break;
            case 'n': result.push_back('\n'); break;
            case 'r': result.push_back('\r'); break;
            case 't': result.push_back('\t'); break;
            case 'u': appendUnicode(result, parseHex16()); break;
            default:
                throw std::runtime_error("Unsupported JSON escape");
            }
        }
        throw std::runtime_error("Unterminated JSON string");
    }

    std::uint32_t parseHex16() {
        std::uint32_t codepoint = 0;
        for (int i = 0; i < 4; ++i) {
            if (eof()) {
                throw std::runtime_error("Invalid JSON unicode escape");
            }
            codepoint <<= 4;
            const char ch = advance();
            if (ch >= '0' && ch <= '9') codepoint |= static_cast<std::uint32_t>(ch - '0');
            else if (ch >= 'a' && ch <= 'f') codepoint |= static_cast<std::uint32_t>(ch - 'a' + 10);
            else if (ch >= 'A' && ch <= 'F') codepoint |= static_cast<std::uint32_t>(ch - 'A' + 10);
            else throw std::runtime_error("Invalid JSON unicode escape");
        }
        return codepoint;
    }

    static void appendUnicode(std::string& output, std::uint32_t codepoint) {
        if (codepoint <= 0x7F) {
            output.push_back(static_cast<char>(codepoint));
        } else if (codepoint <= 0x7FF) {
            output.push_back(static_cast<char>(0xC0 | ((codepoint >> 6) & 0x1F)));
            output.push_back(static_cast<char>(0x80 | (codepoint & 0x3F)));
        } else {
            output.push_back(static_cast<char>(0xE0 | ((codepoint >> 12) & 0x0F)));
            output.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3F)));
            output.push_back(static_cast<char>(0x80 | (codepoint & 0x3F)));
        }
    }

    bool match(const char* token) {
        const std::string_view view(token);
        if (text_.compare(pos_, view.size(), view) == 0) {
            pos_ += view.size();
            return true;
        }
        return false;
    }

    void expect(const char* token) {
        if (!match(token)) {
            throw std::runtime_error(std::string("Expected token: ") + token);
        }
    }

    bool consume(char ch) {
        if (!eof() && peek() == ch) {
            ++pos_;
            return true;
        }
        return false;
    }

    char peek() const {
        return text_[pos_];
    }

    char advance() {
        return text_[pos_++];
    }

    bool eof() const {
        return pos_ >= text_.size();
    }

    void skipWhitespace() {
        while (!eof() && std::isspace(static_cast<unsigned char>(text_[pos_])) != 0) {
            ++pos_;
        }
    }

    const std::string& text_;
    std::size_t pos_ = 0;
};

std::string escapeString(const std::string& input) {
    std::ostringstream escaped;
    for (unsigned char ch : input) {
        switch (ch) {
        case '"': escaped << "\\\""; break;
        case '\\': escaped << "\\\\"; break;
        case '\b': escaped << "\\b"; break;
        case '\f': escaped << "\\f"; break;
        case '\n': escaped << "\\n"; break;
        case '\r': escaped << "\\r"; break;
        case '\t': escaped << "\\t"; break;
        default:
            if (ch < 0x20) {
                escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(ch) << std::dec;
            } else {
                escaped << static_cast<char>(ch);
            }
            break;
        }
    }
    return escaped.str();
}

void stringifyValue(const Value& value, std::ostringstream& output) {
    if (value.isNull()) {
        output << "null";
    } else if (value.isBool()) {
        output << (value.asBool() ? "true" : "false");
    } else if (value.isInt()) {
        output << value.asInt();
    } else if (value.isDouble()) {
        const double number = value.asDouble();
        if (!std::isfinite(number)) {
            throw std::runtime_error("Non-finite doubles cannot be stringified as JSON");
        }
        output << std::setprecision(17) << number;
    } else if (value.isString()) {
        output << '"' << escapeString(value.asString()) << '"';
    } else if (value.isArray()) {
        output << '[';
        bool first = true;
        for (const auto& item : value.asArray()) {
            if (!first) {
                output << ',';
            }
            first = false;
            stringifyValue(item, output);
        }
        output << ']';
    } else {
        output << '{';
        bool first = true;
        for (const auto& entry : value.asObject()) {
            if (!first) {
                output << ',';
            }
            first = false;
            output << '"' << escapeString(entry.first) << "\":";
            stringifyValue(entry.second, output);
        }
        output << '}';
    }
}

}  // namespace

Value::Value() : value_(nullptr) {}
Value::Value(std::nullptr_t) : value_(nullptr) {}
Value::Value(bool value) : value_(value) {}
Value::Value(std::int64_t value) : value_(value) {}
Value::Value(double value) : value_(value) {}
Value::Value(std::string value) : value_(std::move(value)) {}
Value::Value(const char* value) : value_(std::string(value == nullptr ? "" : value)) {}
Value::Value(Array value) : value_(std::move(value)) {}
Value::Value(Object value) : value_(std::move(value)) {}

bool Value::isNull() const { return std::holds_alternative<std::nullptr_t>(value_); }
bool Value::isBool() const { return std::holds_alternative<bool>(value_); }
bool Value::isInt() const { return std::holds_alternative<std::int64_t>(value_); }
bool Value::isDouble() const { return std::holds_alternative<double>(value_); }
bool Value::isString() const { return std::holds_alternative<std::string>(value_); }
bool Value::isArray() const { return std::holds_alternative<Array>(value_); }
bool Value::isObject() const { return std::holds_alternative<Object>(value_); }
bool Value::isNumber() const { return isInt() || isDouble(); }

bool Value::asBool() const { return std::get<bool>(value_); }
std::int64_t Value::asInt() const { return std::get<std::int64_t>(value_); }
double Value::asDouble() const { return isDouble() ? std::get<double>(value_) : static_cast<double>(std::get<std::int64_t>(value_)); }
const std::string& Value::asString() const { return std::get<std::string>(value_); }
const Value::Array& Value::asArray() const { return std::get<Array>(value_); }
const Value::Object& Value::asObject() const { return std::get<Object>(value_); }
Value::Array& Value::asArray() { return std::get<Array>(value_); }
Value::Object& Value::asObject() { return std::get<Object>(value_); }

Value parse(const std::string& text) {
    Parser parser(text);
    Value value = parser.parseValue();
    parser.ensureFinished();
    return value;
}

std::string stringify(const Value& value) {
    std::ostringstream output;
    stringifyValue(value, output);
    return output.str();
}

}
