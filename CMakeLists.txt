cmake_minimum_required(VERSION 3.21)

project(PVZOLBrowser VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

find_package(Qt6 6.5 REQUIRED COMPONENTS Widgets Network)

if(WIN32)
    find_package(Qt6 REQUIRED COMPONENTS AxContainer WebEngineWidgets)
else()
    find_package(Qt6 REQUIRED COMPONENTS WebEngineWidgets)
endif()

qt_standard_project_setup()

qt_add_executable(PVZOLBrowser
    src/main.cpp
    src/MainWindow.cpp
    src/MainWindow.h
    src/BrowserTab.cpp
    src/BrowserTab.h
    src/BrowserView.cpp
    src/BrowserView.h
    src/network/ProxyManager.cpp
    src/network/ProxyManager.h
    src/browser/BrowserConfig.h
    src/browser/FlashRuntime.cpp
    src/browser/FlashRuntime.h
    src/browser/IBrowserBackend.h
    src/browser/BrowserBackendFactory.cpp
    src/browser/BrowserBackendFactory.h
    src/browser/RuffleProxyServer.cpp
    src/browser/RuffleProxyServer.h
    src/browser/WebEngineBackend.cpp
    src/browser/WebEngineBackend.h
)

target_link_libraries(PVZOLBrowser PRIVATE Qt6::Widgets Qt6::Network Qt6::WebEngineWidgets)

if(WIN32)
    target_sources(PVZOLBrowser PRIVATE
        src/browser/WindowsIeBackend.cpp
        src/browser/WindowsIeBackend.h
    )
    target_link_libraries(PVZOLBrowser PRIVATE Qt6::AxContainer)
endif()

set_target_properties(PVZOLBrowser PROPERTIES
    WIN32_EXECUTABLE TRUE
    MACOSX_BUNDLE TRUE
    MACOSX_BUNDLE_BUNDLE_NAME "PVZOLBrowser"
)

# Keep Qt usage LGPL-friendly for personal distribution by linking dynamically.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

add_custom_command(TARGET PVZOLBrowser POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:PVZOLBrowser>/assets"
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${CMAKE_SOURCE_DIR}/assets/ruffle"
        "$<TARGET_FILE_DIR:PVZOLBrowser>/assets/ruffle")
