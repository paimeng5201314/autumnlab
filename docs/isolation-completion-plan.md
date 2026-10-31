# AutumnOS 隔离层实施与验收计划 · 2026-10-24

制作人：派蒙。本轮目标是完成可运行第三方本地应用的真实边界，再以运行条件替换统一拒绝；本文不是放行依据。只在本地实施，不执行 Git、上传、发布、全局安全策略修改或强杀用户程序。

## 基线与缺件

- 以当前工作区源码为准。根目录 `AutumnOS_Isolation_Implementation_Codex_Prompt.md` 未找到；对话中的完整要求、AGENTS、LOCAL_WORKFLOW、CODEX_START、有效规范与现存报告作为输入，不假称已读缺失文件。
- 固定 SDK `1.0.4258.31`、WebView2 `154.0.4258.53`、策略 `autumn-local-web-v2`；本机历史记录 Windows 25H2 / 26200.8037，每次原生实测另记录实际环境。
- 当前启动阻塞在 `src/AutumnOS.Shell/WebAppHost.cs` 的 `required_boundaries`，包校验后、第三方代码执行前。错误为 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`。旧式 copy/cut 在 Job 剪贴板限制下仍能清空合成剪贴板，不能删除这个失败结论。
- 已有网络响应头、固定引擎、来源/账号/实例绑定、SDK 权限及会话代次检查继续保留。源码检查、可编辑报告、开发者模式和应用自报信息不能生成运行资格。
- 当前交付 update-04 的 EXE/ZIP、用户数据、签名密钥与历史记录保持原位。历史清理删除的报告标记为历史引用，不重新生成并冒充原始证据。

## 分关推进

| 关口 | 实施与可审阅产物 | 通过条件 | 初始状态 |
| --- | --- | --- | --- |
| G0 源码缺口修复 | 文件句柄绑定单权限代次；SDK 事件在产生时冻结代次并拒绝迟到旧决定；确定性回归 | 旧读/写句柄在撤权后重授仍无效，新选择正常；阻塞旧事件再撤权不能报告旧授权；Windows Release 编译及自动测试通过 | passed_code_regression |
| G1 浏览器机制可行性 | 现有受限 token / 新 desktop 元数据 A/B；两文档 RTC 最小反例；随后真实浏览器、显示与输入原型 | 必须同时满足普通用户可初始化、真实 browser/renderer/utility 受限、剪贴板读/写/清空负例及正控、内部动画/键鼠/中文 IME；console 成功不能替代 | mechanism_supported_for_G2_full_matrix_pending |
| G2 生产宿主边界 | 原生独立宿主与限长双向 IPC（仅在 G1 证据支持后采用）；关闭/崩溃/重启；来源与账号数据目录 | 第三方字节执行前边界成功；禁止任意 .NET/命令/宿主目录；真实最终 SDK 投递重新核验实例/文档/epoch/权限，撤权完成与子端确认有明确顺序 | code_preparation_product_execution_not_run |
| G3 实际浏览器验收 | 复用并补齐自有收集器、无害探针、受控账号 A/B、文件/SDK/缓存/worker/关闭故障矩阵 | 每个拒绝项都有通道可用正控、探针执行证据及接收端记录；错误、超时、崩溃、收集器故障不能记隔离通过 | not_run |
| G4 通用启动 | 所有入口调用统一运行资格；真实包/来源身份/必要能力/固定 Runtime/当前实例初始化结果 | 不看名称、分类、认可名单、开发模式或包自报 verified；ChemLeap + 不同来源应用 + 元素配对均通过对应范围 | not_run |
| G5 最终交付 | 新构建 ID、实际源快照、EXE、ZIP 解压副本、不覆盖旧入口的快捷方式、逐项报告与文档 | 普通模式内部游戏、返回桌面、继续/结束/重开、实际保存恢复；最终两个产物各自验收，哈希对应实测字节 | not_run |

G0 与 G1 的代码准备可以并行；G2–G5 不越过前置证据。任何关口失败保留最小反例、实际错误和未执行项；不把整个 T00–T06 标成完成。

2026-10-31限定前置证据：a634私有站六项剪贴板A/B、普通NETONLY元数据与f670三步链共同支持带门禁G2源码接入。f670原WinUI/A/UI/Core/Host均退出0，358实际帧、key27/up5/ACK32/DOM27、真实IME事务与你好/data/ACK/hash/同引擎A原始end模式一致；用户确认上方显示两字后关闭，独立审计SHAc0d26aca38687e7a45efd87631ae3eb65ab4c923c95a206925619c57a4ecc973。7个B进程样本均零能力AppContainer并按原句柄退出，同原Job三观察归零关闭、Normal/SDK0/EOF及profile/desktop/station清理闭合。只认各报告实际范围，不把旧UAC剪贴板六项转记当前普通NETONLY包；本次取消计数0、provider/UIA/全局热键/原生代理剪贴板旁路及故障/完整TSF矩阵仍待验，G1_full_acceptance与产品完整隔离不声明通过。G2首先提取backend并保留required_boundaries，随后处理实际native Post ACK与撤权排序；G3–G5和普通第三方资格仍not_run。

## 本轮运行配置

目标基础配置允许包内 HTML、外置 JS/CSS、图片/字体、WebGL、可从包内脚本字节编译的 WASM、必要 SDK、明确授权的私有存档和系统选择器文件句柄。它不允许应用直接联网。当前 `connect-src 'none'` 也禁止本地 fetch，依赖动态 fetch/CDN 的应用必须报告具体不兼容内容，不能悄改已安装包、版本和摘要。

网络能力需求继续保留；当前 SDK `network.*` 尚不可用。后续受控网络接口必须分别验证身份/权限、真实目的地址、DNS 与连接地址、每跳重定向，使用独立无 Cookie/无宿主凭据/无默认 Windows 认证客户端。商店自身网络不算应用授权网络通过。

禁止能力必须实际不可达，覆盖 fetch/XHR、资源/表单/导航/iframe、worker/Service Worker、WebSocket、WebRTC/STUN/TURN/数据通道、WebTransport、DNS/预连接、回环/私网/重定向，以及摄像头、麦克风、位置、剪贴板、下载、新窗口和外部协议。不同应用/来源/账号的 Cookie、localStorage、IndexedDB、缓存、worker 与进程共享纳入测试。

## G1 的止损条件与权限范围

现有 `RestrictedDesktopLaunchProbe` 是仅编译的控制台元数据探针；不包含真实浏览器、剪贴板 API 或内部呈现。新 desktop 本身仍共享窗口站剪贴板。固定 CompositionController 提供鼠标/指针输入，不直接解决跨 desktop 原生键盘与中文 IME，不能用 SetParent 或最终文字注入冒充完整输入。

真实浏览器原型还需要 STA 消息循环、新私有 profile/临时目录必要 ACL、启动环境约束及真实子进程身份检查。只对新对象实施；不放宽现有 WinSta0、Default desktop 或用户目录 ACL，不关闭 Chromium 沙箱。无明确修复依据的初始化/输入失败即暂停该技术路线，提出有影响说明的 ADR，而不是反复添加例外。

派蒙在代码交接后继续要求“完成整个完整隔离，并且别排除审核一下是不是游戏问题”。本轮据此继续执行已说明的普通权限机制与自有浏览器诊断，并独立核查游戏。既往一次性 UAC 授权不沿用。任何需要提权、新系统组件、全局策略或超出已说明范围的更强进程隔离步骤，先提供具体可审阅实现与影响，再取得相应授权。

2026-10-24 实际推进：G0 首轮构建与914项C#、其余8组检查通过。G1 新desktop元数据A通过、B仍在托管入口前0142退出；两个子进程均自然退出且既有ACL不变。两文档RTC诊断6项中A/C完成，B-constructor宿主读取结果超时、B-offer渲染进程崩溃；没有加载游戏代码，不能归因于ChemLeap。具体报告 `desktop-trial-02/report.json` 与 `rtc-diagnostic-02/report.json` 位于本轮 artifacts。继续用独立原型定位，不以这些失败放行。

第二轮：isolation-03 Windows Release及920项C#、其余8组检查通过；增加闲置句柄撤权释放与后台显式权限租约。constructor-only挑战未解决两文档故障，B两个用例均renderer crash，失败保留。只修改新受限token DefaultDACL的原生变体支持启动并创建/销毁窗口，但desktop实际多出SWITCHDESKTOP权限，原上界断言失败。WRITE_RESTRICTED只约束写访问，不能称完整OS限制；只读执行位必须按实际权限逐项记录。接下来验证单文档完整挑战和真实浏览器，尚未取得生产启动资格。

## 证据和资源控制

2026-10-30 普通权限自动station元数据新机制也已闭合限定证据：prepared-b398ec28...单独授权后一次NETONLY真实成功，无UAC，netonly-case-6a4bfd4689f6410e88a46d82ebf4a471。原父子同user/session/IL8192/privileges无增但AuthenticationId不同，新NULL+CWF station原描述符/原关联/原handle关闭、原process/thread exit0及精确名Open2后查均通过；报告SHA15d8...，LSA状态清理/认证不声明。新上下文内AppContainer浏览器、原生WinUI帧与实际键鼠/IME仍not_run，正在独立prototype实现，不越过G1生产前置。

2026-10-30 真实私有站browser/clipboard机制首次闭合：prepared-a634118b...另获本包一次明确UAC授权并执行，case-db3a01bfa141493594607cbb0f53e4fc。A/B前置及copy/cut/paste六组全部通过，B copy/cut seq19/38不变、paste48不变且真实trusted空事件/最终空DOM，A正控均有效。14Job空、64原句柄exit0、8Normal/SDK0/EOF、30个B观察进程AC1/同SID/caps0/IL≤4096，原DACL哈希恢复、profile删除/path/SID及独立station错误2后查均通过。报告SHA6afbd43b955bb30da856df34652de0948c0e3591e0edced0b6c6b38d9a37aaa3，严格限定mechanism_only/sample_not_exhaustive，不把本项等同生产G1。普通无UAC启动与内部画面/键鼠/中文IME仍未闭合；G2–G5不越过前置。

2026-10-30 读取权限七组包prepared-d9d1c087...已获本包明确单次UAC授权并执行，case-d908454c7e6f4600aca98998eb18da70。A23前后成功，B23前后均USER321114，B仅增加READ_CONTROL的20023即三DLL/绑定/exit0成功，20123/20323也成功。同站/desktop87/native字节/B同SID，7Job空/原退出/EOF，原DACL恢复hash一致、profile path/SID及station不存在后查通过。当前原生初始化缺READ_CONTROL得到可复现对照，不再为此增加额外ENUMERATE/READSCREEN；实际browser、clipboard、内部输入仍not_run。下一最小20023真实browser/六case包继续代码准备，新UAC单独授权；G1仍未passed。

2026-10-30 普通权限desktop单变量矩阵已完成：A87/B87/B200C7三组均三DLL、实际WinSta0/新desktop绑定和原exit0通过，B真实AC1/IL4096/caps0/各SID，三Job0/EOF、两个profile删除及path/SID不存在、三个desktop后查2、原主线程context不变。报告desktop-native-runs/invocation-7974bb8839c74ad88d22d91480a39c4a/desktop-native-case-47179f5c098944399f5a5da5fdd25453/report.json。0x87在本轮WinSta0前提下足够，因此不把增加desktop权限当修复。只读WinSta0过滤ACE/Low标签另有真实报告；下一native_station_read_matrix七组包已代码/构建/45纯+2真实pipe回归/普通native入口拒绝路径通过并冻结为prepared-d9d1c0879d2c4d1b8826503818a4fb09，私有站新UAC尚未授权、矩阵not_run。仅变更本次新station的三个读取位，包含明示READSCREEN与恢复23重测；无clipboard位或browser/clipboard动作。详见ADR0008。G1不提升。

2026-10-30 原生DLL分层定位已按prepared-5b288af50a4d443281adbecc6c1fd0c0的新一次明确UAC授权执行，case658a0a55f67847039f9694c519b1582a。A三DLL和精确station/desktop绑定通过；B进入KERNEL32-only入口后加载USER32失败1114，未启动browser或调用clipboard，保持0x23/0x87及Low标签。这定位到USER32初始化阶段，未定位具体权限原因；停止无依据扩权试错，继续核查最小前提与架构影响。真实A/B原进程/Job空、EOF与资源退休、精确profile删除及原路径/SID不存在后查、controller原对象关闭和broker原句柄退出/release均有报告；失败没有改写成隔离通过。该次授权已消耗，后续新的UAC仍须新的具体授权。G1状态不变。

2026-10-30新增真实机制证据：普通A与零能力AppContainerB均在固定WebView2下运行自有WASM42/60帧；B宿主及已观察browser/GPU/renderer/utility等8进程的OS token均AC1、同profile SID、IL4096、capability0。全部原持有句柄exit0、Normal事件和环境进程集0共同成立，新profile删除后原路径错误3及同SID后查80070002。见 `artifacts/isolation-completion-20261030/runs/browser-case-ce7a104caa46431595a2fcaa5d014d33/report.json`。原CUI附属conhost导致的两次正控失败保留，WinExe修复没有新增树外放行规则。本项仅sampled_not_exhaustive浏览器机制通过，真实剪贴板、内部呈现/输入/IME及生产宿主仍未验收，G1不提升为passed。ChemLeap0.1.2独立生命周期修复及58项逻辑/内存夹具测试不代替这些原生边界。新的私有窗口站验证草案只准备/编译，尚未执行新UAC或剪贴板操作。

随后v3另获一轮限定授权：B1精确CreateProcess203，B2只补LOCALAPPDATA即通过真实AppContainer原生机制对照，两个正常child均原句柄exit0、精确profile清理成功，原失败保留。该结果解决了本轮原生机制的环境前提，尚未证明WebView2/browser/renderer/utility、实际剪贴板或生产内部呈现；G1不提升为passed。下一浏览器原型先完成源码/固定版本核查与影响说明，再按对应授权执行。

2026-10-27最新结果：Build11零警告/错误、C#982及全部9组检查、源码86项通过；EXE/ZIP解压副本各53项，完整三项正常关窗31检查通过。真实WebView2 WebTransport A/B32项完成，准确网页执行/CSP事件与分离接收端相互印证，两端正控和实际组退出完整。受控AppContainer新机制一次授权已执行，A通过、B启动前203，精确profile清理成功；仍不提升G1。新开发验证产物只完成基础回归交付，不代表G4/G5的普通第三方验收。新运行、单变量候选或更强步骤仍须按对应明确授权执行。

2026-10-27续验：Build10已实际完成Windows Release零警告/错误、C#980项及其余8组检查、源码86项。首次原生退出矩阵在单文件展开261字符路径时失败，具体为Framework入口PathTooLongException；短路径独立回归与产品长路径修复分别验证，不改写失败。三SID受限进程的自有Local命名互斥量创建错误5是新的独立最小反例，文件锁正控成功，仍没有剪贴板/浏览器生产资格。下一种机制只准备可审阅代码；不得直接注册AppContainer或修改已有命名对象ACL。

最新推进（2026-10-24）：Build07修复Control关闭后提前释放维护租约，单文件/ZIP解压副本各53项原生回归通过。G3独立网络、同字节worker正反例、受控身份五类浏览器存储、同文档替换策略保持已有对应实测，仍不提升G3全矩阵。原双SID限制导致BCrypt加载1114；只读实际对象矩阵后，附加World的全新三SID原生RNG A/B各18阶段通过，WinSta0剪贴板位仍拒绝，浏览器与真实剪贴板尚待验证。该新增OR身份可能扩大其它已有对象写权限，不能称完整OS沙箱；没有修改已有对象ACL。

正常关闭主窗需要额外保留消息循环与锁直到全部已准入资源完成；现已实际编写不可逆准入、原生picker/ContentDialog完成跟踪、商店/更新/身份异步drain，下一构建需真实前台/后台/picker关闭复验。保存导出驱动的现代窗口消息写入被真实默认名称返回反例证伪，测试暂停，导出/覆盖保持未通过。当前普通ChemLeap和另一来源第三方入口仍未验收，门禁不解除。

第三轮：isolation-05已接入生产parser-gate，Windows Release及964项C#、其余8组检查和86项源码检查通过；分开接收端的7项独立比较中A-offer应用实际2包、宿主8包，B/C/R两端均0且各自前后正控有效。产品单文件元素配对49/49通过，范围为实际启动/授权保存/后台继续/结束/启动器重启，不含新游戏实例读取。受限浏览器父程序却被Windows应用控制在Main前拒绝（精确未签名DLL，0x800711C7）；浏览器受限token继承、剪贴板和内部呈现仍not_run。此阻塞不能靠更新清单签名、自签信任或变更系统策略解决；当前冻结草案、签名后需重新绑定子文件哈希的流程及失败证据见本轮browser-default-startup-blocked-01。完整隔离和普通第三方放行仍未完成。

- 输出放在本轮独立的 D 盘 artifacts 子目录；进程内 TEMP/TMP/NuGet 临时目录指向项目 `.tools`，不改系统环境变量。
- 编译复用 `.tools/dotnet`、锁文件与现有 Build/Test/打包脚本。固定 Runtime 尽量只读复用，不反复复制整个引擎。
- 报告按每项 passed / failed / not_run，记录源码快照、二进制摘要、SDK/Runtime/Windows/策略版本、原生 PID 与退出证据。敏感身份、真实文件内容和令牌不进入报告。
- 只使用自有 loopback 收集器与合成数据，不扫描局域网；DNS 与可信 WebTransport 如缺真实正控，明确 not_run。
- 新包原生测试前提醒派蒙正常退出旧启动器，确认单实例没有唤回旧版。测试不强杀，关闭异常保留 PID 与失败记录。
- 受控测试账号只证明命名空间隔离，不冒充 Logto 两个真实账号验收。
- 只有 G1–G5 真实通过才称隔离完成、第三方普通模式可运行；Windows Authenticode/SAC 与应用隔离是不同问题。

具体执行结果追加到 `artifacts/isolation-completion-20261024/` 和 `AUTUMNOS_PROGRESS.md`，本计划状态随真实结果更新。
