cmake_minimum_required(VERSION 3.20)

project(flash_proxy_core LANGUAGES CXX)

if(MSVC)
    cmake_policy(SET CMP0091 NEW)
endif()

add_library(flash_proxy_core SHARED
    src/amf_bridge.cpp
    src/flash_proxy_core.cpp
    src/native_error.cpp
    src/simple_json.cpp
    third_party/amf-cpp/src/amfpacket.cpp
    third_party/amf-cpp/src/deserializer.cpp
    third_party/amf-cpp/src/serializationcontext.cpp
    third_party/amf-cpp/src/serializer.cpp
    third_party/amf-cpp/src/types/amfarray.cpp
    third_party/amf-cpp/src/types/amfbool.cpp
    third_party/amf-cpp/src/types/amfbytearray.cpp
    third_party/amf-cpp/src/types/amfdate.cpp
    third_party/amf-cpp/src/types/amfdictionary.cpp
    third_party/amf-cpp/src/types/amfdouble.cpp
    third_party/amf-cpp/src/types/amfinteger.cpp
    third_party/amf-cpp/src/types/amfobject.cpp
    third_party/amf-cpp/src/types/amfstring.cpp
    third_party/amf-cpp/src/types/amfvector.cpp
    third_party/amf-cpp/src/types/amfxml.cpp
    third_party/amf-cpp/src/types/amfxmldocument.cpp
)

target_include_directories(flash_proxy_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${CMAKE_CURRENT_SOURCE_DIR}/third_party/amf-cpp/src
)

target_compile_features(flash_proxy_core PUBLIC cxx_std_17)
target_compile_definitions(flash_proxy_core PRIVATE FLASH_PROXY_CORE_EXPORTS)

if(MSVC)
    set_property(TARGET flash_proxy_core PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(flash_proxy_core PRIVATE /W4 /EHsc /utf-8)
else()
    target_compile_options(flash_proxy_core PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(WIN32)
    target_link_libraries(flash_proxy_core PRIVATE ws2_32)
endif()
