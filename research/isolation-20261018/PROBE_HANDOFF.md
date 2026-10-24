# 2026-10-18 独立原生探针交接

**最终更新：以 `probe-final2-manifest.json` 及 `probe-final2-{resources-storage,same-origin,network}/probe.json` 为准。** 生产策略增加 async 剪贴板双向 Permissions-Policy 后，工具和生产引用已实际重编、三模式全部重新执行通过。策略源码 SHA256 `6012eaf724d8577f8aa051a4d6c9149117b9d6f38a0440bf141050d441138921`，ID 仍为尚未交付的 `autumn-local-web-v2`。资源/存储报告现为25/25，新增实际引擎识别clipboard-read/write、document policy双向false、permissions.query双向denied三条**只读**检查；没有读取或写入系统剪贴板。legacy `execCommand(copy/paste)` 不计入通过。network与same-origin全部重跑通过；下文first-final保留为前一策略字节历史证据。fixture第二次storage点击也已同步render complete=false，消除旧textarea结果竞态。

制作人：派蒙。以下仅为本机真实 WebView2 引擎证据，不代替主产品 WinUI、SDK、商店、最终 EXE/ZIP 验收。全部窗口非激活、屏幕外；未使用鼠标键盘、未强杀进程、未改系统策略。测试进程和其浏览器均正常退出。

固定运行时 154.0.4258.53，SDK 1.0.4258.31；Windows 10.0.26200。测试策略为生产 `autumn-local-web-v2` 响应头；CSP `connect-src 'none'`、`script-src 'self' 'wasm-unsafe-eval'`，禁止 JavaScript eval，Connection-Allowlist/DNS off 保持。工具及生产关联源、运行二进制和报告 SHA256 在 `probe-final-manifest.json`。

| 报告 | 真实结果 |
| --- | --- |
| `probe-final-resources-storage/probe.json` | 22/22 passed。外置脚本执行、内嵌 WASM 20+22=42、fetch 明确拒绝、eval 明确拒绝；同 origin、真实生产 profile 路径函数、受控账号/应用/来源分隔，Cookie/localStorage/sessionStorage/IndexedDB/CacheStorage 正控与隔离；5个独立浏览器PID全部正常退出；InPrivate 全关后同profile重开为空。 |
| `probe-final-network/probe.json` | A/B passed。27夹具尝试+2 DNS/preconnect提示。A真实20 TCP/44 UDP，两个随机域各4条resolver记录；B额外TCP/UDP与这两个目标resolver均0。15项逐通道接收对照（fetch/XHR/beacon/WS/EventSource/图片/script/module/CSS/FontFace/audio/video/iframe/form/worker-fetch）；真实worker与ServiceWorker执行正控。每例观察器前后TCP/UDP正控均通过。 |
| `probe-final-same-origin/probe.json` | 4/4 passed。局部弱化 `connect-src self` 的A产生3条精确同源WS网络服务事件、1条WT事件；生产none的B均0。WS/WT未出现在 WebResourceRequested，仅页面/script/favicon共3项。证明为本地fetch放开self会使旁路成为实际可达请求。 |

网络A只是可信探针对照：去掉生产头，允许精确自有loopback端口/token通过，其他Wrr请求403；不会成为产品启动配置。收集器记录路径、TCP/UDP、STUN报文类型并实现WS升级。随机 `.invalid` DNS不向无关Web服务发请求；测试进程NetLog与no-proxy参数未进入产品或系统设置。

同源探针注意：WebView2可在宿主随机合成导航收到响应头**之前**预解析该随机origin。最终报告保留这些DNS事件，只对精确 `/owned` WS/WT网络服务请求断言，不能宣称整个浏览器零DNS。该随机origin由宿主生成，不携带应用、账号或存档内容。生产应用导航/资源的其他域名仍由正式策略阻断。外部权威DNS接收端与可信证书HTTP/3 WebTransport成功握手仍 `not_run`。

失败保留：`probe-resources-storage-01` 的前4条功能断言通过，但“没有任何拒绝资源请求”的错误假设遇到自动favicon被宿主拒绝而失败；修复为核对只服务确切index/script，未放行favicon。`probe-same-origin-01` 将初始宿主导航DNS误计为脚本通道，失败保留；随后改为保存所有DNS并精确匹配 `/owned` 网络服务请求，不删除网络证据。`probe-network-01`、各02是中间已通过证据，最终上述3报告对应最终工具字节。

复跑见 `tools/AutumnOS.Isolation.Probe/README.md`。工具最终编译使用 `/p:BuildProjectReferences=false`（共享生产引用已先构建），0警告0错误。新增fixture storage按钮字段为 `marker`、local/session `previous/after`、`cookieBefore/cookieAfter`、`indexedDb.before/after`、`cache.before/after`、`wasm=42`、`evalBlocked=true`、`complete`；可重按做当前文档正控。
