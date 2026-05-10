# PVZOLBrowser

基于 Qt / C++ 的个人跨平台浏览器项目，当前重点是兼容 Flash / SWF 页面访问。

## 当前行为

- 默认主页为 `http://www.baidu.com`
- 界面默认中文
- 禁用浏览器内弹窗
- 尽量阻止页面把外部 URL 转交给系统其他浏览器
- 提供运行时代理切换：
  - 系统代理模式：跟随 Windows / Qt 系统代理
  - 固定代理模式：先用 `http://www.baidu.com` 测试代理可用，再启用；失败则回退系统代理
  - 代理记忆配置写入可执行文件同级的外部目录 `PVZOLBrowserData/settings.ini`

### Windows

- 若系统环境中存在原生 Flash ActiveX，则使用 `IE + Flash` 路径渲染
- 若未检测到原生 Flash，则回退到 `Qt WebEngine + Ruffle`

### Linux / macOS

- 使用 `Qt WebEngine + Ruffle`

### Ruffle 方案

- `assets/ruffle` 直接来自参考项目 `C:\projects\references\Flash-Browser-Android-master`
- HTML 页面会注入与参考项目一致风格的 Ruffle 配置脚本与 `bootstrap.js`
- 直接打开 `.swf` 地址时，会进入内置的 Ruffle 播放页
- 页面中嵌入式 `.swf` 资源仍走原始 SWF 数据代理，避免破坏页面内的 Flash 加载流程

## 架构

当前刻意做了初期解耦，避免后续下载、脚本注入、代理、历史记录等功能直接耦合进主窗口。

- `MainWindow`
  负责窗口、工具栏、标签页和整体视觉层
- `BrowserTab`
  负责单标签状态和加载进度
- `BrowserView`
  负责界面容器与浏览器后端对接
- `IBrowserBackend`
  定义统一浏览器后端接口
- `WindowsIeBackend`
  负责 Windows 下的 IE/ActiveX 路径
- `WebEngineBackend`
  负责 Chromium 内核路径
- `RuffleProxyServer`
  负责本地代理、Ruffle 资源分发、HTML 注入、SWF 播放页
- `FlashRuntime`
  负责运行时判断是否存在原生 Flash 支持
- `ProxyManager`
  负责全局代理模式切换、固定代理连通性检测与 Qt 网络代理应用

## LGPLv3 约束

本项目按“仅动态链接 Qt”来组织，便于遵守 LGPLv3。

- `CMakeLists.txt` 仅链接 Qt 动态库
- Windows 运行时使用 `/MD`
- 仓库不内置 Qt 源码改动
- 分发时应一并附带 Qt 对应许可证文本，并保留用户替换 Qt 动态库的能力

## 本地构建

### Windows

当前已在 Windows 上实际完成一次编译与启动烟雾验证。

```powershell
cmake -S . -B build-msvc -G "NMake Makefiles" `
  -DCMAKE_BUILD_TYPE=Release `
  -DCMAKE_PREFIX_PATH="C:\path\to\Qt\6.x.x\msvc2022_64"

cmake --build build-msvc --config Release
```

若要分发可执行文件，需执行：

```powershell
windeployqt build-msvc\PVZOLBrowser.exe
```

### Linux / macOS

```bash
cmake -S . -B build -DCMAKE_PREFIX_PATH=/path/to/Qt/6.x.x
cmake --build build
```

## 兼容性说明

- 现有工程基于 Qt 6.8.x
- 因为 `Qt WebEngine` 本身限制，`Windows 7 / XP` 不属于当前可保证范围
- 如果目标机器本身具备 `IE + Flash` 运行条件，Windows 路径会优先尝试走原生方案
- 当切换到固定代理模式时，Windows 下新建标签会优先使用 Chromium 路径，以便代理设置对页面请求生效

## 目录

```text
PVZOLBrowser/
  assets/
    ruffle/
  src/
    main.cpp
    MainWindow.*
    BrowserTab.*
    BrowserView.*
    browser/
      BrowserConfig.h
      IBrowserBackend.h
      BrowserBackendFactory.*
      FlashRuntime.*
      RuffleProxyServer.*
      WebEngineBackend.*
      WindowsIeBackend.*
  CMakeLists.txt
  README.md
```
