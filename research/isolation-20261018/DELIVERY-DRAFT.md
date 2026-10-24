# 本轮隔离交付前草案（不是验收通过声明）

制作人：派蒙。2026-10-18，基于当前本地源码与已经生成的报告，只读整理；本文件未启动 GUI、未改变系统策略、未读取真实存档或凭据。根任务文件 `AutumnOS_Isolation_Implementation_Codex_Prompt.md` 缺失，不能声称已读。本轮任务以用户完整指令为准。

当前不能写“ChemLeap 普通模式已可正常运行并通过本轮验收”。最新已产生证据为 Build03：编译、自动测试及目录 Prepare 通过；native-09 因窗口最小化触发安全输入护栏失败，native-10 已实际触发当前文档失效保护并关闭实例，但旧脚本仍期待保留原页面而失败。不能将两者与 Build02 native-08 的未处理错误页混为同一缺陷。剪贴板 UAC02 六项真实对照已全部执行，4 passed / 2 failed；受限令牌候选也在子进程初始化失败，均不是可交付边界。Build04 和待授权请求期间页面/会话失效验证正在准备，**验收 not_run**；普通模式游戏与最终 EXE/ZIP 验证仍为 **not_run**。以下按已落盘报告记录，后续修复另建新 BuildId 和报告，保留所有失败。

## 当前可定位的产物和证据

- BuildId：`T06-20261018-isolation-03`；源码快照：`sha256:2580ee29f8e8672f02a2f7a51e49367d5b25133ed6ef01323bd7f440cd71e610`；Release/win-x64；版本 `0.5.2-local.4` / 显示版本 `0.0.5-20261018-local`。Build04 正在准备，不从计划推定成功。
- 环境：`Microsoft Windows NT 10.0.26200.0`；项目 .NET SDK `10.0.401`；NuGet WebView2 SDK `1.0.4258.31`；Windows App SDK `1.8.260921001`；固定浏览器 `154.0.4258.53`。
- 当前**中间完整目录 EXE**：[AutumnOS.exe](<WORKSPACE>/artifacts/packages/T06-20261018-isolation-03/AutumnOS-0.5.2-local.4-win-x64-development/AutumnOS.exe)。SHA256：`b9b11dc882d6e65f6ce97a2ef58e6ba37d76daa035c9bbcc158cd4318dc9ea12`。这不是最终通过验收的单文件交付。
- 编译和自动测试：[build-result.json](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/build-result.json)、[tests.json](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/tests.json)。837 C# 用例、SDK、样例、原生身份、HTTP、CLI、机器合同和 ChemLeap 模型组报告均为 passed；这些不替代真实产品边界验收。
- 准备报告：[package-prepared.json](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/package-prepared.json)，状态 prepared，native_tests=not_run、zip=not_created、setup=not_created。实际完整目录字节：[delivery-hashes.json](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/delivery-hashes.json)。
- 主产品报告：[native-09](<WORKSPACE>/artifacts/isolation-20261018/native-09/runtime-isolation-native.json) 11/11 已记录检查通过，总 failed；[native-10](<WORKSPACE>/artifacts/isolation-20261018/native-10/runtime-isolation-native.json) 14/14 已记录检查通过，总 failed。失败原因见下文；两份 cleanup 均 passed。Build02 的 [native-08](<WORKSPACE>/artifacts/isolation-20261018/native-08/runtime-isolation-native.json) 与截图继续保留，不回填通过。
- 防火墙提示诊断、用户操作来源和辅助 flag 对照：[FIREWALL_FINDING.md](<WORKSPACE>/artifacts/isolation-20261018/FIREWALL_FINDING.md)。早期策略探针：[PROBE_HANDOFF.md](<WORKSPACE>/artifacts/isolation-20261018/PROBE_HANDOFF.md)。
- 剪贴板已执行失败：[clipboard-station-uac-02/report.json](<WORKSPACE>/artifacts/isolation-20261018/clipboard-station-uac-02/report.json)；新非交互窗口站租约正常释放：[report.json.lease.json](<WORKSPACE>/artifacts/isolation-20261018/clipboard-station-uac-02/report.json.lease.json)。两项失败不是超时、未执行或测试正控无效。
- 受限令牌启动失败：[restricted-token-launch-01/report.json](<WORKSPACE>/artifacts/isolation-20261018/restricted-token-launch-01/report.json)。普通子进程正控与受限令牌创建成功；B 的 CreateProcessAsUser 返回成功，但子进程 exit `0xC0000142`，没有探针 JSON。浏览器兼容性、剪贴板隔离和不可信页面执行均 not_run。

## 当前真实实现与支持范围

策略为 `autumn-local-web-v2`，不是应用或报告自行声明的认证。包内 HTML、JS、WASM、资源及 manifest 都不可信。包完整性与来源身份通过后，宿主必须先完成固定浏览器检查、设置与事件注册、宿主控制的原生挑战，才提供应用资源。版本不匹配、覆盖配置、初始化或挑战失败均拒绝加载。结论限于记录的 Windows、SDK、浏览器字节和策略，不声称抵御浏览器零日或已控制宿主的管理员。

[RuntimeIsolationPolicy.cs](<WORKSPACE>/src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs) 与 [WebAppHost.cs](<WORKSPACE>/src/AutumnOS.Shell/WebAppHost.cs) 的当前行为：

- HTML/JS/CSS/图像/字体来自已校验的包资源字典，以随机实例 HTTPS origin 的宿主响应提供。每个响应附宿主 CSP、Connection-Allowlist、Permissions-Policy、禁止 DNS prefetch、no-store、nosniff 和无 referrer。没有依赖虚拟目录映射触发拦截，也没有使用任意文件或网络代理。
- 允许包内外部 JS/CSS 和本地游戏。允许 JS 中已有字节编译 WASM；不允许任意 JS eval。`connect-src 'none'` 也阻止同源 fetch/XHR，因此 **fetch 本地 JSON/WASM、instantiateStreaming 等方式不在当前配置支持范围**。需打包为可直接加载资源或 JS 字节；不得偷偷改已安装包和旧哈希。
- 禁止直接 fetch/XHR、WS、WebTransport、资源外联、导航/表单/iframe/worker/Service Worker 等旁路；RTC 依靠实际浏览器原生 `Connection-Allowlist: (response-origin); redirects=block; webrtc=block` 与绑定实例/nonce/origin 的强制原生挑战。`WebResourceRequested(All, All)` 是附加拦截，不能单独证明这些通道全被覆盖。
- `--disable-features=WebRtcHideLocalIpsWithMdns` 只减少此固定引擎创建 mDNS 入站监听器的副作用。正式网络边界仍是原生响应策略、强制挑战和接收端证据；“没有防火墙弹窗”不是“没有网络泄漏”。早期用户手动点击允许的规则保留，代理不更改或删除。
- 浏览器 profile 由账号与应用绑定哈希派生，ExclusiveUserDataFolderAccess+InPrivate，核验实际用户目录和浏览器路径。应用绑定包括 appId 与来源身份；同名 appId 不应跨来源共用宿主存档或浏览器数据。每次实例使用随机 origin。[WebViewProfileDirectory.cs](<WORKSPACE>/src/AutumnOS.Runtime/WebViewProfileDirectory.cs) 中缩短路径不等于删掉账号或来源维度。旧缓存保留，当前独立命名空间不读取它。
- SDK 由真实顶层 WebMessage 来源、当前 Core.Source、实例、账号/sessionEpoch 与权限绑定；应用自报 appId 不被信任。迟到成功响应还要通过存活和权限检查。存档/私有数据/文件句柄由宿主按账号、来源、应用约束，不暴露任意 .NET 对象、系统命令或目录。
- 设置与事件拒绝摄像头、麦克风、位置、通知等浏览器原生权限、新窗口、下载和外部协议。异步 clipboard-read/write 在 Permissions-Policy 中禁用；**旧式编辑通道有真实失败反例**：UAC02 的生产响应头+浏览器 Job UI READCLIPBOARD|WRITECLIPBOARD=6 阻止 paste 读取，但 copy/cut 仍能清空隔离站的合成剪贴板。不能因未写入目标文本或 Job 设置成功而判通过，也不能集成该 Job 作为完整拒绝边界。
- 当前能力返回包含 lifecycle、permissions、identity.profile、saves、storage、preferences、受限 files.open/save，以及配置存在时的 appearance/notifications/shortcuts/widgets/links.internal。能力列表表示有宿主入口，实际执行仍需声明、授权、会话、手势及参数检查；不应把整个列表写成全部本轮真实验证通过。
- **第三方宿主网络 SDK 当前不支持**：没有已交付并验收的 `network.request`。授权联网正向验收为 not_run，联网需求保留。后续要建立应用/账号/权限/目标绑定，校验每次重定向和实际连接地址，禁带宿主 Logto 凭据、Cookie 或 Windows 默认认证。不能因“所有直接联网拒绝”删掉该原需求。

## 启动链核查

[StoreApplicationsUi.cs](<WORKSPACE>/src/AutumnOS.Shell/StoreApplicationsUi.cs:163) 的 `OpenInstalledApplicationAsync` 是已安装普通应用共用入口。商店已安装行打开（65）、桌面图标（230）、上下文打开（237）均调用它；本地文件安装注册后使用同一个已安装应用入口。它先检查 web runtime/来源，取得运行 lease 后重新读取注册并再次检查，再创建 `WebAppHost`；后者重新校验包完整性并做前述安全初始化。

当前 github/local/bundled 不通过开发者模式或 manifest `sandboxVerified` 放行；受控 test 来源有单独测试源条件。PM/SQ 分类不是安全认证。ChemLeap 保持 github 身份，没有改成 bundled，没有认可名单。

本轮不能声称已有任意深层链接启动入口全部完成验证：[DesktopExtensionService.cs](<WORKSPACE>/src/AutumnOS.Runtime/DesktopExtensionService.cs:227) 的 `links.openInternal` 是当前实例声明的 action；[RuntimeServicesUi.cs](<WORKSPACE>/src/AutumnOS.Shell/RuntimeServicesUi.cs:43) 当前只将已存活 bundled/developer 实例切到前台，没有从任意 appId 创建新 host。managed 第三方 action 前台激活未覆盖；源码中没有发现另外一个已实现的外部 URI 启动任意第三方包入口。已存在普通启动入口代码统一，所有 UI 入口的真实回归仍需报告。

## ChemLeap 与不同来源应用

已安装 ChemLeap 真实 manifest：[manifest.json](<WORKSPACE>/AutumnOS_Data/Apps/.content/9fc0108a0b0e4b1f9852b8c5c8311bb8/cn.labchronicles.chemleap/0.1.1/manifest.json)。appId=`cn.labchronicles.chemleap`，版本 `0.1.1`，runtime=web，entry=index.html，仅申请 saves；真实来源 `github:1414175562` / `paimeng5201314/chemleap`，包 SHA256 `e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2`。

入口引用的 game.css、autumn-sdk.js、chemistry.js、model.js、renderer.js、game.js 均为包内文件；8 个资源记录先前核验匹配。chemistry.js 的外部 URL 是资料引用数组，没有作为当前运行资源加载。game.js 实际使用 `saves.read/write` 的 `game` 槽；只有无宿主时才使用 localStorage，宿主报错不降级为浏览器存储。清单与资源具备当前本地游戏配置所需条件；**普通模式打开、跳跃、返回继续、结束重开和实际读写存档仍 not_run**，不能由静态检查推成通过。未修改用户已安装包，未读取真实存档。

通用性夹具：[tests/fixtures/normal-local/manifest.json](<WORKSPACE>/tests/fixtures/normal-local/manifest.json)，appId=`cn.labchronicles.normallocal`，名称“本地隔离配对”，版本 `1.0.0`，仅 saves。驱动从真实本地安装入口选择新生成 .autumn，要求注册 source=local、hostSource=`local:cn.labchronicles.normallocal`，与 github:1414175562 不同；不改用户包。实际 Windows 运行仍 not_run。元素配对回归也必须保留，不用该夹具替代原样例的回归。

## 真实报告能证明什么

`probe-final2-*` 是加入 mDNS 辅助 flag 前的真实固定引擎对照，保留作发现与修复依据，**不是最终 Build02 的完整重新验收**：

- resources-storage 25/25，包括真实本地资源/JS/WASM 正向、JS eval/本地 fetch 拒绝、5 种浏览器存储正向读写，以及不同受控账号/应用/来源独立 profile 与 browser process、InPrivate 关闭重开无串用。受控测试身份不能冒充真实 Logto 双账号。
- network 对照 A 收到 TCP 20、UDP 43；B 接收侧 TCP/UDP/DNS 观测 0，并有前后正控。覆盖仅限探针实际通道和自有接收器。任意外部权威 DNS、可信证书 WebTransport 完整握手、其他 Windows 环境仍 not_run。
- same-origin 真实反例证明 `connect-src self` 会允许原生同源 WS/WT，当前 `connect-src none` 阻止；不可用“源码不存在 API”替代这个结果。

随后 [probe-matrix-03/summary.json](<WORKSPACE>/artifacts/isolation-20261018/probe-matrix-03/summary.json) 已对 Build02 关联的新共享 mDNS 参数实际重跑四组独立固定引擎探针：runtime-ab、resources-storage、network、same-origin 均 passed，binding_errors 为空。资源/存储 25 项；网络有自有接收器、15 个逐通道正控和主机 NetLog；网络/同源模式从 NetLog 读回实际命令行确认共享 flags 与未禁用沙箱。runtime-ab/资源模式仅记录 API 配置参数，未捕获完整实际命令行。该矩阵仍不是 Build03/04 主产品完整验收。

Build02 `native-08`：通过主产品真实窗口、包确认与哈希、原生挑战早于包页面、SDK 未声明权限/身份伪造/未知命令/重复请求拒绝、浏览器权限拒绝或明确不可用、新窗口被阻止且原文档保持。日志已出现 `navigation_denied`，随后 `navigation_completion_ignored`；外部导航之后读取原探针观察字段超时 15 秒，截图对应错误页。**导航没有完成原文档保持验收**，不能因为触发拒绝回调就记为通过。

该报告明确保留 network_negative、declared_permission_denial、navigation、sequential_instance_storage、controlled_account_browser_storage、close_inflight_readonly、concurrent_BroadcastChannel、real_account_switch、initial_aboutblank_RTC 为 not_run；商店普通启动需要单独驱动。cleanup=passed。修复后完整主产品复测尚未完成，不能回填本报告状态。

Build03 **native-09**：在 11 项记录通过后，浏览器权限按钮输入阶段因窗口已最小化而被输入护栏拒绝。稳定性记录 client origin 为 `(-32000,-32000)`、client size 为 `(0,0)`，目标坐标不可用；脚本没有继续点其他窗口。总状态 failed/cleanup passed，这是输入前置条件失败，不是某个被点击通道的拒绝通过。后续未到达用例仍 not_run。

Build03 **native-10**：14 项记录通过，包括 popup 与 download 拒绝后原页面保留；随后禁止导航触发日志 `navigation_denied → document_binding_lost(state=Crashed) → closed(state=Crashed)`。**当前文档失效保护已实际生效并关闭实例**，不再留下可用旧文档会话。脚本仍调用 `Assert-IsolationPreserved` 等待旧页面观察字段，15 秒后超时，因此总状态保留 failed/cleanup passed。应修正测试以真实验证该终止行为、旧文档不能继续使用 SDK，以及有待授权响应时的失效拒绝；不能直接删断言或把本次总状态改 passed。新源码与 Build04、pending-permission 场景的真实验证尚 not_run。

**剪贴板 UAC02 已执行 6 项，4 passed / 2 failed，总状态 failed。** 测试在新建、非交互的 `AutumnOS_ClipboardProbe_*` 窗口站和 OwnedDesktop 中运行；探针与浏览器为同用户普通权限，UAC helper 只提供经认证的新站租约。只读取/写入该站的合成数据，报告明确 WinSta0 剪贴板从未打开、读取、清空或写入。CDP 只向自有测试页提供鼠标和 Ctrl+V 激活；页面报告 trusted、userActivation、focus 与本地完成，剪贴板标记只由原生端设置，CDP 不是安全边界。

- A 无浏览器 Job 的 copy、cut、paste 三个正控全部 passed，证明通道实际执行且原生接收端可观察。
- B 带生产响应头及浏览器 Job UI=6 的 paste passed：页面收到空文本，合成剪贴板基线与 sequence 51 保持。
- B copy failed：合成基线被清空，原生接收端值 null，sequence 22→23；B cut failed：同样清空，sequence 40→41。两个页面命令均有真实激活与执行完成，不能把空值误当成功拒绝。
- 六个独立浏览器均正常退出且无 ProcessFailed；helper 释放 exit 0，probe 因安全断言失败 exit 1。租约 lifecycle passed 不表示剪贴板边界 passed。该失败候选未集成；必须另行修复/设计边界并保留上述最小反例，再做正式复测。

**受限令牌只读启动试验 restricted-token-launch-01 总状态 failed。** A 使用本人的普通令牌启动，exit 0 并返回经验证元数据；新增 S-1-5-12 restricting SID、DISABLE_MAX_PRIVILEGE 的受限主令牌创建成功。B 的 `CreateProcessAsUser` 已成功创建子进程，随后子进程自然 exit -1073741502（`0xC0000142`，初始化失败），stdout 无 JSON，无法验证其实际进程令牌/窗口站元数据。不得写成 API 权限拒绝，也不得将进程创建成功写成可用隔离宿主。A 只查询 WinSta0 窗口站句柄授予掩码，没有任何剪贴板 API 调用；该试验无 UAC、ACL/全局策略改变、窗口站关联改变、GUI/WebView 初始化、强杀或 breakaway。WebView 兼容性、clipboard isolation acceptance、untrusted content 均明确 not_run。

## 建议最终运行顺序及脚本覆盖缺口

顺序中每个 GUI 驱动都应单独运行，前一个正常退出并确认自身进程清理后才开始下一个。开始任何新包前提醒派蒙正常退出旧启动器；测试不得强杀用户程序或清除真实数据。

1. 完成文档失效终止/待授权响应验证与剪贴板边界修复、所需源代码/脚本/用户与开发者文档更新；确认测试断言未删减。冻结新的源码快照与 BuildId，执行既有 Build/Test，再 `Package-T05.ps1 -Stage Prepare`。目前正准备 Build04，不能预记成功；任何已构建快照之后改 tracked 文件会导致包脚本快照核验拒绝，这是正确行为。
2. 在新固定引擎/相同策略参数上重跑网络、资源/存储、同源对照，保留宿主与接收器证据；在 Prepare 的真实普通完整目录上重跑 `Test-RuntimeIsolationNative.ps1` 和必要剪贴板正式验收。所有失败保留，未到达项目仍 not_run。
3. 运行 `Test-ChemLeapStoreSmoke.ps1 -ExecutablePath <目录AutumnOS.exe> -ReportDirectory <新目录> -TestRoot <新短路径>`；运行 `Test-NormalLocalApp.ps1` 同类参数，验证开发者模式始终 Off、真实安装来源、正常游戏与 SDK 存档、后台继续/结束和重开读档。两个驱动复制完整目录到独立新数据范围，校验原始/测试 EXE 和 receipt/源码关联，不碰用户真实存档。
4. `Package-T05.ps1 -Stage Finalize` 会对当前源码/冻结交付字节再核验，要求当前构建的六组自动测试及 T06 机器合同；普通交付不允许 `-SkipNativeRegression`。它实际顺序执行 StoreSmoke、DesktopInteractionSmoke（保留元素配对）、DeliveredLauncher，然后 ZIP 写入与每个条目摘要回读。
5. **Package-T05 不会自动执行本轮隔离、ChemLeap、NormalLocal，也不会解压 ZIP 后再启动。** 新目录解压最终 ZIP，逐文件对 delivery-hashes 核验；对解压原路径执行 `Test-DeliveredLauncher.ps1`，并把解压目录 EXE 作为 ChemLeap/NormalLocal 的输入再跑必要普通模式验收。后两个驱动会复制完整解压目录到隔离 TestRoot；报告须区分“从 ZIP 字节副本游戏验收”和“解压原路径实际启动”。
6. 若最终交付仍要求单 EXE，执行 `Package-SingleFile.ps1 -BuildId <最终ID>`。它从 Prepare 清单取字节、去 PDB、生成压缩 payload/哈希校验与单文件入口，只生成 prepared 报告，不自动做 GUI 验收或外层交付 ZIP。`Test-SingleFileSmoke.ps1 -BuildId <ID> -ReportDirectory <新目录>` 校验原生首次提取、元素配对、存档、后台继续、重启与单实例；`-InPlace` 只用于新的交付目录且要求首次只有 EXE。
7. ChemLeap/NormalLocal 支持 `-SingleFileBuildId <ID>`，校验确切包路径、大小/哈希和实际提取 payload，正常关闭双层 bootstrap。**它们当前只接受 package-single-file.json 中原始 EXE 路径；直接传外层 ZIP 解压 EXE 会因绝对路径不等而拒绝。Test-SingleFileSmoke 也没有任意 ExecutablePath 参数。** 若交付单文件 ZIP，需保留字节等价与包来源证明的独立解压副本测试入口，或准确记录人工验收；不可把“原始单 EXE 已测”当作“ZIP 解压副本已测”。
8. 最终产物/报告摘要、签名状态、本轮剩余 not_run、窗口站/Job 等额外系统需求如有，都记录到用户/开发者文档与 AUTUMNOS_PROGRESS.md；建立含版本/BuildId 的新快捷方式，验证目标、工作目录与快捷方式启动，不覆盖根旧入口或旧快捷方式。当前本轮最终 EXE、ZIP 和新快捷方式均未完成。

NormalLocal 文件 picker 的具体限制：`Find-NormalLocalPicker` 当前只接纳产品同 PID、class `#32770`、owner 链回到产品窗口的对话框；新 WinAppSDK FileOpenPicker 可能由独立 PickerHost 承载，因此不能假定自动选择可靠。现有 `-ManualPicker` 可让派蒙只选脚本显示的生成夹具，随后由脚本核验产品安装确认、包身份与后续流程。若扩充自动化，必须绑定本次 picker 激活、PID/创建时间、系统签名路径与 owner/关联元数据；不能接受任意系统 PickerHost，也不能把 Windows 安全中心当文件选择器输入。

## 交付前最少不可遗漏项

- 基于 native-10 已观测的失效终止行为补齐 Build04 真实验收，尤其待授权响应期间文档失效；保留 native-08/09/10 失败并完成主产品剩余网络、存储、撤销、旧会话与关闭用例。
- 解决 UAC02 已证实的 copy/cut 清空反例；不能集成 Job UI=6 作为完整边界，也不能把 4 passed / 2 failed 改成 pending/not_run。修复后需保留全部正反例再验收。
- 冻结后重建，使用新参数重跑对照；旧报告有价值但不是最终构建验收。
- ChemLeap 普通模式、另一 local 来源普通模式与元素配对必须真实通过；联网不支持和深层链接现有范围明示。
- 最终目录/单 EXE/实际 ZIP 解压副本各自证据、哈希与快捷方式；打包脚本成功不替代这些产品测试。

本轮通过也不代表 T03 真实 Logto 双账号、授权第三方联网、所有历史阶段或所有 Windows/浏览器环境全部完成。
