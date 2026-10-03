# T01 运行边界与未完成验证

制作人：派蒙。适用版本 0.1.0-t01。

WinUI 是原生外壳，WebView2 仅承载内部 `.autumn` 的 Web 内容。当前只开放构建随附的元素配对知识小游戏，不提供任意包启动入口。后续商店与第三方包需求全部保留，没有维护认可名单。

## 已实施控制

- `.autumn` ZIP 限制大小、展开大小、数量、压缩比、资源类型；拒绝越界/ADS/Windows 保留名/重复或大小写冲突路径、链接、原生文件与危险脚本；校验 CRC、清单与资源。先完整验证，再同盘暂存、写安装记录并提交。重复安装验证原有实际字节，不覆盖损坏或冲突版本。
- 网关身份来自 RuntimeSession；核对精确来源、消息结构、版本、大小、请求 ID、并发、速率与生命周期。权限只从声明中请求，游客 `saves` 无任意磁盘路径、无账户或宿主令牌。
- WebView2 使用独立随机 origin、游客应用 profile 和 InPrivate 模式。宿主仅从当前包字节响应 GET 资源，设置 CSP、nosniff、no-store 与 Permissions-Policy；禁止导航、子框架、新窗口、下载、外部协议、宿主对象与浏览器权限。UI 和网关均有错误结果，未支持能力不假成功。
- 启用 `--force-renderer-accessibility`，使内容无障碍树稳定可查询。WinUI 组合承载的 Chromium 文档作为独立 UI Automation 节点呈现；测试只查找样例标题，并校验渲染进程归属和主窗口范围。
- 返回主界面保留后台游戏，结束时关闭 WebView2；逻辑状态的 BlocksMaintenance 不冒充 T05 的跨进程维护锁。存档原子提交，坏记录不会被静默覆盖。

## 未完成项 / 发布阻塞

HTTP 资源拦截和 CSP 不等于覆盖浏览器所有网络通道。需要针对 WebRTC（含 UDP/STUN）、WebSocket、Service Worker、iframe、fetch、图片/脚本、导航和外部协议执行本机负面测试；无法可靠禁止的通道需要更强隔离。未验证的完整网络沙箱、跨账号浏览器持久化隔离、真实挂起/恢复、崩溃故障注入、输入法/触控/DPI 矩阵均保持 `not_run`，不得据此开放一般不可信包或进入 T02–T04 完成声明。

本机同一 Windows 用户的恶意进程不是本实现的强隔离边界；现有路径检查拒绝已存在的重解析点，未宣称解决所有本地并发替换攻击。完整来源固定、安装恢复、跨进程维护锁与数据迁移在后续模块验收。

官方参考（2026-10-01 核对）：[WebView2 安全建议](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)、[WinUI 3 的环境/控制器初始化](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.webview2.ensurecorewebview2async?view=windows-app-sdk-1.8)、[浏览器参数](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)。具体测试结果见 AUTUMNOS_PROGRESS.md 指向的构建报告，本文的设计描述不代替实测。
