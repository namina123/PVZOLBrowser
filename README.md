# PVZOLBrowser

一个基于 Qt Widgets 的跨平台浏览器项目：

- Windows 使用 IE 内核：`QAxWidget + Shell.Explorer`
- Linux / macOS 使用 Chromium 内核：`QWebEngineView`
- 默认主页：`http://www.baidu.com`
- 当前工程按 Qt 动态链接方式组织，便于遵守 LGPLv3 的使用边界

## 当前状态

当前版本已经具备这些基础能力：

- 单窗口多标签页浏览
- 地址栏输入并回车跳转
- 后退、前进、刷新、首页
- 顶部页面加载进度条
- Windows / Linux / macOS 按平台切换浏览器内核
- 默认中文界面，并保留 `tr()` 形式，后续可继续扩展 i18n

Windows 上已经完成一次实际编译和启动验证。

## 初期架构

为了避免后期功能增长后出现严重耦合，当前结构拆成了几层：

- `MainWindow`
  负责窗口、工具栏、标签页管理和整体视觉样式
- `BrowserTab`
  负责单个标签页会话和页面加载进度
- `BrowserView`
  负责把统一浏览器接口挂接到界面容器
- `IBrowserBackend`
  负责定义跨平台统一浏览器后端接口
- `WindowsIeBackend / WebEngineBackend`
  负责各平台浏览器内核实现

这样后续继续加下载、脚本注入、Cookie、代理、历史记录时，能够尽量沿职责边界扩展，而不是把所有逻辑堆进主窗口。

## 目录结构

```text
PVZOLBrowser/
  CMakeLists.txt
  README.md
  .gitignore
  src/
    main.cpp
    MainWindow.h
    MainWindow.cpp
    BrowserTab.h
    BrowserTab.cpp
    BrowserView.h
    BrowserView.cpp
    browser/
      BrowserConfig.h
      IBrowserBackend.h
      BrowserBackendFactory.h
      BrowserBackendFactory.cpp
      WindowsIeBackend.h
      WindowsIeBackend.cpp
      WebEngineBackend.h
      WebEngineBackend.cpp
```

## LGPLv3 与动态链接约束

这是个人项目，但如果你基于 Qt 的 LGPLv3 版本来分发程序，建议至少保持这些原则：

- 只使用 Qt 动态链接库，不静态链接 Qt
- 分发程序时同时附带 Qt 的 LGPLv3 许可证文本
- 不阻止用户替换程序依赖的 Qt 动态库
- 如果修改了 Qt 库本身，分发时需要提供对应修改

当前工程侧已经采取这些策略：

- `CMakeLists.txt` 正常链接 Qt 动态模块
- Windows 上显式使用 `/MD` 运行时
- 仓库不提交 Qt SDK 和构建产物

## 依赖

### Windows

需要这些组件：

- Qt 6
- Qt Widgets
- ActiveQt / AxContainer
- CMake
- MSVC 或 MinGW 工具链

说明：

- Windows 分支依赖系统自带的 `Shell.Explorer`
- 程序启动时会写入 `FEATURE_BROWSER_EMULATION=11001`，尽量让嵌入式 IE 以内核高版本模式运行

### Linux / macOS

需要这些组件：

- Qt 6
- Qt Widgets
- Qt WebEngineWidgets
- CMake
- 平台对应的 C++ 编译器

## 构建

### Windows

如果已安装 Qt 并能被 CMake 找到，可执行：

```powershell
cmake -S . -B build -DCMAKE_PREFIX_PATH="C:\path\to\Qt\6.x.x\msvc2022_64"
cmake --build build --config Release
```

### Linux / macOS

```bash
cmake -S . -B build -DCMAKE_PREFIX_PATH=/path/to/Qt/6.x.x/gcc_64
cmake --build build
```

## 后续建议

下一步优先建议补这些能力：

- 历史记录与收藏夹
- 下载管理
- 自定义用户代理与代理设置
- 脚本注入
- 更完整的错误页和证书处理
- 真正的多语言翻译文件体系
