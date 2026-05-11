# NativeFlashProxy Core

`NativeFlashProxy` 是当前项目的原生 core，目标是让浏览器壳、后续其他桌面宿主，甚至别的语言宿主都复用同一套 Flash/PVZOL 相关能力，而不是在每个平台各写一份业务逻辑。

## 设计目标

- 跨平台优先：core 不依赖 WinForms、Qt UI、WPF、WebView2 这类宿主 UI 框架。
- 接口稳定优先：通过 C ABI 暴露能力，便于 `C#`、`C++`、其他桌面宿主调用。
- 解耦优先：本地映射、AMF 编解码、AMF 请求等能力收敛在 core。
- 宿主最薄：浏览器层只负责 UI、内核选择、把请求转交给 core。

## 当前职责

当前 core 已负责：

- 本地映射代理
- 上游代理转发
- 基于 host/url 关键字的映射放行
- AMF0/AMF3 包编解码
- AMF HTTP POST
- PVZOL 场景下的 AMF 请求封装

当前仍在宿主层的逻辑：

- Windows `IE WebBrowser` 控件本身
- 测试期 `WebView2 + Ruffle` 页面宿主
- Cookie 面板、代理设置面板、地址栏、全屏按钮等 UI

## 为什么 Ruffle 暂时没有完全下沉到 core

理想状态下，`ruffle` 的资源分发、HTML 注入、SWF 播放页生成也应尽量下沉到 core。

但目前有一个现实约束：

- `ruffle` 运行本身需要 Chromium/WebView2/Qt WebEngine 这类现代浏览器内核
- 当前 Windows 原生 Flash 路线使用的是 `IE/Trident`

所以现在采用的是折中方案：

- core 继续负责跨平台可复用的网络/协议能力
- 宿主层先提供一层最薄的 `Ruffle` 验证逻辑
- 等这条线稳定后，再评估把哪些 `ruffle` 相关逻辑继续下沉到 core

也就是说：

- 能放进 core 的，优先放进 core
- 确实依赖具体宿主浏览器控件的，才暂时放在浏览器层

## 暴露接口

头文件位置：

- [flash_proxy_core.h](/D:/VS%20Project/Broswer/NativeFlashProxy/include/flash_proxy_core.h:1)

### 代理生命周期

- `flash_proxy_create`
- `flash_proxy_destroy`
- `flash_proxy_start`
- `flash_proxy_stop`

### 代理配置

- `flash_proxy_set_cache_root`
- `flash_proxy_clear_mapping_hosts`
- `flash_proxy_add_mapping_host`
- `flash_proxy_clear_mapping_url_keywords`
- `flash_proxy_add_mapping_url_keyword`
- `flash_proxy_set_upstream_proxy`

### 错误与内存

- `flash_proxy_get_last_error`
- `flash_proxy_free_memory`

### AMF 能力

- `flash_amf_encode_packet_json`
- `flash_amf_decode_packet_json`
- `flash_amf_post_json`
- `flash_amf_post_pvzol_json`

## 建议的宿主调用边界

推荐宿主层按下面的方式使用 core：

1. 启动时创建 `FlashProxyHandle`
2. 配置 cache、本地映射 host、url 关键字、上游代理
3. 启动 core 代理
4. 浏览器宿主只把浏览请求、AMF 请求、配置变更转发给 core
5. 不在宿主里重复实现本地映射/AMF 编解码

## 跨平台落地建议

未来如果需要 Linux/macOS 宿主：

- core 继续保持 `C ABI + sockets + STL` 这类平台无关实现
- 宿主自己选择浏览器内核
- Windows 可用 `IE/Flash` 或 `WebView2/Ruffle`
- Linux/macOS 直接用 Chromium 类内核配合 `Ruffle`

这样可以做到：

- 协议与业务逻辑尽量共用
- 具体浏览器内核按平台分别适配
- 减少重写和分叉

## 与浏览器宿主的推荐边界

Windows 宿主当前采用两条浏览器后端：

- `IE/Flash`
- `WebView2/Ruffle`

建议边界如下：

- core 负责协议、代理、本地映射、AMF、后续可复用的 `ruffle` 支撑能力
- 宿主负责窗口、控件、地址栏、Cookie UI、具体浏览器控件切换

只要某部分逻辑不依赖具体控件，就应继续往 core 收。
