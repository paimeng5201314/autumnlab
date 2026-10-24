# AutumnOS 0.5.2 运行隔离

制作人：派蒙。适用源码：`0.5.2` 本地隔离开发候选；策略：`autumn-local-web-v2`；SDK：`0.3.0`；消息协议：`1`。本文描述实现；本轮真实逐项结果见 `artifacts/isolation-20261018/`，不以历史结果代替当前产物验收。剪贴板完整边界未通过，尚不能交付为第三方正式放行版本。

2026-10-24 按派蒙最新要求先完成代码核验与测试包，后续原生实测交由派蒙。Build05的838项C#和其余七组自动检查已通过；其原生界面未运行。新的交接构建会重新关联源码与测试报告，步骤见[实测说明](isolation-manual-verification.md)。本次进一步将关闭操作调整为先撤销Session代次/证明，再取消宿主任务，并独立尝试每项资源释放；某项异常不跳过WebView关闭。失败时保留待清理状态与维护租约，重入不重复清理，审计异常不能中断撤销。此代码核验不替代真实挂起权限窗口关闭复验。

## 原生策略与启动证明

应用仍是原生 .NET / WinUI 客户端内部运行的 Web/WASM `.autumn`。宿主只使用交付目录的固定 WebView2 `154.0.4258.53`，不静默降级到其他已安装浏览器。缺少固定引擎或存在可替换浏览器、用户目录、附加参数的 WebView2 环境覆盖时，启动失败。

宿主对每个合成响应施加同一策略，包括拒绝资源的 `403` 响应：

```http
Connection-Allowlist: (response-origin); redirects=block; webrtc=block
X-DNS-Prefetch-Control: off
Permissions-Policy: camera=(), microphone=(), geolocation=(), usb=(), serial=(), bluetooth=(), display-capture=(), clipboard-read=(), clipboard-write=()
Content-Security-Policy: default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
```

还保留 `nosniff`、`no-store`、`no-referrer` 和浏览器设备权限策略。Connection Allowlist 由 Chromium 原生浏览器与网络服务实施，补充 CSP 对 WebRTC、DNS 预取、预连接等通道的覆盖；没有替换 JavaScript 网络函数或开启实验标志。唯一允许的响应 origin 每实例随机生成；资源只来自宿主已经校验并读入内存的包，没有通用网络或文件系统代理。

固定154引擎另使用辅助参数 `--disable-features=WebRtcHideLocalIpsWithMdns`，避免RTC初始化仍建立mDNS监听而请求Windows防火墙入站许可。它不构成网络边界，不单凭参数存在或没有弹窗形成启动证明，也不关闭Chromium沙箱。新路径真实A/B已证明：同样辅助参数下，无Connection-Allowlist的A不能形成证明，生产头B才能形成证明；该次无安全提示且未新增防火墙规则。正式宿主及全部接收端探针使用同一 `RuntimeIsolationPolicy.BrowserArguments`，仍需按最终字节复测。上游相关修复CL8485457于157源码树合入，尚无证据证明当前可下载的154/155固定包已回补，不能仅升级版本号冒充修复。

每个实例先加载仅包含宿主生成 HTML 和外置脚本的挑战页，此时不提供应用资源，也不接受 SDK 消息。挑战使用新随机 nonce，绑定当前实例 ID 和精确 origin。脚本创建两个真实原生 `RTCPeerConnection`，只配置字面 loopback STUN 地址和 relay 策略，不提供 TURN，进行双 peer 协商。因此无策略的引擎在自检时也没有可用 relay 服务器。成功条件包括原生 ICE server 列表被过滤为空、ICE 状态 `failed`、零候选及 `restartIce` 后仍失败。实测 154 的聚合 `connectionState` 可保留 `new`，仅接受 `new` 或 `failed`，其他状态拒绝。

Shell 在调用 `ExecuteScriptAsync` 前后核对真实 `Core.Source`，直接读取自己的挑战结果。Runtime 严验原生返回的 JSON 包裹、8 KiB UTF-8 上限、协议、nonce、实例、origin、全部状态与精确字段集合；缺失、重复、未知字段、异常或超时都不形成证明。应用 `WebMessageReceived`、SDK 参数或自报身份不能提供证明。只有仍处于 Starting 的匹配实例接受验证器生成的证明，随后才提供包资源并导航到应用页面。

`platform.getCapabilities().networkSandboxVerified` 是当前实例的动态布尔状态：新建或未验证时 `false`，完成本次原生自检后 `true`；关闭或崩溃立即撤销，即使后续原生资源关闭发生异常也不能保留证明。不能复用同 appId、其他实例或旧运行的证明。SDK 字段类型由旧 literal `false` 拓宽为 `boolean`；SDK 仍为 `0.3.0`、协议仍为 `1`。

网络自检本身不代表完整运行资格。已确认旧式copy/cut可清空剪贴板后，当前源码在共享WebAppHost的包验证后、创建WebView前，对所有外部安装包及任意开发预览返回 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`。开发模式、测试源、manifest声明、报告状态、ChemLeap名称和PM/SQ均不能解除。自带元素配对也需安装资源校验通过、宿主创建的来源绑定精确匹配、安装包摘要和身份与持只读lease检查的固定发行asset一致；保留现有安装记录、选版、运行lease及存档命名空间。不同字节的旧选版不会被悄悄重装或改版本；GitHub/local即使同ID或同摘要也不转为bundled。该措施是已知缺口下的拒绝执行，**不是本轮“正常启动第三方”目标完成**。实现并验证实际受限宿主后，才能以其真实初始化结果替换此拒绝。

Build01–04是发现问题和复测修复的未交付中间诊断版本，其中任意外部包路径尚未加入上述剪贴板失败关闭处理；不能拿这些EXE作为已验证安全版本给第三方应用放行。后续构建与最终验证单独关联哈希。

## 保留的安全边界

基础配置支持包内 HTML、外置 JavaScript、CSS、图像、字体、WebGL，以及由包内脚本提供字节的 WASM 编译；不开放 JavaScript `eval`。`fetch`/XHR 即使请求本地合成 origin 也被禁止，不能将 `connect-src` 放宽为 `self`：同源 WebSocket/WebTransport 不保证经过 WebResourceRequested。依赖远程 CDN、动态 fetch 或独立 `.wasm` fetch 的应用需重新适配并用新版本/新哈希安装，不能修改已安装包。

现有 SDK 没有第三方联网权限或宿主网络代理；`network.*` 为 `CAPABILITY_UNAVAILABLE`。授权联网、目标白名单/DNS固定连接、逐跳重定向核验及不携带宿主凭据的网络代理是待实现能力，不把宿主商店下载当作此项通过。网络需求保留，当前基础配置不支持它。第三方自己填写的离线声明、安全字段、分类或可编辑报告不参与隔离证明。

环境创建后精确核验实际 BrowserVersionString 为 `154.0.4258.53`，确认实际用户目录、浏览器可执行路径及 InPrivate 状态；启用独占用户目录访问。进程环境或适用于宿主的 WebView2 注册表覆盖存在时失败关闭；只读检查，不修改系统策略。自检仍为每实例强制执行，版本匹配不代替自检。审计记录策略 ID、实际引擎、浏览器 PID 与哈希命名空间，不记录账号资料或 SDK 内容。

宿主禁止框架导航、顶层重导航、弹窗、下载、外部协议与浏览器权限，关闭宿主对象、开发工具、自动填充及密码保存。只接收当前页面来源的有限 SDK 消息；身份来自宿主会话，权限来自包声明和真实授权，应用不会收到账户凭据。

禁止的顶层导航可能触发固定引擎的内部错误文档，即使宿主已设置 Cancel=true。真实五组对照证明 Stop 和关闭内置错误页也不能保留原文档。宿主因此同时监听原生 SourceChanged/ContentLoading，应用首次文档提交后如有新文档、来源变化或错误页，立即撤销会话和证明、关闭 WebView；SDK 迟到响应不得再交付。此类违规导航会结束该实例，已提交存档保留；不声称未保存的页面输入可以从被浏览器替换的文档恢复。复现及正控见 `artifacts/isolation-20261018/navigation-cancel-01/`，产品新处理仍需对应构建实测。

**当前未通过的剪贴板边界（2026-10-18）**：原生 Permissions Policy 拒绝异步 clipboard-read/write，但不覆盖所有旧式浏览器编辑通道。第二次单次获授权的私有窗口站实测已完成 copy/cut/paste 六项A/B：无Job三项正控通过；新浏览器 Job UI READCLIPBOARD|WRITECLIPBOARD=6 能阻止粘贴读取，但复制、剪切仍清空合成 clipboard，原生序号分别22→23、40→41。Job草案因此不集成，不将“没能发布新文本”改写成“没有改动剪贴板”。全部浏览器自然退出、临时窗口站释放，没有访问WinSta0 clipboard。报告在 `artifacts/isolation-20261018/clipboard-station-uac-02/`。后续获授权的受限token控制台试验仅A正控执行成功；B创建进程后初始化失败0xC0000142，没有探针JSON，浏览器与剪贴板仍not_run。不能用风险确认、开发者模式或应用名单代替实际受限宿主。

浏览器使用按账户、应用、来源绑定的独立 profile 路径及 InPrivate 控制器；账号改变前关闭旧环境。正式存档通过宿主 SDK 按 app/source/account 隔离，不以浏览器缓存代替。`worker-src 'none'` 保留，排除共享 worker/blob worker 的跨上下文策略风险。

此实现依赖固定浏览器的原生机制，不声称新增独立的操作系统网络防火墙或 AppContainer 宿主。Chromium 默认沙箱保持开启。同一 Windows 用户的恶意本地进程、浏览器漏洞和资源耗尽不属于本文宣称已解决的强隔离范围。

## 历史验证状态（2026-10-17，不计为本轮新策略通过）

文档冻结时，独立原生工具直接链接当前策略源的挑战验证 `3/3` 通过：无策略 A 不能形成证明，有策略 B 才产生匹配证明。独立网络最终报告 `network-direct-final-bf58e3e82c3748289d7d36a273b1a3ad` 共 `29` 项通过，包含 fixture `27` 项及两项网络提示验证。无策略 A 实际产生 `4` 次 TCP 连接、`43` 个 UDP 数据报，两处随机 DNS 目标各有 `4` 个 resolver 记录；有生产策略 B 的对应观测均为 `0`。直接加入 remote ICE candidate 可以返回 accepted，但随后 ICE 失败且没有数据外流；不能仅凭 API 是否抛异常判断隔离。

额外 about:blank 初始 iframe realm 的 RTC 尝试在固定 154 独立原生工具中触发渲染进程崩溃，不能记为正常可用。宿主失败路径先撤销证明，再关闭网关和应用；新增主宿主测试包含该故障关闭情形。本文冻结时，主宿主该 UI 用例与普通商店游戏链路仍为 `not_run`，后续成绩写入交付报告。

初次完整代码测试 `833/833`、机器合同 `360/360`、静态源码检查 `81/81` 通过；这些不代替 Windows 产品测试。新的主宿主网络负面 UI 测试，以及 ChemLeap 普通商店下载、安装、游戏跳跃、SDK 存读档 UI 验证，在本文冻结时均为 `not_run`。

任意外部 DNS 权威服务器端验证、可信证书下的真实 WebTransport 握手、真实账号切换后的浏览器持久化隔离、物理触摸/IME、多 DPI/显示器、Windows 10、干净机器和长期故障矩阵仍需对应实测。不能从当前机器、固定引擎和列出的探针推导未来 API 或全部平台已通过。实际命令、源快照、构建 ID、文件哈希、失败记录与最终逐项状态见 `artifacts/runtime-isolation-20261017/` 的报告及 `HANDOFF.md`。

## 官方依据

- [Microsoft Edge 152 平台发布说明](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/release-notes/152)列出 Connection Allowlists；目标 WebView2 支持仍通过真实运行核验。
- [Chrome 正式发布说明](https://developer.chrome.com/blog/connection-allowlist-announcement)说明 152 起正式支持、默认阻止 WebRTC/重定向及对 CSP 的补充。
- [Chromium 实现设计](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/docs/connection_allowlist_design.md)说明网络上下文约束、DNS/WebSocket/WebTransport/WebRTC 检查及网络服务恢复。
- [WICG 规范](https://wicg.github.io/connection-allowlists/)定义响应头与 worker 边界；[WPT 测试](https://chromium.googlesource.com/external/w3c/web-platform-tests/+/7ba7013f9eab51c771cb980eec5807c49c1a4bb1/connection-allowlist/tentative/webrtc-block-interactions.sub.window.js)覆盖 ICE 配置过滤和失败行为。
- [WebView2 安全建议](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)用于导航、来源和宿主能力控制。CSP 草案 `webrtc` 指令不是本实现依赖的已验证机制。
