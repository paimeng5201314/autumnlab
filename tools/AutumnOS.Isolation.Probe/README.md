# 本机 WebView2 隔离探针

制作人：派蒙。工具直接引用生产 Runtime/Contracts/Storage，使用固定 WebView2 SDK 与运行时。它不启动产品单实例、不使用鼠标键盘、不访问用户账号/文件。新增 `network`、`resources-storage`、`same-origin` 窗口为屏幕外非激活窗口，仅为真实 WebView2 提供 HWND。

在工程根以项目内 SDK 编译；不要与主解决方案同时改写共享构建输出：

```powershell
& .tools/dotnet/dotnet.exe build tools/AutumnOS.Isolation.Probe/AutumnOS.Isolation.Probe.csproj -c Release --no-restore
$env:DOTNET_ROOT = (Resolve-Path .tools/dotnet).Path
& tools/AutumnOS.Isolation.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Isolation.Probe.exe --runtime .tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64 --report artifacts/my-new-owned-probe/probe.json --mode resources-storage
```

每次使用全新报告目录。已有报告拒绝覆盖。仅在 Runtime 及其引用已由同源构建完成时，可给工具 build 加 `/p:BuildProjectReferences=false`，避免与主构建争用。

- `resources-storage`：生产 CSP、真实外置 script、禁止本地 fetch、内嵌 WASM 字节执行 20+22=42、JavaScript eval 被拒绝；通过生产 `WebViewProfileDirectory.ForBinding` 生成 profile，使用 InPrivate 控制器。故意对所有实例使用**同一 origin**，单独验证账户/应用/来源的 profile 边界；Cookie、localStorage、sessionStorage、IndexedDB、CacheStorage 先写读正控，其他绑定无 marker，再证明原绑定未被改变。全部控制器关闭后用同 profile 重开并检查 InPrivate 清空，记录独立浏览器 PID 与真实退出。身份是明确命名的受控测试值，不是 Logto 双账号产品验收。
- `network`：A 是仅针对可信夹具的局部对照，去掉生产响应头、仅让精确自建 loopback 端口/token 的请求进入收集器，其他请求仍由宿主拒绝；B 使用生产头。收集器记录 TCP/UDP 和每条 HTTP 请求行、完成 WebSocket 升级；分别核验 fetch、XHR、beacon、WS、EventSource、资源、iframe/form、worker fetch 的接收端证据，并校验真实 worker/Service Worker 执行对照。随机 `.invalid` 域用于 DNS/preconnect 正反对照，保留 NetLog。A 不是产品运行配置或放行选项。
- `same-origin`：A 仅在探针内部将 `connect-src 'none'` 改成 `'self'`，B 使用未修改的生产头。用每例随机 `.invalid` origin 尝试同源 WSS/WebTransport，精确检查 `/owned` 的网络服务请求事件。记录初始宿主合成导航在响应头之前发生的随机 origin DNS 预解析；不能把这些事件归因于脚本，也不能宣称整台浏览器零 DNS。此反例证明不能为了 fetch 放开 `connect-src 'self'`。
- 原有 `runtime`/`runtime-ab` 是启动挑战的独立引擎验证，不代替主程序入口、来源资格、SDK 和关闭清理验收。

`network` 与 `same-origin` 仅在测试进程使用 NetLog 和 `--no-proxy-server`，保证 DNS 对照可观察；不修改系统代理、防火墙、信任证书或产品参数。Chromium 默认沙箱保持。所有流量只指向自建 loopback 接收器或随机保留 `.invalid` DNS 名，不扫描私网。

真实可信证书 HTTP/3 WebTransport 成功握手、外部权威 DNS 接收端、真实 Logto 切换、主程序普通启动、SDK 授权存档、最终 EXE/ZIP 产品验收不由这些独立工具覆盖。WebTransport 抛错或超时不能作为完整通道隔离成功。失败目录保留；工具报告的通过仅对应列出的断言。
