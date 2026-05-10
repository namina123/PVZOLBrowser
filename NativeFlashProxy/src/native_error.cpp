#include "native_error.h"

#include <algorithm>
#include <cstring>
#include <mutex>

namespace native_core {
namespace {

std::mutex g_error_mutex;
std::string g_last_error;

}

void setLastError(const std::string& message) {
    std::scoped_lock lock(g_error_mutex);
    g_last_error = message;
}

std::string getLastError() {
    std::scoped_lock lock(g_error_mutex);
    return g_last_error;
}

int copyLastError(char* buffer, int buffer_size) {
    const auto value = getLastError();
    if (buffer != nullptr && buffer_size > 0) {
        const auto copy_size = std::min<std::size_t>(value.size(), static_cast<std::size_t>(buffer_size - 1));
        std::memcpy(buffer, value.data(), copy_size);
        buffer[copy_size] = '\0';
    }
    return static_cast<int>(value.size());
}

}
