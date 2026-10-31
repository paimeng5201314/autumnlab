# AutumnOS 0.5.2 运行隔离

制作人：派蒙。适用源码：`0.5.2` 本地隔离开发候选；策略：`autumn-local-web-v2`；SDK：`0.3.0`；消息协议：`1`。本文描述实现；本轮真实逐项结果见 `artifacts/isolation-20261018/`，不以历史结果代替当前产物验收。剪贴板完整边界未通过，尚不能交付为第三方正式放行版本。

2026-10-27验收后补记：Build11 Windows Release零警告/错误、C#982及其余8组检查通过，EXE和ZIP新解压副本各53项、前台/后台/原生选择器关窗31项通过，包含真实浏览器组清理和新游戏读档。独立真实WebView2 WebTransport32项A/B通过：相同InPrivate/参数/外链脚本，A完成临时证书SHA pin会话及两种回传，B实际执行且CSP拒绝、接收端0非健康UDP，两组前后健康和Normal原句柄退出均完成。具体范围、完整哈希和原始报告见 `artifacts/isolation-completion-20261027/REPORT.md`。这不是网络SDK broker、普通ChemLeap或完整隔离通过；AppContainer首轮203失败保留，另获授权的v3单LOCALAPPDATA对照证明零能力低完整性原生child可启动/创建自有命名对象并正常清理。没有浏览器或真实剪贴板操作，不能据此放行。

2026-10-24 当前复验补充：Build07 的单文件和 ZIP 解压副本均实际53项通过，包含浏览器全组退出先于包/维护租约释放的原生记录以及重开游戏读取合成存档。独立当前策略网络诊断 `network-current-run-03`、受控身份浏览器存储/资源诊断 `resources-current-run-02` 34项、页面外链脚本执行的 document.open/write 诊断 `document-replacement-native-run-02` 已通过各自范围；它们不能替代产品 SDK、第三方剪贴板和 ChemLeap 运行。账号隔离使用明确测试身份，不是 Logto 双账号。document.open 复用同一Document/Global时，DOM变化不自动等于主体改变；本轮实际记录原生事件、策略保持和合成消息，不把合成消息当作真实SDK敏感响应。

当前正在补正常退出的最后边界：主窗口收到正常关闭请求后先关闭新操作准入，撤销应用会话和任务，取消并等待本窗口拥有的原生选择器与确认窗口，等待浏览器、下载、更新和身份异步任务真实完成，最后才关闭窗口并释放单实例/更新协调。关闭准入不释放已有写入或游戏租约，不以超时、task canceled或隐藏窗口代替资源完成。相关实现和控制测试在新源码中；新构建/原生前台、后台和打开选择器时关闭的验收结果单列，不移用Build07“先结束游戏再关闭窗口”的成绩。

每个新WebView2环境显式设置并读回 `IsCustomCrashReportingEnabled=true`，在创建环境前完成；不建立自定义上传器。这使当前实例保留本地崩溃资料，避免其是否发送给Microsoft取决于Windows崩溃报告同意设置；它不控制其它OS网络活动。旧未显式设置的实测，其OS是否上传保持unknown，不推测已上传或未上传。实现依据固定SDK的 [EnvironmentOptions3](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2environmentoptions3?view=webview2-1.0.4258.31)。这项隐私设置与网络/剪贴板边界分别验收。

保存选择器自动化已发现并保留一个失败：现代FileNameControlHost的WM_SETTEXT回读与真实选择结果不一致，返回的是默认AutumnOS-export.json。本轮没有继续调用导出写入；该返回路径未证明处于测试目录，不能把这项记为受控文件访问通过，也不能把窗口文字回读当作完成文件选择。现代写入自动化已暂停，原生只打开/取消测试不选择文件。

2026-10-27入口兼容性修复：单文件解包使用保留128bit随机度的25字符临时目录，避免最终Product目录路径可用、临时目录却因额外13字符超限而启动失败。原相对路径校验、逐级重解析点拒绝、内容hash及目录移动保持。真实Framework正反例验证旧stage的269字符路径失败、新stage253及最终256字符读回成功；不是无限长路径支持，也没有修改系统长路径设置。超出最终支持范围返回`STARTUP_PATH_TOO_LONG`，入口和AutumnOS_Data需一起移短目录，不能清除真实数据。原失败与实际修复证据在`artifacts/isolation-completion-20261027/long-path-review/`。

2026-10-24 新一轮按 [隔离实施计划](isolation-completion-plan.md) 推进，实际新增结果见 `artifacts/isolation-completion-20261024/`。文件句柄现在绑定账号/来源/实例/epoch之外的单权限代次；撤销再授予不会恢复旧句柄，不相关权限变动不使句柄失效。原生选择器注册、读取返回与最终写入在权限和会话租约内校验；新选择器结果可以签发新代次句柄。权限通知只异步排队清理，真正执行时按当前代次检查并释放闲置失效句柄，迟到旧通知不会关闭重新选择的新句柄。后台清理显式获取对应files权限租约，不依赖SDK请求的AsyncLocal；关闭时解绑通知并释放全部句柄。清理检查异常会显示失败，逐次权限检查仍保留。权限事件在产生时冻结 revision，原生宿主排队后沿用这个值，并在真正 PostWebMessage 时复检；迟到的旧决定与当前状态不符时不发送。开发预览使用原授权时，最终租约也持有原权限服务锁，不只锁预览覆盖层。这些局部修复不代表浏览器剪贴板或完整隔离已经通过。

先前代码交接阶段按派蒙要求先完成代码核验与测试包、原生实测交本人；随后派蒙要求继续完成隔离并审查游戏，本轮已继续运行普通权限、自有数据的独立机制诊断，不复用已消耗的一次性UAC授权。当前isolation-03构建的920项C#和其余8组自动检查通过；独立浏览器诊断不等于最终产品验收。关闭实现先撤销Session代次/证明，再取消宿主任务，并独立尝试每项资源释放；某项异常不跳过WebView关闭。失败时保留待清理状态与维护租约，重入不重复清理，审计异常不能中断撤销。此代码核验不替代真实挂起权限窗口关闭复验。

## 浏览器退出与更新互斥

2026-10-24 实测表明，关闭控件后浏览器仍可能运行约六秒。当前源码先撤销 Session、拒绝待完成的 bootstrap、取消任务及文件能力，再关闭本实例的控件；保持原浏览器进程句柄、环境退出订阅、包运行租约和维护租约。取得匹配的 `BrowserProcessExited`、原持有进程对象退出和环境进程集合为空的证据后，才记录资源 `closed` 并解除租约。`Session.State=Closed` 或会话的 `BlocksMaintenance=false` 本身不是资源释放证据。

清理重试仅移除本实例的控件，并保留尚未移除的引用，不清空共用容器。控制器初始化失败而未取得 PID 时，也要求环境退出事件；无法确认的创建失败保持维护阻塞，不能超时后自动放行更新。产品不会强杀进程，也没有“等十秒就视为退出”的路径。诊断草案的十秒上限仅用于报告该次合作退出是否通过。

最终单文件驱动要求同实例原生退出事件在资源释放日志之前，PID 与持有对象一致，两类租约已解除。此处是当前源码和验收规则；通过与否以匹配构建的真实报告为准。客户端进程退出后的浏览器全组清理另行验收，不能由运行中维护互斥替代。

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

当前源码采用加载策略 `autumn-parser-gate-v1`：每个实例只向真实应用入口导航一次，初始响应仅有宿主HTML、完整挑战脚本和暂停响应的同步gate脚本；此时不提供应用资源，原生WebMessage也关闭。ContentLoading与gate请求到达后读取完整挑战结果，不等待仍被gate阻塞的NavigationCompleted。挑战使用新随机nonce，绑定当前实例ID和精确origin；两个真实原生RTCPeerConnection、ICE协商与restartIce算法完全保留。仅使用字面loopback STUN和relay策略，不提供TURN，不向外部地址发起宿主自检。成功条件仍包括原生ICE服务器列表过滤为空、ICE failed、零候选、restart后仍failed；154聚合connectionState只接受new或failed。

Shell在ExecuteScriptAsync前后核对Core.Source，Runtime保留8KiB严格包裹/字段/nonce/实例/origin/状态校验。另检查仍在loading的可信文档/root闭包，并由原生导航ID、SourceChanged和ContentLoading负责文档失效；闭包不能单独证明document.open没有重置文档。只有仍处于Starting的匹配实例确认原完整proof后才提交gate、开放包资源和SDK。同一HTML parser以JSON编码的原HTML继续解析，不做第二次导航，不使用document.open、DOMParser或API重写。取消、崩溃与关闭必须先撤销会话，再拒绝并完成挂起gate，异常不跳过其余清理。

该加载方式在独立固定154原型中7项真实比较通过；其完整HTML插入具有明确兼容限制：HTML5模式已固定，重复html/head/doctype服从浏览器合并/忽略规则，head属性实测不会保留。入口仅支持严格UTF-8、最大8MiB；依赖head属性、特殊文档模式或精确节点位置的应用需要另验，不能宣称任意HTML透明兼容。旧双文档及constructor-only失败报告保留，最终产品验收单独记账。

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
