# Runtime isolation negative fixture

制作人：派蒙。此目录是供本机验收运行器使用的测试素材；不是游戏、正常商店应用或安全实现。只在新的隔离宿主/数据目录执行，保留历史结果。素材自身没有运行测试，不能因文件存在把任何验收标为通过。

`index.html` 是包入口，UI 文档标题为“AutumnOS 完整隔离负面样例”，结果 textarea 标签为“完整隔离观测 JSON”。页面无内联脚本或样式，不依赖远端脚本，也不依赖 SDK 包装器。SDK 探针直接向当前实际 WebView2 的 `chrome.webview` 发送协议请求。

## 运行器适配

1. 在新临时目录复制 `manifest.json`、`index.html`、`probe.css`、`config.js`、`probe.js`、`network.css`、`worker.js`、`service-worker.js` 八个资源；不要把本 README 或 C# 观察器送入 `.autumn` 包校验。
2. 编译 `LoopbackNetworkObserver.cs`，显式用新的 32 位小写十六进制随机 token 构造观察器。它只绑定 `127.0.0.1`。`HttpPort` 与 `UdpPort` 数值相同，协议不同；这同时观察发往 HTTP URL 同端口的 HTTP/3 / WebTransport UDP。`TcpPort` 是独立 TURN/TCP 观察端口。构造前不会监听。
3. 在副本中把 `config.js` 替换成 `window.autumnIsolationProbeConfig = Object.freeze({...})`，字段为 `token`、`httpBase: http://127.0.0.1:<HttpPort>/<token>`、`tcpPort`、`udpPort`、`deadlineMs:3500`、`allowNetworkAttempts:true`。配置校验失败时页面拒绝发探针。把 `network.css` 的所有 `__HTTP_BASE__` 替换成相同 `httpBase`。
4. 用真实官方 CLI validate / pack；通过正常安装/注册及普通第三方启动路径运行。不能启用开发者例外证明社区隔离。若先做开发预览用于排查，报告明确区分，两类证据不混用。
5. 自建 HTTP 客户端（UseProxy=false）先后请求 `/<token>/control-before`、`control-after` 并校验固定响应 `owned-isolation-observer`。自建 TCP 客户端向 **两个 TCP 端口**先后写 `CONTROL:<token>:before\n` 与 `CONTROL:<token>:after\n`；自建 UDP 客户端向 `UdpPort` 发送相同正控报文。关闭客户端，等待观察器真实记录后才采集前/后计数。正控必须同时覆盖 TCP 连接、HTTP 请求和 UDP 接收，不能只检查监听端口已创建。
6. 用所属进程、窗口范围和文档标题定位原生 UI Automation 节点。确认 `configured:true`，保存原文档 `documentNonce`、`pageOrigin`、未保存输入、宿主 instanceId、构建 ID 与源哈希。点击“尝试所有网络通道”，等待 `network.complete:true` 并逐项核对 `started` 与 `outcomes`，同时保留 CSP、真实宿主拒绝事件和监听记录。不能仅以 JS 抛错判断无网络。
7. 先独立执行“探测 SDK 权限与伪造”；再点击新窗口、外部导航、表单导航、本地下载和未注册协议按钮，每次等待可归属该实例的实际拒绝事件。每步核对原文档 nonce、未保存输入、instanceId 和维护阻塞仍存在，避免“通过关闭游戏阻止导航”误判。
8. 关闭自己的会话/宿主并记录正常清理、零残留。观察器 Dispose 会等待自己的连接任务；不关闭其他应用、不强杀用户进程、不改系统代理/防火墙/注册表。

## 通道与预期

当前网络按钮共27项，其中新增 `rtc-direct-udp`：offer端只创建SDP而不设置localDescription，answer端供应精确loopback UDP端口的远端host candidate并观察原生ICE。candidate被接受不等于有报文送达，必须核对独立UDP观察者；独立引擎A/B实测亦另加两个随机DNS/preconnect目标。另有“尝试初始 about:blank RTC”按钮，用于单独验证浏览器渲染异常后的宿主失败关闭；它不混入主网络序列，以免破坏其他观测。完整生产策略采用原生Connection-Allowlist，详见 docs/runtime-isolation.md；下文关于代理仅为反例边界。

网络按钮执行 fetch、XHR、sendBeacon、WebSocket、EventSource、图片、远端 script、模块 import、远端样式、本地样式中的 `@import` / 字体 / 背景图、FontFace、audio、video、iframe、frame 目标 form、prefetch、preconnect、dns-prefetch、专用 Worker、自建 Blob Worker、SharedWorker、Service Worker、STUN/UDP、TURN/UDP、TURN/TCP、WebTransport。RTC 使用真实 `RTCPeerConnection`、data channel、offer 和本地 SDP，STUN 使用 all，TURN 使用 relay；不是空构造测试。工作器/Service Worker 若意外创建，页面会尝试自清理，报告仍须判定 worker 策略失败。

浏览器 API 缺失、被宿主显式禁用、异常、CSP 拒绝、无完成信号分别保留，不能互相改写。“无完成信号”不是通过；`beacon` queued 也不是已出网。负面期 **任何非正控 TCP 连接**（含零字节、TLS、失败握手）或 **任何非正控 UDP 报文**都应阻断验收；不能只要求 HTTP 请求行为零。正控 HTTP 连接也是非 `CONTROL:` 连接，运行器必须通过精确记录 ID/时间窗口剔除它们，不能宽泛忽略所有连接。

dns-prefetch 使用 `localhost`，不向生产 DNS 或任意外网域名发送探针，因此本素材不能证明任意 DNS 外传已阻止。WebTransport 使用 loopback HTTPS 且无可信测试证书，浏览器拒绝可能先发生在证书/能力层；零 UDP/TCP是观测事实，不能据此称已覆盖任意真实服务器握手。完整禁网还应有受信任的宿主隔离策略（例如受支持网络代理/浏览器能力约束或更强 OS 隔离）及其不可被脚本修改的证据。此素材不调用原生 socket API，它们不应暴露给 Web 应用。

## 权限、伪造、跨实例与关闭

默认包 `permissions:[]`。SDK 按钮对存档/存储/文件能力请求应收到真实 PERMISSION_NOT_DECLARED；任意原生命令与取宿主令牌方法应能力不支持。额外顶层 appId/sourceKey/accountKey 应 INVALID_REQUEST；伪造参数也必须拒绝，不能改变宿主绑定。协议 999 应 PROTOCOL_UNSUPPORTED；重复请求第二次应 DUPLICATE_REQUEST。运行器读取实际响应代码并核验测试目录未出现探针 save marker，不预先替换正确答案。

另复制一个独立包并只声明 `["saves"]`，更换 appId/token，在同一次真实原生点击后执行 SDK 按钮。原生运行器拒绝真实权限对话框，随后重复请求不得反复弹窗，写入仍 PERMISSION_DENIED。不要为了测试篡改真实权限 JSON 或自动允许提示。默认无权限包不能代替此拒绝流程。浏览器权限按钮单独请求 geolocation、camera、microphone、notification；期待实际宿主 PermissionRequested 拒绝或显式功能关闭，不能打开真实设备。

跨实例：以两个不同 token 同时运行包，分别点击“读取并标记浏览器存储”，再让第二个实例先打开 BroadcastChannel、重新以新会话触发第一个实例发送。两个页面 `pageOrigin` 和 nonce 必须不同，`channelMessages` 应空，新实例读取的 localStorage/sessionStorage/cookie 不应包含前一实例 marker。不同 source/account 的 profile 分隔还须由独立账号/来源矩阵验证，本素材不能凭 guest 两实例证明全部账号隔离。

“排队请求后由宿主结束”只循环有限的生命周期只读请求，供原生驱动在请求期间真实结束实例，验证不再收到活跃响应、不保留 WebView、维护租约和能力句柄释放。页面关闭后无法自证无宿主写入，因此等待中的存档写/权限弹窗/账号切换/旧会话回复由现有 RuntimeSession 逻辑测试和原生文件哈希验收补充。勿把这一按钮视为全部并发撤销证明。

JS 与 C# 记录只包含固定探针值、随机令牌、API 状态、端口和协议形态；不读取真实文件内容、Clipboard、宿主账号/令牌或浏览器凭据。所有失败报告应保留，未执行项记 not_run。

## 官方核对入口（2026-10-17）

- [WebView2 browser flags](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)：`proxy-server` 仅影响 HTTP/HTTPS，不能单独作为所有传输禁网证明。
- [Edge WebRTC handling policy](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/webrtciphandlingurl)：`disable_non_proxied_udp` 限制直连 UDP，可能转代理或 TCP，并非禁用整个 WebRTC API。
- [W3C CSP3 connect-src](https://www.w3.org/TR/CSP3/#directive-connect-src)：覆盖 fetch/XHR/EventSource/Beacon/WebSocket 等请求；仍须对实际浏览器非 Fetch 通道独立验证。
