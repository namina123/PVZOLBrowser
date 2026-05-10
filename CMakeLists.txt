cmake_minimum_required(VERSION 3.21)

project(PVZOLBrowser VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

find_package(Qt6 6.5 REQUIRED COMPONENTS Widgets)

if(WIN32)
    find_package(Qt6 REQUIRED COMPONENTS AxContainer)
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
    src/browser/BrowserConfig.h
    src/browser/IBrowserBackend.h
    src/browser/BrowserBackendFactory.cpp
    src/browser/BrowserBackendFactory.h
)

target_link_libraries(PVZOLBrowser PRIVATE Qt6::Widgets)

if(WIN32)
    target_sources(PVZOLBrowser PRIVATE
        src/browser/WindowsIeBackend.cpp
        src/browser/WindowsIeBackend.h
    )
    target_link_libraries(PVZOLBrowser PRIVATE Qt6::AxContainer)
else()
    target_sources(PVZOLBrowser PRIVATE
        src/browser/WebEngineBackend.cpp
        src/browser/WebEngineBackend.h
    )
    target_link_libraries(PVZOLBrowser PRIVATE Qt6::WebEngineWidgets)
endif()

set_target_properties(PVZOLBrowser PROPERTIES
    WIN32_EXECUTABLE TRUE
    MACOSX_BUNDLE TRUE
    MACOSX_BUNDLE_BUNDLE_NAME "PVZOLBrowser"
)

# Keep Qt usage LGPL-friendly for personal distribution by linking dynamically.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
