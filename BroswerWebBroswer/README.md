# BroswerWebBroswer Host

这是当前 Windows 宿主层。

## 职责

它只负责：

- 主窗体 UI
- 地址栏、Cookie 面板、代理设置面板
- 选择当前浏览器后端
- 在 `IE/Flash` 与 `WebView2/Ruffle` 之间切换

它不应该重复实现：

- AMF 编解码
- 本地映射业务规则
- 后续可跨平台复用的协议逻辑

这些逻辑应优先进入 `NativeFlashProxy` core。

## 当前浏览器后端

- `NativeIe`
  使用 WinForms `WebBrowser`，适合本机已有可用 Flash 的情况。
- `RuffleWebView2`
  使用 `WebView2` 承载 `ruffle`，适合无 Flash 或测试 `ruffle` 的情况。

## 本地映射

当前 `IE/Flash` 与 `Ruffle/WebView2` 两条线路都使用同一组本地映射规则：

- host 规则：`pvzol.org`、`youkia.pvz`、`pvz.youkia`、`youkia.com`
- URL 关键字规则：`/pvz/`、`/youkia/`、`youkia.pvz`、`pvz.youkia`、`.youkia.com`

命中后优先从可执行文件同目录的 `cache` 目录读取本地文件，不再转发上游请求。

这组规则当前由宿主层统一下发到：

- native `flash_proxy_core`
- `RuffleLocalProxy`

后续若继续抽离，应优先把这部分配置入口继续收敛到 core，而不是在 UI 层分叉维护。

## 后端选择策略

当前通过独立策略层决定后端：

- [BrowserBackendSelector.cs](/D:/VS%20Project/Broswer/BroswerWebBroswer/BrowserBackendSelector.cs:1)
- [FlashRuntimeDetector.cs](/D:/VS%20Project/Broswer/BroswerWebBroswer/FlashRuntimeDetector.cs:1)

支持三种策略值：

- `auto`
- `native`
- `ruffle`

优先级：

1. 环境变量 `PVZOL_WINDOWS_FLASH_BACKEND`
2. `App.config` 中的 `WindowsFlashBackend`
3. 默认 `auto`

## 当前默认值

当前默认配置是：

- `WindowsFlashBackend=ruffle`

这是为了先验证 `ruffle` 线路。

后续进入正式逻辑时，改成：

- `WindowsFlashBackend=auto`

即可恢复：

- 有 Flash 时优先 `IE/Flash`
- 无 Flash 时自动切换 `WebView2/Ruffle`

## 运行诊断

程序启动后会在可执行文件同目录写出：

- `browser_runtime.log`

用于记录：

- 当前策略值
- Flash 检测结果
- `WebView2` 可用性
- 实际选择的浏览器后端
- `Ruffle` 初始化和导航结果

## 当前打包注意点

为了让 `WebView2` 路线在目标机可启动，当前输出目录除了托管 DLL，还会复制：

- `WebView2Loader.dll`

如果目标机仍然没有走进 `Ruffle`，先看：

- `PVZOL浏览器.exe.config`
- `browser_runtime.log`
