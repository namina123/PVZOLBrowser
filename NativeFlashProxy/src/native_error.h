#pragma once

#include <string>

namespace native_core {

void setLastError(const std::string& message);
std::string getLastError();
int copyLastError(char* buffer, int buffer_size);

}
