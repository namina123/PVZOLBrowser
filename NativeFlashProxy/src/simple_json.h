#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <variant>
#include <vector>

namespace simple_json {

class Value {
public:
    using Array = std::vector<Value>;
    using Object = std::map<std::string, Value>;

    Value();
    Value(std::nullptr_t);
    Value(bool value);
    Value(std::int64_t value);
    Value(double value);
    Value(std::string value);
    Value(const char* value);
    Value(Array value);
    Value(Object value);

    bool isNull() const;
    bool isBool() const;
    bool isInt() const;
    bool isDouble() const;
    bool isString() const;
    bool isArray() const;
    bool isObject() const;
    bool isNumber() const;

    bool asBool() const;
    std::int64_t asInt() const;
    double asDouble() const;
    const std::string& asString() const;
    const Array& asArray() const;
    const Object& asObject() const;
    Array& asArray();
    Object& asObject();

private:
    using Storage = std::variant<std::nullptr_t, bool, std::int64_t, double, std::string, Array, Object>;
    Storage value_;
};

Value parse(const std::string& text);
std::string stringify(const Value& value);

}
