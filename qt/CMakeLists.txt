cmake_minimum_required(VERSION 3.21)
project(HwWidgetQt LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ---------------------------------------------------------------- 版本号单一真源：仓库根 VERSION
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION" _ver_raw)
string(STRIP "${_ver_raw}" APP_VERSION)
string(REPLACE "." ";" _ver_parts "${APP_VERSION}")
list(LENGTH _ver_parts _ver_len)
list(GET _ver_parts 0 APP_VERSION_MAJOR)
if(_ver_len GREATER 1)
    list(GET _ver_parts 1 APP_VERSION_MINOR)
else()
    set(APP_VERSION_MINOR 0)
endif()
if(_ver_len GREATER 2)
    list(GET _ver_parts 2 APP_VERSION_PATCH)
else()
    set(APP_VERSION_PATCH 0)
endif()
set(APP_VERSION_COMMA "${APP_VERSION_MAJOR},${APP_VERSION_MINOR},${APP_VERSION_PATCH},0")
message(STATUS "HwWidgetQt version ${APP_VERSION}")

configure_file(src/version.h.in "${CMAKE_CURRENT_BINARY_DIR}/version.h" @ONLY)
configure_file(res/app.rc.in "${CMAKE_CURRENT_BINARY_DIR}/app.rc" @ONLY)

# ---------------------------------------------------------------- Qt
find_package(Qt6 6.2 REQUIRED COMPONENTS Core Gui Widgets Network)
qt_standard_project_setup()
# qt_standard_project_setup 只开 AUTOMOC/AUTOUIC，qt_standard_project_setup 不会处理 .qrc，
# 这里手动开启 AUTORCC，否则 :/lang.json 与 :/HwWidget.ico 不会打包进 exe
set(CMAKE_AUTORCC ON)

set(APP_SOURCES
    src/main.cpp
    src/cfg.h            src/cfg.cpp
    src/lang.h           src/lang.cpp
    src/format.h         src/format.cpp
    src/device.h         src/device.cpp
    src/paintutil.h      src/paintutil.cpp
    src/lightwindow.h    src/lightwindow.cpp
    src/traymenu.h       src/traymenu.cpp
    src/hostspopup.h     src/hostspopup.cpp
    src/widgetwindow.h   src/widgetwindow.cpp
    src/settingsdialog.h src/settingsdialog.cpp
    src/hostsdialog.h    src/hostsdialog.cpp
    src/smsdialog.h      src/smsdialog.cpp
)

qt_add_executable(HwWidgetQt WIN32 ${APP_SOURCES} res/app.qrc "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
target_include_directories(HwWidgetQt PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src" "${CMAKE_CURRENT_BINARY_DIR}")
target_link_libraries(HwWidgetQt PRIVATE Qt6::Core Qt6::Gui Qt6::Widgets Qt6::Network)

if(WIN32)
    # DWM 圆角/描边控制（DwmSetWindowAttribute / DwmExtendFrameIntoClientArea）
    target_link_libraries(HwWidgetQt PRIVATE dwmapi)
endif()

# ---------------------------------------------------------------- 静态构建的插件导入
# 静态链接时平台/样式/TLS/图标等插件不会被自动带上，必须在链接期显式导入，
# 否则单文件 exe 启动会报 "no Qt platform plugin could be initialized"。
if(NOT QT6_IS_SHARED_LIBS)
    qt_import_plugins(HwWidgetQt
        INCLUDE_BY_TYPE platforms QWindowsIntegrationPlugin
        INCLUDE_BY_TYPE styles QModernWindowsStylePlugin
        INCLUDE_BY_TYPE tls QSchannelBackendPlugin
        INCLUDE_BY_TYPE networkinformation QNLMNIPlugin
        INCLUDE_BY_TYPE imageformats QICOPlugin
    )
endif()

if(MSVC)
    # 源码含中文/俄文/日文等字面量，MSVC 默认按本地代码页解析会乱码
    target_compile_options(HwWidgetQt PRIVATE /utf-8)
else()
    target_compile_options(HwWidgetQt PRIVATE -finput-charset=UTF-8 -Wall -Wno-unused-parameter)
endif()

set_target_properties(HwWidgetQt PROPERTIES OUTPUT_NAME "HwWidgetQt")