# Lab Chronicles AutumnOS 本地开发记录

制作人：派蒙。工作区：`<WORKSPACE>`。本记录只写本地事实，不代表完整产品已交付。

## 当前阶段

2026-10-24 派蒙明确授权“将所有内容以派蒙的身份上传到github对应仓库”，本轮已完成。GitHub连接与本机凭据均核实为paimeng5201314（派蒙），对应paimeng5201314/autumnlab；提交作者/提交者、Release作者和10个附件上传者均归该账号。源码/SDK/规范/自有隔离探针/保留报告共577公开文件，经远端Git blob逐项核对；main源码提交95b830c24f1ee396c8eb2605948ac63e4c69c64a，发布材料提交753b26b13869fd27cd6e70122cc9113cf08b145e，保留17个远端既有LICENSE/工作流/历史文件。预览Release meta-v0.5.2-local.9（422539615）于2026-10-24T06:05:56Z公开，未设Latest，原稳定Latest保持plus0.0.4-20261017。EXE、公开交付ZIP、两份源码ZIP、payload、真实绑定清单/签名及校验说明全部上传，10项远端长度/摘要/ID/身份核验passed。签名使用既有D盘生产身份，真实payload Asset ID671383797，完整载荷验签通过；未导出或上传私钥、凭据、真实用户数据。

发布后真实UpdateService检查已识别并验签meta-v0.5.2-local.9。诊断用新客户端根+明确标识的较旧版本参数0.5.2-local.8识别候选；实际当前版本0.5.2-local.9不提示同版本升级。旧发行缺清单警告仍保留，checkComplete=false，不宣称全部历史更新可安装。两次下载0、installation=not_run，GUI升级/回滚未执行。Windows Authenticode仍NotSigned，ChemLeap原隔离缺口仍未完成。新公开ZIP仅脱敏8份文本，原EXE与本地ZIP保留；当前源码对构建快照只有维护文档密钥迁移说明变化。初次草稿标签变化被严格签名前校验拒绝，修正tag后重新读取确认，失败证据保留。完整记录 `artifacts/github-upload-20261024/UPLOADED.md` 和 `published-release-verification.json`，GitHub发布页 https://github.com/paimeng5201314/autumnlab/releases/tag/meta-v0.5.2-local.9 。本次授权只用于本轮发布，没有初始化工作根Git、改全局策略或启动云端构建，旧入口和用户数据未覆盖。



2026-10-24 F/D清理已完成。派蒙明确授权D盘旧测试和构建也清理、当前交付与报告保留，并确认正常退出startup-02；没有强杀用户程序。F盘清理1559500592字节测试/缓存，签名密钥2906字节已迁D；D盘按核对清单删除217518文件、189902731821字节（约176.86GiB），两个旧负面测试junction仅移除链接本身，三个旧测试快捷方式已移除。当前update-04 EXE、ZIP、解压副本、源码ZIP及原根入口哈希复核通过；65个AutumnOS_Data根原位保留，根真实数据与D盘私钥目录不在清理范围内。工程源码/SDK/开发者项目/唯一探针源码与当前报告依赖保留。详见 `artifacts/update-fix-20261024/CLEANUP-HANDOFF.md`、`cleanup-final-verification.json`、`d-cleanup-result-04.json`。大量旧生成物/旧报告已依本轮授权删除，以下历史记录引用的部分路径不再存在；这不改变历史失败或not_run状态。当前GUI、在线更新安装及第三方隔离没有新增验收。密钥实际位置为 `<PRIVATE_SIGNING_DIRECTORY>`。

2026-10-24 派蒙明确授权“签名密钥转移到D，删除所有测试资料所有残留”。密钥已安全迁到 `<PRIVATE_SIGNING_DIRECTORY>`：新目录创建时仅当前用户ACL，两个文件受保护字节一致，实际调用本地签名工具完成DPAPI解密及现有公钥匹配验证后删除F盘原件与已空父目录；未输出私钥明文、未更换签名身份。详见 `artifacts/update-fix-20261024/signing-key-moved-to-D.json`。维护文档已更正实际位置；旧交付ZIP中的文档是迁移前快照，EXE/ZIP字节未改。F盘其余明确测试残留正在核对清理；D盘历史测试/构建是否也删已单独询问，未答前保留。

2026-10-24 派蒙询问本轮F盘新增/修改及删除。已按源码、报告和文件元数据核对：正式更新身份目录在LocalAppData，仅public-trust.json 884字节、signing-key.dpapi 2022字节；暂保留，已单独请求迁移/保留/永久删除选择，未读私钥内容。update-03/04测试临时密钥已自动清理；现已非递归删除两个经确认的空测试父目录，并精确删除11:05恢复依赖期间刷新的四个NuGet元数据缓存，共759780字节。旧共享缓存、用户截图、存档及系统组件保留。报告 `artifacts/update-fix-20261024/F-DRIVE-CHANGES.md` 与 `f-drive-cleanup-7645ab51696c4fdabd39e23ab205dbdf.json`；不宣称全盘完整文件审计。

2026-10-24 更新页修复已交付本地核验包 `T06-20261024-update-04` / `0.5.2-local.9` / 显示 `0.0.5-20261024-update`，源 `sha256:5bcc9db971e6cda62f823b8301a1bb8e6bcf788a1c11505e189213ef1b421a58`。派蒙新截图证明startup-02已进入设置页；本轮按“完成代码核验后交本人实际检测”未启动产品GUI。建立独立RSA-3072生产更新根autumnos-release-20261024，DPAPI CurrentUser私钥在工程外，普通构建只嵌公钥。更新服务准确区分Release读取与完整更新验证、缺清单/签名/载荷/重复资产，UI去除重复错误。正式准备工具核对原目录完整文件集合、独立stage和最终1703文件清单，保留签名、来源、安装确认与二次验签。真实公开源检查configured=true、读取2条记录、UPDATE_MANIFEST_MISSING、下载0；线上现有发行没有协议所需清单/签名/payload，公网自动安装仍未完成。

本轮验证：Windows Release零警告零错误；C#890/890、生产更新CLI17/17、其余七组自动检查及86项静态检查passed；实际最终二进制六处公钥/BuildId/源摘要一致且没有测试根；EXE/ZIP18文件回读及解压副本、尾部布局与payload摘要passed；439文件源码ZIPpassed。EXE SHA256 `6db3dceec996df71fe0030a7f006d4c2ac18f383c17e538d2865b78cb84dfe11`，ZIP `9315d6c7bdddb5709b3e129f6313936fadd0124b96df565adfa64e240dd2444c`。产物在 `artifacts/code-handoff/T06-20261024-update-04/`；新快捷方式 `AutumnOS-更新修复-20261024.lnk`；完整交付说明 `artifacts/update-fix-20261024/HANDOFF.md`，总报告 `update-04-verification.json`。真实payload与ID均0的未签草案在 `artifacts/update-preparation/T06-20261024-update-04/`，未上传或发布，不冒充正式绑定。旧入口/数据保留。初次bootstrap密钥测试失败及update-03脚本UTC转换导致的验签分支未触达失败报告均保留，修复后的严格断言已通过。

Windows发布者证书依然缺少，新EXE为NotSigned；本轮自有更新公钥不能解除SAC。新包GUI、解压副本GUI、真实在线升级及GUI失败恢复均not_run。首次生产根需手动使用新完整EXE，旧稳定Updater不能通过普通payload建立根。ChemLeap普通模式仍因未完成隔离边界被拒绝，未建设认可名单/特判/开发者放行。本轮没有Git、上传、全局策略改动、UAC或强杀用户程序，不代表T00–T06全部完成。

2026-10-24 派蒙请求继续做可信签名，并确认“中国大陆个人、无现成证书”。已核查项目内固定Windows SDK的SignTool为Valid；实际对startup-02交付EXE执行只读verify返回1/No signature found，整理25项产品命名PE待审查清单。当前无证书，签署0文件。官方核查：微软Artifact Signing Public Trust个人限美国/加拿大；Certum Standard及SSL.com IV有个人与实体硬件方案，大陆证件受理/寄送仍需CA确认，不能按受众选择虚假地区。申请衔接与未发送的售前询问在 `artifacts/startup-failure-20261024/SIGNING-APPLICATION.md`，实际预检 `signing-readiness-d170345d454f4581b34a5744eca4daaf/report.json`。未购买、注册、上传个人资料/项目、创建自签根或修改系统策略；受信签名、签名后打包和SAC接受仍not_run，等待本人完成合法证书申请。现有EXE与源快照未修改。

2026-10-24 启动诊断修复包已完成：`T06-20261024-startup-02` / `0.5.2-local.8`，C#869/869（新增15项错误报告、16项合成PE布局）、其余七组自动检查及83项静态检查全部通过；Framework单文件编译通过，EXE/ZIP16文件回读/解压摘要、实际EXE及解压副本资源区解析与SHA256通过。最终EXE SHA256 `43140d2c5fb4dadacb18432c61f23540ac99b5671f0cae41a8749eb3c2e7af6b`；ZIP SHA256 `e965b4b2dd293f8868c585e25b86f809c1ba8fd004cb95a17276e3444962d768`。交付路径 `artifacts/code-handoff/T06-20261024-startup-02/`，新快捷方式 `AutumnOS-启动诊断修复-20261024-未签名.lnk`，旧入口与真实数据保留。431文件源ZIP已回读核对。完整报告 `artifacts/startup-failure-20261024/STARTUP-HANDOFF.md`、`startup-02-verification.json`。

派蒙已明确没有现成代码签名证书。新包继续NotSigned，Windows策略拦截尚未解除；真实签名/SAC接受/新GUI均not_run，不改安全策略，不执行云签名或购买。没有通过代码测试冒充原第三方隔离目标完成，ChemLeap普通模式仍不可打开。

2026-10-24 派蒙补充两张截图后，启动失败根因已确认：Windows智能应用控制/CodeIntegrity策略拦截旧根入口和codecheck-01内层 `AutumnOS.Client.exe`。09:29:53–09:31:39的3033/3077事件与派蒙主动运行诊断入口的实际退出码31相符。内层Bootstrap已进入托管入口，不是CLR空目录或尚未执行.NET。旧代码把Process.Start异常显示为UPDATE_BOOTSTRAP_FAILED，并指向并未创建的更新报告。实际Win32异常数字因旧代码未记录仍未知。证据在 `artifacts/startup-failure-20261024/`；原codecheck-01启动继续记failed，不用新自动测试覆盖。

已实现原生错误分类与真实脱敏报告（便携数据根Logs/Startup，只有写成才显示路径）、PE证书表尾部兼容解析；保留payload/hash与更新恢复行为，不关闭SAC、不自动提权、不重命名规避，不改旧EXE/存档。两个My证书存储没有可用代码签名证书；5个实际入口/组件NotSigned。新构建 `T06-20261024-startup-02` / `0.5.2-local.8` 源 `sha256:b130e9646e87b9c043d76b3a970e48bef31135721c845558044ee9cbba76aade` 已Windows Release 0警告0错误，新增错误/布局回归纳入真实本地自动测试；打包与最终结果完成后追加。实际签名/SAC下启动/GUI为not_run，缺少受信签名身份，不能把这次代码修复说成已解除Windows拦截。ChemLeap及原隔离缺口保持不变。

以下为诊断取得前的历史状态；“根因未知/诊断未运行”已经被上面新证据更新，原始过程保留：

2026-10-24 后续派蒙实测反馈：上述codecheck-01“无法正常启动”，此原生启动项目改记 `failed（用户反馈，退出码尚待采集）`，不能继续把该包描述为可正常启动。代理只读核查candidate的实际展开目录：1687项程序清单逐项SHA256全部匹配，portable marker/seed存在；没有业务Logs或runtime lock。CLR为空并不能判断内层未执行：实际.NET bundle只有180个程序集及deps/runtimeconfig，无需native落盘提取项。确切启动异常仍未知，不把长路径、WinUI或隔离代码猜成根因。

已发现诊断盲区：Shell早期失败只写stderr，单文件外层原样返回子进程退出码；SDK自带module initializer的异常还可能先于Main。已编写并静态检查绑定原失败EXE摘要的诊断脚本 `artifacts/startup-failure-20261024/Invoke-StartupDiagnostic.ps1`，新入口 `AutumnOS-启动诊断-20261024.lnk`；只由派蒙主动运行后采集原字节退出码、stderr及.NET host trace，不改全局环境/安全策略、不强杀、不清理数据。当前未运行诊断入口，未声称启动故障已修复；完整隔离缺口和ChemLeap阻塞仍保留。

2026-10-24 代码核验包已生成（实际窗口交派蒙检测）：`T06-20261024-codecheck-01` / `0.5.2-local.7` / 显示号 `0.0.5-20261024-codecheck`，源码 `sha256:1403e7ce68b463e096f4dc42642e93fb85825f6e941754e012439d12610ae50b`。Windows Release 0警告0错误；C#838、SDK22、机器合同363、样例6、ChemLeap模型50、身份客户端夹具21、HTTP样例7、CLI12均通过，另83项静态检查通过。没有运行本包GUI或真实账号。

复用Prepare与Package-SingleFile生成488756135字节单文件，SHA256 `0d0a85a579834dfcefb680a2a46a905ce4bcb39051e32d1ea646534ab1ecac5f`；代码核验ZIP SHA256 `decb2286cee61ab5022456739da3c480a782c263aff2f25b220359e977f63f34`，15个交接文件回读与解压哈希一致。入口 `artifacts/code-handoff/T06-20261024-codecheck-01/candidate/AutumnOS.exe`，ZIP及extracted副本同级。新快捷方式 `AutumnOS-代码核验-20261024.lnk` 未启动，旧根入口摘要保持 `71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1`。426文件源码ZIP已导出并回读核验，未含用户数据。完整报告：`artifacts/isolation-code-handoff-20261024/HANDOFF.md`、`verification-matrix.json`。原生窗口/解压副本运行/新边界检测为not_run；剪贴板受限宿主未实现，ChemLeap普通模式仍不能打开。完整原隔离目标未完成，T01/T03/T06不升为全部通过。

两份独立诊断代码也已完成编译（0警告/0错误）、脚本语法检查和独立复核，清单25/15文件逐项一致：`artifacts/isolation-code-handoff-20261024/probes/TwoDocumentRtcProbe/`提供两文档RTC与自有UDP正控；`RestrictedDesktopLaunchProbe/`提供仅新desktop ACL及自有控制台子进程A/B、token和句柄元数据检测。后者不访问剪贴板内容、不启动浏览器、不改变既有ACL，实际执行须新增明确授权；两个工具均未执行且不接入产品，不以编译通过当隔离通过。说明见同交接目录 `PROBES.md`。中间编译失败和修正前快照保留。

2026-10-24 派蒙调整当前交付方式：先完成代码编写与核验，再由派蒙实际检测。代理本轮不再启动产品GUI、UAC或新的受限进程/私有desktop试验。原始完整任务文件仍缺失，沿用对话中的要求；不以此跳过边界。上次中断后的Build05已实际完成0警告0错误、838项C#和其余七组自动检查、目录Prepare，源 `sha256:cbdb47d53c2513d49ce23438a72b5d3c5495d85f6030084e106f6e9885d80f19`。Build05原生验收not_run。

本轮源码继续修正WebAppHost关闭顺序：显式先撤销Session，分步尝试取消/句柄/WebView/流/资源清理，任一步异常不跳过后续浏览器关闭；未完成清理保留维护锁和重试，防止重入，审计失败不打断撤销。独立复核确认各启动入口共用WebAppHost。新增 `docs/isolation-manual-verification.md` 和 `scripts/Package-CodeHandoff.ps1`，只产出明确标识的代码核验包及ZIP字节回读/解压核对，不修改正式Package-T05门禁。新的构建/自动测试/单文件打包及交接结果完成后追加。剪贴板受限宿主仍未实现，ChemLeap普通模式仍不能开放，不冒充“代码全部已实现只差用户点通过”。

2026-10-18 后续真实验证：Build04 的 `native-12` 会话/存储专项在92项通过后失败，完整网络专项明确未运行。通过项包括五种浏览器存储、受控测试身份A/B/A、本地WASM、SDK拒绝、原生拒绝权限、旧文档失效，以及同一挂起权限探针的原生允许正控；不冒充真实Logto双账号。关闭另一条真实挂起权限请求时CLI返回 `DEVELOPER_OPERATION_FAILED`，宿主记录证明已撤销但尚留浏览器资源，迟到响应验收仍not_run。已保留原始失败报告，源码补充取消异常下的订阅清理、可重试资源释放和脱敏异常阶段；新增抛出取消回调的真实RuntimeSession回归。待Build05与原生复验，不把代码修复当成产品通过。

`restricted-token-accesscheck-01/02` 仅在内存中计算权限，未把token附加到线程/进程，未创建子进程、修改ACL或访问剪贴板。01证明本机WinSta0/Default对标准RestrictedCode仍授予剪贴板权限，不能作为修复；02中合成限制SID+WRITE_RESTRICTED对13个固定二进制的读执行允许、WinSta0剪贴板拒绝，但Default创建窗口也拒绝。这不是实际浏览器边界验收。下一步只准备新私有desktop的受限子进程草案；涉及新对象ACL和真实受限进程的试验须另获授权。原受限token初始化失败与Job清空剪贴板失败均保留。

Build04 `T06-20261018-isolation-04`（0.5.2-local.5，源 `sha256:179f261a8386c2b3d27563ca3adb0e45358f2a700cbf0cda4448841d188ff701`）已完成0警告0错误构建、八组自动检查和完整目录Prepare。`native-11`用独立记录哈希的修正版驱动在17项通过后失败：全部网络通道并发探针触发原生process_failed，宿主撤销证明并释放实例；接收端仅有before正控，后控未执行，不能记网络passed。失败截图、事件和自有浏览器minidump均保留，正在分离后续独立会话测试与查明退出通道。

Build04之后源码已加入缺少实际剪贴板受限宿主时的失败关闭：所有注册外部包和任意开发预览，在包验证后、创建WebView之前返回 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`；开发模式没有例外，不读取报告或包安全声明。自带元素配对仍只走宿主固定发行资产路径。新增浏览器故障元数据仅记录SDK固定枚举和数字退出码，不记页面文本或URL。此源码增量待新构建实测；它是防止已知缺口下执行的临时安全状态，不是ChemLeap启动目标完成，也不将T01标记完成。

2026-10-18 最新增量：`T06-20261018-isolation-03`（0.5.2-local.4，源 `sha256:2580ee29f8e8672f02a2f7a51e49367d5b25133ed6ef01323bd7f440cd71e610`）Windows Release 0警告0错误，八组自动检查通过、目录Prepare完成，仍是未交付的隔离诊断中间产物。`native-09`因目标窗口最小化而触发输入护栏中止；未放宽护栏。`native-10`在14项检查后，未注册外部协议使引擎提交错误页：新 `document_binding_lost` 保护实际撤销证明并关闭实例，但原脚本期待保留页面，因此该次报告仍failed。后续源码已拒绝Crashed实例的旧调试会话、将权限UI绑定到发起请求的真实instance ID，并补关闭挂起权限请求的宿主响应丢弃记录；这些后续修改待新构建/真实验证。

派蒙授权的受限token控制台机制试验已运行一次：`restricted-token-launch-01/report.json` 中A普通子进程正常执行；B `CreateProcessAsUser`成功创建子进程，但其初始化以 `0xC0000142` 失败，没有探针JSON。这是初始化失败，不是已证实的权限错误，也不定位具体DLL；浏览器/剪贴板能力均not_run。未提权、未改ACL/窗口站/全局策略、未启动浏览器。Job剪贴板copy/cut清空反例仍未修复，不能将当前候选普通启动路径视为已通过安全验收；未分发或替换根入口。真实挂起权限请求取消、迟到成功资料响应、撤销及账号切换竞态分别记账，不互相代替。

2026-10-18 后续实测：`T06-20261018-isolation-02`（0.5.2-local.3）构建0警告0错误，837项C#及其余自动检查通过，完整目录已Prepare；源 `sha256:963ef5dc0be3b74bd26246fee2fd1bcdd8f5503c86b1474c3ca6bcb8b092bd2c`。共享mDNS辅助参数消除了本轮新副本中观察到的防火墙窗口干扰，但不是安全边界。`probe-matrix-03/summary.json`用新工具字节重跑runtime-ab、25项资源/存储、网络接收端A/B、同源WS/WT反例，四组通过；不代表产品/剪贴板/最终EXE通过。

`native-08`实际执行至13项通过后失败：外部导航已被宿主Cancel，但固定引擎仍提交ERR_NETWORK_ACCESS_REVOKED错误页，原宿主状态未撤销。已保留截图、接收端及事件记录；源码正在补原生SourceChanged/ContentLoading文档绑定失效处理，尚待重建复验，不将当前源码冒充Build02原字节。

派蒙第二次单次UAC授权已用于 `clipboard-station-uac-02`。六项实际执行：无Job的copy/cut/paste正控均通过；Job UI=6下paste拒绝通过，但copy/cut仍清空合成剪贴板（原生序号22→23、40→41），两项失败。六个浏览器均自然退出，helper正常释放；没有访问WinSta0剪贴板。此Job实现不符合完整边界，**没有集成或据此放行**。不删除失败断言、不恢复真实用户剪贴板、不继续使用该次UAC授权。正在核查比Job限制更可靠的修复路线；涉及新增进程限制须先说明影响并获授权。ChemLeap普通模式、最终EXE/ZIP仍not_run。详见 `artifacts/isolation-20261018/DELIVERY-DRAFT.md`。

2026-10-18：继续本地第三方隔离交付，制作人派蒙。根 `AutumnOS_Isolation_Implementation_Codex_Prompt.md` 确实缺失，以本轮用户完整要求执行。当前根入口仍为旧版 `0.0.3-20261017-local`，488734744 字节，SHA256 `71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1`；当前源码已无旧 T01 一律拦截，但旧候选未完成 GUI 验收并未替换根入口。用户已确认旧客户端退出并授权本轮真实桌面测试。根入口和真实数据保留，不执行 Git/远端写入/上传/发布/全局安全策略修改。

本轮新增 `autumn-local-web-v2`：精确检查实际固定 Runtime 154.0.4258.53、实际 profile/浏览器路径/InPrivate、只读拒绝适用 WebView2 环境及注册表覆盖；保留加载前挑战。允许包内脚本携带 WASM 字节而不允许 JS eval；`connect-src 'none'` 保留，未开放同源 fetch，因为实测局部 connect-self 对照会放开同源 WebSocket/WebTransport 网络服务请求。当前无第三方受控网络 SDK，授权网络明确 unsupported/not_run，需求不删除。修复 legacy SDK 在关闭或撤销后交付迟到成功响应的缺口。

2026-10-18 本轮中间验收：跟踪构建 `T06-20261018-isolation-01` Windows Release 0警告0错误；837项C#及7组其他自动检查通过。固定引擎独立资源/存储25项和网络A/B、同源WS/WT反例已保留，见 `artifacts/isolation-20261018/STATUS.md` 和 `PROBE_HANDOFF.md`。后续源码/文档仍在修改，尚未冻结最终EXE/ZIP，ChemLeap普通模式游玩/存档尚为not_run。

主产品UI测试遇到Windows防火墙提示，已在 `native-diag-07/security-picker.picker-27212.png` 抓到真实窗口：询问是否允许网络访问Microsoft Edge WebView2。它出现在宿主RTC挑战期间、早于第三方页面；输入护栏没有操作安全窗口。派蒙随后明确确认曾点击这些提示的“允许”；只读ActiveStore发现本轮测试路径及.tools对应Public入站Allow规则。代理没有添加、删除或修改这些规则，但不能把环境写成完全未变。网络结论只按实际策略和接收端观测陈述，不借助系统提示或用户允许作为隔离证据。正在定位如何避免该入站请求。

剪贴板补充：Permissions Policy只覆盖异步API，旧式浏览器编辑通道仍需真实验证。派蒙已授权仅新建浏览器的Job READCLIPBOARD|WRITECLIPBOARD限制试验；`browser-job-03/report.json`实际赋值/回读UI=6、成员关系、新renderer/WASM及正常退出均通过，尚未集成。普通用户创建私有windowstation两次失败(5/183)，均未触剪贴板。派蒙另授权一次UAC仅创建新非交互station；`clipboard-station-uac-01/report.json.lease.json`显示创建/认证/释放成功，但普通probe在SetThreadDesktop返回170，剪贴板A/B未执行。不会重复消耗该次UAC授权；正在修线程初始化并先进行无剪贴板预试。所有失败证据保留。

初步直接 Windows Release 编译 0警告0错误，836/836 C# 通过（`artifacts/isolation-20261018/work-tests.json`，未冻结辅助构建，不当作最终交付成绩）。新真实固定引擎探针已有网络逐通道接收正控、资源/WASM及五种存储多身份隔离证据；最终工具字节及主产品/EXE/ZIP验证正在继续。ChemLeap真实已装0.1.1，github:1414175562，8资源逐项摘要一致，仅saves权限，无运行远程依赖；未读真实存档。测试脚本更新至此版本/实际发布包摘要，准备普通模式游玩与真实SDK存读档。所有未跑项仍 not_run，阶段总状态不提升。以下2026-10-17及以前条目为历史记录。

2026-10-17 本轮本机单文件打包与验证已完成，入口 `<WORKSPACE>\AutumnOS.exe` 已原子替换为 `T06-20261017-refresh-06`（0.5.1-local.7 / 显示号0.0.3-20261017-local，488730505字节）。源码 `sha256:ee6caca9f3a122d17f7930f923fecff10aa1f3bfd9a91ddd38662485dbc6d660`，最终EXE SHA256 `73c4b22ca18a9a09ba8211b32f4a684eddd34c712296134091978f0ada1495a8`。Windows Release 0警告0错误，7套自动测试全部通过（702/22/358/6/21/7/12），最终EXE原生47/47，较短全新目录独立首次启动31.55秒通过。隔离测试公钥的目录更新80/80、手动选版/回滚/固定53/53通过；这些是本地演练，不代表生产签名或单文件入口升级已实测。原入口备份、失败尝试与用户数据保留；按用户最后要求没有清理或归档旧文件，没有上传GitHub。真实Logto discovery仍HTTP502，真实登录not_run；现有Framework准备器不支持超长提取路径（262字符失败保留），较短路径复验通过；生产签名更新、完整Windows环境矩阵及安装器等未完成项仍按原规则保留。交付报告：`artifacts/local-refresh-20261017/delivery-final06/HANDOFF.md`。本轮本地交付完成不等于完整T06验收完成，T06仍in_progress。
2026-10-02 派蒙已授权进入 **T06 全量联调、开发者交付与本地候选验收**，替代下面旧T05阶段的“未进入T06”限制。当前开发基线为 **T05-20261002-portable-05 / 0.5.0-t05-portable**，实际272文件源快照、原EXE和ZIP哈希已重新读取匹配，见 `artifacts/reports/t06-foundation/baseline.json`。根目录T06提示文件缺失；用户随后提供的 `G:/下载/AutumnOS_T06_Codex_Prompt.md` 已完整读取，不能将第一次缺文件时的检查冒充已读。完整711行进度历史、原T06、58需求/66验收、G01–G14和接口/文档规范已核对。**安装包继续按用户最新明确指示暂缓，只做便携EXE/ZIP与开发者交付；T03原收尾继续暂缓**。当前T06-A实施中，历史T04/T05成绩仅作基线、不填成本轮成绩。无Git、上传、云端CI或公开发布授权；完整发布就绪仍为false。后续结果在本记录末尾追加。

以下T05摘要为上一轮交付时点，保留其历史结论：

2026-10-02 T05 授权覆盖下文旧阶段限制，真实起点为 **T04-20261002-desktop-02** / `0.4.1-desktop-multitask` / `sha256:36f5ff28f2c98cc017d51804164a625c818e357d1117f09080b6ab9b562d53d8`。按派蒙最新要求，**安装版暂缓，仅交付便携 EXE/ZIP**。当前交付 **T05-20261002-portable-05 / 0.5.0-t05-portable**，源 **sha256:e8455e0aa53be5864c361a62795996e544c579faf15435d3fadfba31b56a1a1b**；553 项 C#、相关 SDK/CLI 回归、最终交付商店75/桌面120/原地启动18项通过。包含完整协议的真实 A/B/Bad 发行构建在隔离目录完成72项升级/回退和144项故障专项；详见文末最终记录与 acceptance-coverage.json，不能扩展为所有断言通过。T05 保持 in_progress：生产公钥和正式签名更新源 blocked，普通版明确显示自动安装尚未配置；完整物理故障/系统矩阵仍 not_run。T03 收尾继续暂缓，T01/T02/T04历史未完成项保留，T06未开始，无发布授权。根目录缺少 `AutumnOS_T05_Codex_Prompt.md`，实际读取用户明确提供的 `G:/下载/AutumnOS_T05_Codex_Prompt.md`，输入摘要见 `artifacts/reports/t05-foundation/inputs.json`。下文历史失败、旧Setup和所有旧包/数据/快捷方式原样保留，不回写为成功。

以下为先前阶段摘要与完整历史，保留原时点结论：

T00 `verified`（阶段完成条件）；T01/T02/T03 `in_progress`。2026-10-02 派蒙明确暂缓 T03 收尾与完整验收，授权直接进入 T04；当前工作重点 T04 `in_progress`，T05/T06 `planned`。本摘要原先停留在 T03 planned，与后文和源码不一致，现纠正；T03 已有账号/保持登录/回调页成果保留，其余待办不删除或补写通过。T01 缺项没有取消或冒充通过，完整 Windows 双系统、双发行包和产品验收仍未完成。原始 ZIP 和两层解压开工包原样保留；未创建 `.git`，未执行任何 Git、远端源码同步或上传。

## 2026-10-01 实际操作

- 读取根规则、完整开工指令、需求/接口/阶段/验收/Logto 文件。建立 `AutumnOS.slnx`、Contracts / Storage / Identity / WinUI Shell / Tests 工程。
- 环境：Windows 11 专业版 x64，10.0.26200；PowerShell 7.6.5；当前进程非提权；系统只有 .NET 8.0.31 运行时、没有 SDK/MSBuild/系统 Windows SDK。WebView2 Runtime 154.0.4258.48 已安装。
- 用户已明确批准将 .NET 10 SDK 安装在项目 `.tools/`。执行 `./scripts/Install-LocalSdk.ps1`，官方 ZIP 的 SHA-512 匹配，`dotnet --info` 成功：SDK 10.0.401，MSBuild 18.9.11，Runtime 10.0.12。没有修改系统 PATH。
- 创建固定版本依赖：WindowsAppSDK 1.8.260921001、Windows SDK BuildTools 10.0.26100.9169、WebView2 SDK 1.0.4258.31。
- 执行 `./scripts/Restore.ps1`：纯 C# 项目恢复成功；WinUI NuGet 下载出现 TLS EOF/超时，正在保留证书验证的前提下排查。首次挂起下载已停止，尝试禁用 HTTP/2 后重新恢复，结果待记录。
- 实现 `AutumnOS_Data`、首次启动状态、结构化日志、真实公开 Logto Options/校验/只读发现探测；测试正在编写。原生窗口调用这些真实服务，不提供假登录或假商店。

## 证据与限制

恢复核实的历史证据：`artifacts/builds/T00-20261001-foundation-03/` 有 Windows Release 构建 passed、81/81 自动测试 passed、`windows-smoke/windows-smoke.json` 22 项 passed（普通用户、原生窗口、首次初始化、中文空格路径、不同工作目录、二次启动保留存档、最大化/恢复、关于）。源 ID `sha256:6b1b000d184163234bb1f14c816d9ef3a6ab7b5003870649a88c571d6cc06485`。已查看原生窗口截图，制作人：派蒙可见。现有开发 ZIP 位于 `artifacts/packages/T00-20261001-foundation-03/AutumnOS-0.1.0-t00-win-x64-development.zip`，不是正式发行候选。

本次重新执行 `./scripts/Get-Environment.ps1`：Windows 11 专业版 10.0.26200、非提权、PowerShell 7.6.5、项目内 SDK 10.0.401 / MSBuild 18.9.11、WebView2 154.0.4258.48。没有系统 Windows SDK，但固定 NuGet BuildTools 已可完成本机构建；当前不需要新安装授权。

T01 半成品原有 Packages、Runtime、manifest 与 d.ts，缺少样例页面、SDK JS 与 WebView2 接线，不能称为可运行。现继续补齐这些模块并准备重建/实测。身份修复辅助验证 83/83 passed（未跟踪快照的直接测试，不替代最终 Build/Test）。

Logto Native/public 类型、两项回调登记仍为 `unverified`；真实登录为 `not_run`，不索要 Client Secret。全部 57 项需求和 65 项验收保留；内部 .autumn、iPad 风格桌面、GitHub 商店发现及 pm/sq、版本筛选、代理、plus/meta 安全更新、完整 SDK/文档仍在范围内，没有认可名单。

## 下一步

继续 T01 的真实浏览器网络通道负面测试（含 WebRTC/STUN、WebSocket、worker、框架、导航、下载与外部协议）、崩溃故障注入、物理键鼠/触控、中文 IME、DPI/缩放矩阵与真正挂起/恢复。网络隔离未证实前，仅开放自带样例；完整第三方需求保留。上述阶段验收通过后，再按依赖进入 T02–T04。最终双包、正式签名、干净系统/Windows 10、真实账号、安装与更新恢复仍未验收。

## 本次完成的 T01 本地增量（2026-10-01）

制作人：派蒙。T00 阶段保持 verified，T01 保持 in_progress，未声明整个产品完成。

- 保留并修复现有 Runtime/Packages 半成品，接通真实 `.autumn` 安装、WinUI 内部 WebView2、元素配对游戏、JS SDK、类型、清单/消息 schema 与 `docs/sdk-t01.md`。
- 安装先验证路径、链接、ZIP CRC、大小/数量、原生文件、清单与资源，再暂存提交；来源/实例绑定、消息限制、速率、超时、授权撤销与挂起/恢复竞态有负面测试。游客存档真实原子写入，坏记录和旧数据保留。
- 用户点击保存/载入后才申请存档权限；后台仍是存活游戏并阻止维护，结束时关闭实际 WebView，新实例可重新授权并读回同一存档。该状态判断还不是 T05 跨进程维护锁。
- Native 窗口和许可/署名保留。Logto 使用原公开配置，修复默认发现对可选 offline_access 的过度要求。只读公开发现已通过；Native 类型/回调登记仍 unverified，真实登录 not_run。
- 没有新安装组件，没有修改系统 PATH，没有 Git/远端写入或公共发布。原始 ZIP/解压开工包保留。

### 最终构建与实际命令

构建 ID：`T01-20261001-internal-01`。源文件 ID：`sha256:74d4ece59f3a5e20b453f3b4987096e2a63dc7100aeb69ae2f95c4a800ee9ade`。commit：not_applicable。所有最终报告在 `artifacts/builds/T01-20261001-internal-01/`。

| 实际命令 | 结果 / 证据 |
|---|---|
| `./scripts/Get-Environment.ps1` | Windows 11 10.0.26200，普通用户；项目内 SDK 10.0.401 / MSBuild 18.9.11；WebView2 154.0.4258.48。`artifacts/reports/environment.json` |
| `./scripts/Test-Source.ps1` | 29 项静态检查 passed；57 项需求与 65 项验收保留。静态检查不替代产品验收 |
| `./scripts/Build.ps1 -BuildId T01-20261001-internal-01` | locked restore 与 Windows Release passed，0 警告/0 错误；构建前后源快照一致，程序集 ID 与产物哈希核对通过 |
| `./scripts/Test.ps1 -ProbeLogto` | C# 132/132 passed，含 Windows ACL/junction；Node v24.19.0 SDK 12/12 passed。公开 OIDC discovery passed，真实登录仍 not_run |
| `./scripts/Test-WindowsSmoke.ps1 -ExerciseRuntime -ReportDirectory artifacts/builds/T01-20261001-internal-01/windows-smoke` | 31 项 passed：普通用户原生启动、初始化/数据保留、中文空格目录、翻牌、中文昵称、许可覆盖层、真实存档、缩放、后台/恢复、结束/新实例读档。8 张截图；已检查恢复页和许可覆盖层，派蒙最终设计审阅 not_run |
| `./scripts/Package.ps1` | 本地开发目录/ZIP 生成；publish 字节与实际构建一致，无用户数据，无上传 |
| 本地 `ZipArchive` 逐项 SHA-256 校验 | ZIP 内 551 个文件与开发目录一致；`package-verification.json`。不是干净机器启动测试 |
| Codex 已有 Python 执行 `autumnos-spec/tools/check_plan.py` / `check_public_config.py` | 两项原始静态校验 passed，报告在本构建目录；不代替产品测试。系统 `python` 入口不可用，实际使用 `<USERPROFILE>/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe`，没有新安装或修改 PATH |

最终状态写入后再次执行静态源检查并生成 `after-delivery/source-snapshot.json`：源 ID 仍与最终构建完全一致，开发 ZIP SHA-256 也与记录一致。规范 requirement/acceptance/release 状态已补入本增量证据；完整断言未完成的测试保持 not_run，T01 未升级为 verified。

### 本地产物

- 可双击入口：`artifacts/packages/T01-20261001-internal-01/AutumnOS-0.1.0-t01-win-x64-development/AutumnOS.exe`。必须保留完整同级文件，界面点击“打开元素配对”。
- 开发 ZIP：`artifacts/packages/T01-20261001-internal-01/AutumnOS-0.1.0-t01-win-x64-development.zip`，90,600,217 字节；SHA-256 `6a7df86d521612dcb144121ce2d693a18feade2804015dd72dfdf938d85240e0`。
- 真实样例包：`artifacts/samples/element-pairs.autumn`，也随 EXE 放入 `Samples/`。确定性打包来自实际样例/SDK 字节，不下载源码 ZIP 当安装包。
- 可复现源码、锁文件、本地脚本和文档保留在 src/tests/scripts/sdk/samples/docs；最终源清单、产物哈希、测试/窗口/包报告均与上述构建 ID 关联。

### 如实保留的失败和待验证项

早期辅助构建曾因样例包尚未生成而报 MSB3030。原生调试报告 `artifacts/reports/t01-native-integration-01` 因 WinUI 的 Chromium 文档位于独立无障碍节点而找不到输入框；已按精确标题、进程归属与窗口范围修复查询。`-02` 误激活同名工具栏按钮；确认按钮改为“确认结束”。`-03` 不支持卡片的 InvokePattern；已使用实际 TogglePattern。`-04` 29 项 passed（辅助未跟踪构建），最终快照构建重跑 31 项 passed。所有失败报告原样保留，不回填成功。

T01 完整网络隔离、原生 IME/触控/DPI、崩溃注入、真正浏览器挂起和恢复仍 not_run；TypeScript 编译和完整 JSON Schema 引擎验收 not_run。当前仅自带样例入口，没有一般第三方包运行的安全完成声明，没有认可名单。

Windows 10 22H2、无开发环境/无预装 .NET 的干净系统、离线 WebView2 依赖交付、安装 EXE、正式签名、全部产品桌面/账号/商店/更新及恢复门禁仍 not_run 或 planned；当前包是开发增量，release_candidate=false。

## 交付包闪退修复（2026-10-01，用户反馈“无法打开”）

已复现原包真实启动失败：`Start-Process -FilePath .../T01-20261001-internal-01/AutumnOS.exe -WorkingDirectory <EXE目录> -WindowStyle Normal -PassThru`，8 秒后进程 6492 已退出，退出码 -1073741189（0xc000027b）。Windows Application Error 1000 指向 Microsoft.UI.Xaml.dll。比较实际构建与交付目录确认原包缺少 `App.xbf`、`MainWindow.xbf`、`AutumnOS.pri`，因此窗口尚未建立就崩溃。此前原生测试验证构建输出，ZIP 哈希只验证 ZIP 与错误的打包目录一致，不能证明交付包能启动。**上节原包“可双击入口”的描述撤回：该旧包不可用，保留作失败证据。** 无需安装新组件。

修复 `scripts/Package.ps1`：从已校验哈希的实际构建中复制所有 WinUI .pri/.xbf 资源，要求三项应用资源齐全，全部交付文件与构建字节一致；打 ZIP 前必须对交付目录的隔离副本执行普通用户原生窗口及内部游戏测试。失败停止打包并保存报告。README 同步说明新门禁。没有改换技术路线、修改公开配置、上传或删除已有包。

新构建 ID：`T01-20261001-packagefix-01`；源 ID：`sha256:c1274fbe3ec48f83195c5bb318500db2f6429b506c72b4091b9336cc0c7c717e`；commit：not_applicable。真实命令与结果：

| 实际命令 / 动作 | 结果 |
|---|---|
| `./scripts/Build.ps1 -BuildId T01-20261001-packagefix-01` | locked restore、Windows Release passed；0 警告/0 错误，构建前后源 ID 一致 |
| `./scripts/Test.ps1` | C# 132/132、SDK 12/12 passed；本轮未重跑 Logto 探测，真实登录仍 not_run |
| `./scripts/Package.ps1` | 交付资源完整、字节匹配；自动执行 `Test-WindowsSmoke.ps1 -ExecutablePath <新交付EXE> -ExerciseRuntime -ReportDirectory artifacts/builds/T01-20261001-packagefix-01/package-smoke`，29 项 passed；8 张截图，已查看真实读档恢复页 |
| 本地 ZipArchive 每项 SHA-256 与目录比较 | ZIP 内 554 文件全部匹配，无 AutumnOS_Data，三项 WinUI 应用资源存在；`package-verification.json` passed |
| `./scripts/Test-Source.ps1` | 29 项 passed |
| `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T01-20261001-packagefix-01/after-delivery` | 实际源 ID 与构建相同 |
| 直接 `Start-Process` 打开新交付 EXE | 普通用户启动 passed，进程 2316、真实窗口标题 Lab Chronicles AutumnOS、窗口句柄 328954，8 秒仍存活；数据目录建立，保留窗口供用户使用。`delivered-launch.json` |

全部修复证据在 `artifacts/builds/T01-20261001-packagefix-01/`；旧包复现报告 `original-package-launch-failure.json`。新入口：`artifacts/packages/T01-20261001-packagefix-01/AutumnOS-0.1.0-t01-win-x64-development/AutumnOS.exe`。根目录新增 **`AutumnOS（本地开发版）.lnk`**，目标为这个真实 EXE，不移动用户数据。快捷方式创建前检查不存在，未覆盖既有文件。

新 ZIP：`artifacts/packages/T01-20261001-packagefix-01/AutumnOS-0.1.0-t01-win-x64-development.zip`，90,973,419 字节，SHA-256 `07f25fdb337ecdb8e366c18620391ecb44782d510d008a7611df550737c7bdb4`。直接启动后产生的数据仅保留在目录，已生成 ZIP 不含用户数据。新包为本地开发增量，release_candidate=false。

下一步继续 T01 网络隔离、IME/触控/DPI、崩溃与真正挂起测试；这些仍 not_run。Windows 10、干净机器、离线依赖、安装版、真实登录和完整 T02–T06 门禁未验收，所有原功能保留。没有 Git 操作或远端发布。

## T00/T01 核对、SDK 长会话与 T02 桌面中间检查点（2026-10-01）

制作人：派蒙。本轮用户明确要求核对 T00/T01 的真实记录、修复 4096 请求上限后推进 T02；因此 T02 进入 in_progress，T01 仍 in_progress。没有把窗口可运行等同全验收，完整 T02 未声明完成。保留现有 Runtime、Packages、Storage、Identity 公开配置、旧样例/数据、测试和原始开工包。

### 先核对的历史证据

完整清单和证据强度见 `docs/t00-t01-evidence-audit.md`，已实际读取报告并核对来源清单摘要、ZIP 和核心产物哈希：

| 历史构建 | 实际执行结果 | 证据边界 |
|---|---|---|
| T00-20261001-foundation-03 | Release passed；81 自动测试；22 原生窗口检查 | 原生构建输出；完整双系统/双包/产品验收未完成 |
| T01-20261001-internal-01 | Release passed；132 C#；12 SDK；31 构建目录窗口检查 | 旧交付包实测闪退，缺 XBF/PRI；551 文件 ZIP 对照没有证明启动 |
| T01-20261001-packagefix-01 | Release passed；132 C#；12 SDK；29 交付目录窗口检查；554 ZIP 文件；实际交付 EXE 8 秒存活 | 修复包本机可运行；不是 Windows10/干净系统、安装版或全 T01 通过 |

原生 IME、物理触控、DPI 矩阵、浏览器所有网络通道隔离、崩溃注入、真正浏览器挂起/恢复、真实 Logto 登录、Windows 10/干净机器/安装版仍 not_run。已有公开发现成功也不证明 Native 类型/回调登记；登记状态保持 unverified，不索要秘密或已有公开值。

### 本轮真实变更

SDK 的 MAX_REQUESTS=4096 和宿主永久 seenIds/4096 封顶都已确认并修复。SDK 以随机文档前缀和 BigInt 单调序号生成唯一 ID，不存全部历史 ID；保留 32 KiB 消息、16 个未完成请求、滚动 60 秒 120 次。宿主在排队前预留待决 ID，不淘汰仍在执行的 ID；完成后保留 10 分钟，总记忆容量 2048，满时 REQUEST_BUSY，保留未过期去重项。单调时间防止时钟调整绕过限制。窗口外手动重放可能再执行，不承诺永久 exactly-once；类型、Schema、合同同步。

宿主和 SDK 分别通过同会话 12000 次调用测试；用可控单调时钟推进窗口，没有等待两小时或伪称两小时真实运行。宿主还验证记忆集合约 1000 项且不超 2048、最近重复拒绝、窗口过期继续、待决重复不随时间失效；长会话后仍能授权、保存和读取。保留并发、频率、超时、取消与授权撤销测试。

T02 首启现为全窗口 hello → 品牌 → 初始化引导 → 壁纸桌面；原诊断页面迁至设置 → 开发者诊断。桌面状态区显示本机时间，图标网格从实际安装验证结果生成元素配对，Dock 包含真实可用入口。元素配对返回桌面调用 Background，再次点击同一实例 Foreground；关闭菜单会确认并实际结束会话。设置是系统应用，真实原子保存 light/dark/system 与三套原创矢量壁纸。左键长按 600 ms、移动 8 DIP 取消计时、右键菜单/原生键盘语义保留；本轮实测是作用于产品窗口的合成鼠标输入，物理触控不冒充通过。动画遵循 Windows 设置。

新样例版本为 0.2.0（SDK 字节改变，不静默覆盖同版本安装），appId 与存档命名空间保留。旧版本文件未删除。旧安装版样例的跨版本迁移专项仍 not_run；现有保留/冲突处理单测和新样例保存/重启已通过。

静态审查发现并修复首启保存失败反馈不在当前页、长按取消后残留会吞键盘动作、极窄窗口裁切风险。首选最小窗口为 900×680，跨 DPI/窄屏矩阵仍待验收。系统页面没有假商店、账号或下载成功。

### 最终构建、真实命令与结果

构建 ID：`T02-20261001-desktop-01`。实际源 ID：`sha256:eca771ab5e9345ec90bcebd2454fc53f0c9fc149aa75e14200bd8b7d9f0efcea`。commit：not_applicable。环境 Windows 11 10.0.26200 x64、非提权，固定项目 SDK 10.0.401、WinUI/WinAppSDK 与 WebView2 依赖未换源升级。

| 实际命令 / 操作 | 结果和位置 |
|---|---|
| `./scripts/Build.ps1 -BuildId T02-20261001-desktop-01` | locked restore、Windows Release passed，0 警告/0 错误；前后实际源快照一致，程序集元数据与实际产物哈希保存 |
| `./scripts/Test.ps1` | C# **153/153**；Node SDK **15/15** passed；含两侧 12000 次长会话、18 项主题/壁纸 Storage 测试。`tests.json` / `sdk-tests.json` / test-runs 日志 |
| `./scripts/Package.ps1` | publish 实际字节核对、XBF/PRI 完整后自动运行下面的交付包原生检查；通过才生成 ZIP |
| `./scripts/Test-DesktopSmoke.ps1 -ExecutablePath <交付目录>/AutumnOS.exe -ReportDirectory artifacts/builds/T02-20261001-desktop-01/package-smoke`（Package 内实际调用） | **47/47 passed**，**17 张真实截图**。包含首启、安装入口、图标/Dock、所有主题/壁纸落盘、长按/右键、开发诊断、内部游戏许可/翻牌/存档、返回桌面同实例、结束/新实例读档、重启记录与存档/外观不变、不同工作目录 |
| ZipArchive 全文件 SHA-256 对照 | **554** 个文件匹配，无用户数据，入口及 WinUI 资源齐全。`package-verification.json` |
| `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T02-20261001-desktop-01/source-check.json` | **30** 项静态检查 passed；57 需求与 65 验收定义保留。静态不代替产品测试 |
| `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T02-20261001-desktop-01/after-delivery` | 实际源 ID 与最终构建相同 |
| `Start-Process -FilePath <实际交付EXE> -WorkingDirectory <EXE目录> -WindowStyle Normal -PassThru` | 实际目录直接启动，进程 4944，窗口句柄 12322152，标题 Lab Chronicles AutumnOS；8 秒仍存活，数据目录建立，窗口保留供用户操作。`delivered-launch.json` |

全部最终报告位于 `artifacts/builds/T02-20261001-desktop-01/`。只读审计、辅助编译/测试与 UI 调试分别保留在 `artifacts/reports/`，不混作最终源快照结果。首次 `t02-desktop-integration-01` 在前一弹窗关闭动画期间点击关于导致测试等待超时；工具已增加可见性与关闭等待，失败原样保留。`-02` 47 检查通过但 local-untracked，只作调试；最终交付检查已重跑通过。Storage 隔离工具最早 4 个辅助测试问题与修正报告也保留，不覆盖失败事实。

### 本地交付与派蒙审阅

- EXE：`artifacts/packages/T02-20261001-desktop-01/AutumnOS-0.2.0-t02-checkpoint-win-x64-development/AutumnOS.exe`。完整目录一起保留。
- 新根目录快捷方式：`AutumnOS（T02 桌面检查点）.lnk`。创建前检查不存在；旧快捷方式、旧包和其用户数据保留。
- ZIP：`artifacts/packages/T02-20261001-desktop-01/AutumnOS-0.2.0-t02-checkpoint-win-x64-development.zip`，90,997,444 字节，SHA-256 `8ef872664b2cd0c292ffb8941113278f458c48c92aed4873fad3bc84305150f0`。release_candidate=false，未上传。
- 浅色：`package-smoke/04-light-desktop.png`；深色：`06-dark-desktop.png`；长按：`07-long-press-menu.png`；游戏返回桌面：`14-background-desktop.png`（都在本构建报告目录）。另有 hello、品牌、初始化、设置、内部游戏/许可/读档截图。
- 截图直接捕获真实产品客户区，避免窗口阴影透出其它程序；没有合成界面。已查看首启三页、浅/深桌面、设置、长按和后台卡片，`visual-review.json`。派蒙最终设计审阅保持 not_run。

### 下一步与仍未执行项

T01 全网络隔离、物理输入/IME/触控、DPI、崩溃注入、真正挂起与恢复继续未完成。T02 多页/文件夹、编辑拖放、搜索/应用库、多任务切换卡片、通知/控制中心、动态壁纸与其余个性化、独立减少动态开关、首启中途退出/异常恢复 UI 矩阵仍未验收（not_run），本轮只是用户要求的桌面检查点。

T03 真实身份、T04 GitHub 发现/pm/sq/版本选择/代理、T05 plus/meta 安全更新、完整 SDK/开发文档、T06 双系统干净机器与双包仍保留。没有认可名单、远端写入、Git 初始化/提交或云端 CI。继续按本地需求完善上述阶段，不用本轮截图或 47 检查替代全部验收。

最终状态写入后再次执行 `Test-Source.ps1 -ReportPath artifacts/builds/T02-20261001-desktop-01/after-delivery/source-check.json`：30 项 passed；`New-SourceSnapshot.ps1` 再核对实际源 ID 与交付完全相同。用已有 `<USERPROFILE>/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe` 分别执行 `autumnos-spec/tools/check_plan.py` 和 `check_public_config.py`：57 需求/65 验收/7 阶段结构及公开配置静态校验 passed，报告位于本构建目录，未访问 Logto、未执行登录、没有新安装。最终查询交付进程 4944 仍有真实窗口且 Responding=true。

## T02 双栏设置与后台游戏操作增量（2026-10-01）

制作人：派蒙。本轮仅按用户要求修改两个桌面交互，不进入 T03。旧包、旧脚本、旧报告、用户数据全部保留。与上一构建 source-snapshot 对照，Runtime、Packages、Storage、Identity、Contracts、SDK、样例、原有测试和公开配置共 45 个相关文件字节未变。本轮版本为 0.2.1-t02-interactions。

设置改为持久左栏导航与右栏详情，默认外观；五类分组含图标、真实选中态。外观继续调用原 DesktopPreferencesStore 保存 light/dark/system 和 warm/mist/night。桌面/Dock 读取实际安装与运行状态，通知/控制中心如实显示未接入，没有假开关。开发诊断移入高级组。后台游戏只显示图标状态，双击原生 Flyout 的继续/结束绑定真实宿主引用及实例 ID；长按/右键菜单动作相同。旧 RunningCard 已移除。确认结束后真实关闭 WebView/Session，保留存档。

辅助验证已实际执行两次 `Invoke-Dotnet -Arguments @('build','AutumnOS.slnx','-c','Release','-p:Platform=x64','--no-restore')`，均零警告/零错误，日志 `artifacts/reports/t02-interactions-first-compile.log`、`t02-interactions-second-compile.log`，只作 local-untracked 调试证据。静态审查修正诊断页 Grid 行重叠及 Button 已处理键盘事件订阅。`./scripts/Test-Source.ps1 -ReportPath artifacts/reports/t02-interactions-source-check.json`：31 项静态检查 passed，不代替产品测试。

新增 `Test-DesktopInteractionSmoke.ps1`，保留旧 `Test-DesktopSmoke.ps1`。辅助 integration-01 因工具没有读取 AssemblyProductAttribute 导致关于页检查失败；integration-02 真实输入 hit-test 发现窗口被其他应用遮挡，拒绝继续（该次失败屏幕截图不作为产品视觉证据）；integration-03 在分类指针点击后等待超时。失败目录和原图均保留在 `artifacts/reports/t02-interactions-integration-01` 至 `-03`。测试工具正在完善前台/命中归属与指针时序，未通过的交互不先行宣称通过。最终冻结构建、测试与交付结果将在下文记录。

### 调试完成与证据条件

第三次同参数辅助编译亦零警告/零错误，`t02-interactions-third-compile.log`。integration-04、-05 均在 38 项设置/外观检查通过后因外部程序抢前台而安全停止。定点诊断确认 UIA/Win32 坐标一致、DPI 96；系统鼠标悬停激活只读查询为关闭，未修改设置。只对脚本自己启动的测试窗口临时 TOPMOST、点击经 HTCAPTION/实际光标/进程归属核验的标题栏正常激活，缩短激活与输入间隙，动作间保留光标于产品内、结束后恢复初始位置。不关闭已有程序、不改系统前台策略。pointer-diagnostic-05 的长按→单击→后台双击→继续→右键结束链通过。

补严了每个鼠标 down（含第二击）的实际光标/最新控件边界/进程/前台检查；后台单击在输入前采日志基线，继续必须出现输入后的同实例新 Foreground 事件，不能用旧日志充数。截图屏幕回退前后检查其它窗口遮挡；报告拒绝覆盖已有目录，清理只持有脚本自启进程。上述辅助目录及其失败未覆盖或删除。integration-06 最终 **80/80 passed、21 张截图、退出 0**，仍明确是 local-untracked 辅助证据；随后冻结脚本并重跑正式构建/交付检查。

### 最终冻结构建与真实命令

构建 ID：`T02-20261001-interactions-01`。实际源 ID：`sha256:4476c5657ed189206ea0df22ce41ca83fbc5921626e40f7ff835624ff172e8d3`。commit：not_applicable。Windows 11 10.0.26200、普通用户、项目内 .NET SDK 10.0.401，现有依赖及锁文件保留。本次所有正式证据在 `artifacts/builds/T02-20261001-interactions-01/`。

| 实际命令 / 操作 | 实际结果 |
|---|---|
| `./scripts/Build.ps1 -BuildId T02-20261001-interactions-01` | locked restore、Release **passed，0 警告/0 错误**；构建前后源快照一致、程序集构建身份及产物哈希核对通过 |
| `./scripts/Test.ps1` | **C# 153/153、Node SDK 15/15 passed**；原有 12000 次长会话和 18 项外观存储测试保留并实际重跑 |
| `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T02-20261001-interactions-01/source-check.json` | **31** 项静态检查 passed，不代替产品测试 |
| `./scripts/Package.ps1` | 从已核对的构建 publish；保留 6 个实际 WinUI 资源；交付目录自动化通过才打 ZIP，退出 0 |
| `./scripts/Test-DesktopInteractionSmoke.ps1 -ExecutablePath <交付目录>/AutumnOS.exe -ReportDirectory artifacts/builds/T02-20261001-interactions-01/package-smoke`（Package 内实际调用） | **80/80 passed、21 张真实截图**；测试进程 6308、19944 正常退出，中文/空格隔离目录；报告身份与最终源快照完全匹配 |
| ZipArchive 遍历每项并 SHA-256 对照交付目录 | **554/554** 个文件字节一致，ZIP 无用户数据，EXE/XBF/PRI 齐全。`package-verification.json` |
| 与上一源清单及 ZIP 哈希对照 | **46** 个受保护源码/SDK/样例/测试/配置与旧桌面烟测脚本字节一致；旧桌面 ZIP 哈希一致，旧证据保留。`preservation-check.json` |
| `Start-Process -FilePath <实际交付EXE> -WorkingDirectory <交付目录> -WindowStyle Normal -PassThru` 后观察 8 秒 | **passed**：进程 **8508**、窗口句柄 **24118372**、标题 Lab Chronicles AutumnOS、Responding=true、非提权；没有测试置顶覆盖。`delivered-launch.json`；留窗供派蒙操作 |

新检查脚本 SHA-256：`6bfd07e1ebb722cb3d0106b7504449dd973e062aeb7bab01540e13c5e881c715`。受保护 46 项是在前述 45 项基础上额外计入旧 Test-DesktopSmoke.ps1，不是改变原核心文件范围。

### 交互检查的具体证据

五个分类均验证唯一选中态和右栏标题，左栏在详情左侧；默认外观。system/mist、light/warm、dark/night 真正落盘，重启后 dark/night 的配置字节、界面状态与首启记录保持不变。没有增加假开关；通知与控制中心只是如实说明尚无服务，未记功能完成。

未运行时真实单击桌面图标创建实例 `a33683d2-8ba3-4319-b69c-b1fe963e057b`。返回桌面后两个图标显示运行标记，单击分别持续观察仍为后台，Enter/Space 可打开面板。桌面双击间隔 **147.93 ms**、Dock 双击 **160.95 ms**（系统阈值 500 ms）；双击只弹操作面板，不恢复前台。继续后出现该同一 ID 的新 Foreground 事件，未保存昵称“派蒙·尚未保存”仍在，原存档哈希不变。面板确认结束后 Closed 且 blocksMaintenance=false、两个标记清除。再单击 Dock 创建新实例 `72a2073b-7367-4da6-a1e1-ff045caef424` 并可重新读档；长按 Continue 仍为新实例原 ID，右键 End 实际关闭并释放维护阻塞。原始安全生命周期日志复制至 `package-smoke/runtime-events.jsonl` 便于核对。

### 本地检查包与七张指定截图

- 快捷方式：工程根 `AutumnOS（T02 双栏与后台操作）.lnk`；创建前确认不存在，旧快捷方式保留。
- EXE：`artifacts/packages/T02-20261001-interactions-01/AutumnOS-0.2.1-t02-interactions-win-x64-development/AutumnOS.exe`。整个目录一起保留。
- ZIP：`artifacts/packages/T02-20261001-interactions-01/AutumnOS-0.2.1-t02-interactions-win-x64-development.zip`，**91,004,859** 字节，SHA-256 **`12f884ca3de1ff418c50f49857cb98c8dfb6d8c3be0cfe46701e2abefbc6b554`**。直接运行时生成的数据只在目录中，先前生成的 ZIP 不含用户数据。
- 本地开发检查包，release_candidate=false，未上传、未创建公共 Release、未做任何 Git 操作。

以下均为本构建 `package-smoke/` 下的实际 Windows 客户区截图，未合成或改画：

| 请求画面 | 文件 |
|---|---|
| 设置左栏总览 | `01-settings-overview.png` |
| 设置－外观 | `02-appearance.png` |
| 设置－开发者诊断 | `03-diagnostics.png` |
| 元素配对后台图标状态 | `04-background-icons.png` |
| 双击图标操作面板 | `05-double-click-actions.png` |
| 继续游戏恢复界面 | `06-continued-application.png` |
| 结束游戏后的桌面 | `07-closed-desktop.png` |

另有 hello/品牌/初始化、浅深桌面、原生菜单、许可、游戏保存、最大化、重启截图，共 21 张。已逐张查看以上七张以及浅深桌面，图像哈希与观察记录在 `visual-review.json`；派蒙最终设计审阅仍 **not_run**。

### 尚未执行和下一步

本轮两个交互及其自动化检查通过，不代表完整 T02。T01 全网络隔离、物理输入/触控/IME、DPI 矩阵、崩溃/真正挂起，T02 多页/文件夹/拖放/搜索/通知控制中心/其余个性化，以及 Windows10/干净机器/安装 EXE/真实 Logto 登录和登记核验继续 **not_run** 或保持原 in_progress。Logto 公开配置未改、Native/回调仍 unverified。T03 未进入，T04–T06 与原完整功能继续保留。

下一步是派蒙审阅本地新包和截图；收到后续范围再继续当前 T02，不以本轮五类导航替代原通知、多任务或完整桌面验收。

最终规范状态保存后，实际执行 `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T02-20261001-interactions-01/after-delivery`，ID 仍与交付构建一致，`after-delivery/verification.json` passed。用已有 `<USERPROFILE>/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe` 分别执行 `autumnos-spec/tools/check_plan.py` 和 `check_public_config.py`，**57 需求 / 65 验收 / 7 阶段结构、公开配置静态校验 passed**；日志 `plan-validation.log`、`public-config-validation.log`。未联系远端 Logto、未验证登记或登录，未安装工具。规范增加本次部分证据，旧证据与上一 local_increment 历史保留；T02 继续 in_progress，T03 继续 planned。

## T02 启动器单实例增量（2026-10-01，开发与验收记录）

制作人：派蒙。本次只新增启动器协调，不重做双栏设置、后台游戏面板、SDK 长会话修复，不进入 T03。已先核对现有源码、88 项源清单与历史产物，审计见 `docs/t02-single-instance-baseline-audit.md`；旧双栏包记录的 153 C# / 15 SDK / 80 窗口检查与 554 项 ZIP 字节可追溯，但不作为本次新代码已测试的证据。

已加入 `AutumnOS.Launcher` 和 WinUI 自定义入口，保留固定 .NET 10 / Windows App SDK、自包含启动及原业务模块。AppInstance 的跨版本身份范围不能作为产品级保证，故按 ADR 0003 采用路径/版本无关的用户 SID + 登录会话命名互斥，以及仅同用户、本机、固定有限消息的原生命名管道。只有取得主所有权后才创建 App/MainWindow/数据根；转发不会切换游戏或数据目录。超时返回 20、拒绝 21、启动失败 22，不按超时另开实例、不结束既有进程。Windows 不允许置前时保留真实 attention_requested，未冒充 foreground。启动锁不等于 T05 维护锁，旧二进制未实现协议不能追溯修复。

已执行辅助命令：解锁模式 restore 只用于接入无外部依赖的新项目引用，依赖固定版本未升级；`dotnet build AutumnOS.slnx -c Release -p:Platform=x64 --no-restore` 首次 0 警告/0 错误，日志 `artifacts/reports/single-instance-first-restore.log`、`single-instance-first-compile.log`。随后两次仅测试工程编译均 0 警告/0 错误。首次测试启动因报告目录未预建而在写命令日志时退出，未执行测试；修正调用目录后 `single-instance-unit-01/tests.json` 实际 **168/168 passed**。补充关闭所有权和 handler 故障回归后的结果见 `single-instance-unit-02/`，不覆盖首轮。`Test-Source.ps1 -ReportPath artifacts/reports/single-instance-source-01.json` **34 项静态检查 passed**，不代替产品验证。

辅助 UI 失败也保留：integration-01 的自有焦点探针窗口类无法提供 HTCAPTION，尚未发转发请求；integration-02 已验证普通/最小化/最大化召回和真实游戏保持、主进程挂起后超时不另开，但恢复后标题栏输入因遮挡安全拒绝；integration-03 系统实际返回 attention_requested，测试拒绝记为置前通过。修正的是测试前台权限/探针交互，产品没有改成强抢焦点或永久置顶。定点 foreground-diagnostic-01 连续三次前台召回通过。上述仍为 local-untracked 辅助报告；正式冻结构建、不同版本副本和最终交付 EXE 的结果将在后文单独记录。

需求追踪新增 R058/A058，保留原 57 项需求和 65 项验收、全部历史 evidence；T01/T02 继续 in_progress，T03 planned。Windows 10、跨用户/登录会话/UAC 权限矩阵、干净机器、安装包、Logto 登记和真实登录仍 not_run；未取消原开工包任何功能。

### 故障实测与首轮冻结失败（保留）

`single-instance-unit-02` 实际 170/170 passed。`integration-04` 后续核对发现真正的产品缺陷：挂起主进程期间客户端超时断开，恢复后 0.925 秒产生 2437 条 transport_unavailable；原因是 Broken 管道的 IsConnected=false 仍需要 Disconnect 才可复用。已修为无条件尝试断开并加入 50ms 异步异常退避；新 ACK 丢失回归执行 8 次实际断管，辅助 `single-instance-unit-03` **171/171 passed**。`integration-05` 恢复后 2.0098 秒只有 2 条新事件，其中 1 条 transport_unavailable，正常召回同窗通过；本轮最终仍 failed，因为仅改名独立 MainWindow.xbf 未能注入启动失败（资源仍嵌在 PRI），脚本正确拒绝把 D 副本的正常窗口算作失败通过。该测试缺陷在修正，不能宣称资源失败项已通过。

首轮冻结辅助版本 `T02-20261001-single-instance-peer-01` 与主构建 `T02-20261001-single-instance-01` 均 Release 0 警告/0 错误，源 `sha256:ffe7e690c6d3bf079ca60890de8b253f88f8e5d0a32d64127df3d6657b062566`。首轮正式 `Test.ps1` **170/171**，并发 8 次转发中存在响应未确认；SDK 因前置失败保持 not_run，没有打包。Windows 文档及管道边界确认服务端立即 Disconnect 会丢弃尚未读取的 ACK；已改为同一 1 秒响应预算内异步等待客户端读完并关闭，不采用无界 Flush/WaitForPipeDrain。正在加入延迟读取 ACK 的确定性回归。第一轮构建、测试和辅助版本全部保留；后续将使用新的构建 ID 和快照，不拿旧证据替代修复后验证。

### 修复后辅助实测与第二轮打包阻断

`single-instance-unit-04` 实际 **172/172 passed**，含新延迟 200ms 读取 ACK 测试；原 153 项均保留。`single-instance-resource-diagnostic-01` 首先发现缺 PRI/XBF 会产生 WinUI 原生退出 `0xC000027B`，未被 Main 外层捕获；已在主所有权取得后、XAML 前检查必要主资源，次实例不执行资源或数据初始化。`integration-06` **130/130 passed、8 张实机截图、23 次进程启动**：资源缺失返回 22，超时/无管道返回 20；恢复后 2.02 秒仅一条传输异常；6 进程在 53ms 内冷启只有主 PID 13688。此辅助轮使用同版本副本，distinct_build_versions 仍 not_run，没有冒充跨版本验收。

第二轮构建 `T02-20261001-single-instance-02` 及 `peer-02`，源 `sha256:0c2128a42837e08d535a4cffeb7ca99cde9f848a275cfd538c74a7f6db7db963`，Release 0 警告/错误、**C# 172/172、SDK 15/15 passed、35 静态检查 passed**。Package 在任何 UI/ZIP 前正确阻止：publish 的 `AutumnOS.deps.json` 与构建字节不一致。查明共享生成目录的增量构建保留了先前辅助版本的应用依赖清单标题；不是通过覆盖产物来忽略。构建脚本已改显式 `-t:Rebuild` 并核验 deps 中应用版本，辅助和主构建均使用新 ID 重做。第二轮失败发行目录、报告及两个辅助版本保留，无 ZIP 交付。

第三轮 `T02-20261001-single-instance-03` / `peer-03` 使用 Rebuild，源 `sha256:47a9e5f39aa38d11ada9e337851e8b180ee6506e090e71ccb7c24f57e83197cf`，Release 0 警告/错误、C# 172/172、SDK 15/15、静态 35 项及发布目录逐字节验证通过。原桌面 **80/80** 回归与 21 张截图通过；跨真实版本检查已确认主 PID 16484、另一版本转发、数据不串用、游戏输入保持及故障恢复日志有界，但在第 81 项已通过后，系统 TextInputHost（PID 12220，Windows 输入体验）遮挡测试探针标题栏，脚本拒绝向它点击并停止。没有结束或改动该外部进程，没有 ZIP。

只修正 `Test-SingleInstanceSmoke.ps1` 的探针焦点工具：有限候选点/5秒预算/7轮，仅自有探针可换位，产品窗口不移动；鼠标按下前仍复查精确 HWND、PID、光标和 HTCAPTION。定点 `single-instance-probe-reposition-diagnostic-03.json` 真实验证遮挡时不点击，只移动探针后安全激活；01/02 工具失败记录保留。随后使用 `peer-04`、主构建 `T02-20261001-single-instance-04` 重新冻结；最终结果以以下交付记录为准，不能把第三轮部分测试写成整轮通过。

### 单实例增量最终交付（已完成本轮本机验收）

制作人：派蒙。最终构建 **`T02-20261001-single-instance-04`**，版本 **`0.2.2-t02-single-instance`**，实际源快照 **`sha256:7045aa9f8b844bb07d4aa8b1cc4bb74e9d308b262ca8507dfc680789dedc9b68`**；commit=not_applicable。Windows 11 10.0.26200 x64 普通用户，项目内 .NET SDK 10.0.401，现有 Windows App SDK/WinUI/WebView2 固定版本与官方恢复源保持不变。此前所有失败构建、日志和旧包均保留。

实现使用统一产品协议 v1 的同用户 SID／SessionId 内核互斥与受限本机命名管道，名称不含路径、版本、构建 ID；在 App/MainWindow、WebView 和数据初始化前取得所有权。已有实例优先继续使用自身版本及 EXE 旁数据，次实例只发送固定召回请求并退出。启动未就绪会有界等待，超时/无法转发返回 20，拒绝返回 21，主启动失败返回 22；超时不会另开业务实例。主所有权保留到退出，崩溃后由内核释放。UI 调度恢复原 HWND，仅最小化时执行恢复，不改变页面、游戏、窗口坐标或关闭语义；无托盘、自启、永久置顶或系统前台策略改动。Windows 拒绝置前时真实记录 attention_requested 并有限闪烁任务栏，不能写成 foreground。详见 `docs/adr-0003-launcher-single-instance.md`，AppInstance 身份范围评估、ACK 断管修复、权限限制和 T05 交接边界均有记录。

### 实际执行命令与最终结果

以下报告均在 `artifacts/builds/T02-20261001-single-instance-04/`，与最终实际源快照及构建身份关联。

| 实际命令／操作 | 结果与证据 |
|---|---|
| `./scripts/Build-ProtocolPeer.ps1 -BuildId T02-20261001-single-instance-peer-04` | 独立真实编译版本 `0.2.2-t02-protocol-peer.1`，Release 零警告/错误；同源快照，版本和 deps 清单一致；产物在 `artifacts/protocol-peers/T02-20261001-single-instance-peer-04/distribution/`，不是只改版本标签的复制品 |
| `./scripts/Build.ps1 -BuildId T02-20261001-single-instance-04` | locked restore、Release Rebuild **passed，0 警告/0 错误**；实际源快照前后一致，程序集身份及 deps 版本、产物哈希核对通过 |
| `./scripts/Test.ps1` | **C# 172/172、SDK 15/15 passed**；原 153 项 C# 加 19 项启动器测试。保留并重跑宿主/SDK 12000 次长会话和真实外观存储覆盖。启动器测试含固定帧拒绝、延迟就绪/超时、8 并发、退出中保留所有权、异常 handler、断管恢复和延迟 200ms 读取 ACK |
| `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T02-20261001-single-instance-04/source-check.json` | **35 项静态检查 passed**；保留原需求/验收 ID，增加 R058/A058，共 58 需求、66 验收定义、7 阶段；不代替产品测试 |
| `./scripts/Package.ps1 -PeerExecutablePath '<WORKSPACE>/artifacts/protocol-peers/T02-20261001-single-instance-peer-04/distribution/AutumnOS.exe'` | publish 文件与构建逐字节相符，6 个 PRI/XBF 资源检查通过；依次通过下面三种 Windows 检查，之后才生成 ZIP。`package-result.json` |
| Package 内调用 `Test-DesktopInteractionSmoke.ps1 -ExecutablePath <最终交付EXE> -ReportDirectory <本构建>/package-smoke` | **80/80 passed、21 张真实截图**。原 hello/品牌/初始化、五类双栏设置、主题/壁纸落盘和重启、图标/Dock、双击面板、长按/右键、同实例继续/结束后新实例、未保存输入及存档全部重跑；原 Runtime/Packages/Storage/SDK 功能未重做 |
| Package 内调用 `Test-SingleInstanceSmoke.ps1`，输入最终交付 EXE 与真实 peer EXE | **132/132 passed、8 张真实截图、23 次主/转发进程启动**。多目录/工作目录、不同版本、快捷方式、窗口恢复、真实游戏、冷并发、无响应、转发失败、正常退出、隔离崩溃、资源缺失、恢复后日志有界均通过。`package-single-instance/single-instance-smoke.json` |
| Package 内调用 `Test-DeliveredLauncher.ps1 -ExecutablePath <最终交付EXE> -ReportDirectory <本构建>/delivered-executable` | **13/13 passed**；在最终发行目录原地执行 EXE，主 PID **22404**、HWND **2426908**，不同工作目录次 PID **13660** 退出 0；原主 PID/HWND 保留，主进程正常退出 0。此门禁前台/游戏/首启完成测试为 not_run，相关覆盖来自上述独立实机报告，不重复冒认 |
| ZipArchive 遍历文件并计算实际 SHA-256 | **556/556** 文件与最终目录匹配，526 个构建清单文件哈希匹配，EXE 和 6 个 PRI/XBF 齐全，无 AutumnOS_Data。`package-verification.json` |
| 对照双栏增量源快照、历史 ZIP/EXE 与报告 | **35 个受保护文件字节不变**（Runtime/Packages/Storage/Identity/Contracts、SDK/样例、已有测试与烟测、两份 Logto 配置、MainWindow.xaml）；2 个旧 ZIP、旧 EXE 哈希不变，22 份旧报告和 21 张旧截图保留。`preservation-check.json` |
| `./artifacts/builds/T02-20261001-single-instance-04/shortcut-verification/Test-DeliveredShortcuts.ps1` | **29/29 passed**；两个真实 .lnk、不同工作目录、精确窗口位置补验。此项在 ZIP 完成后执行，如实独立记录，不倒填为 ZIP 前门禁；最终程序/配置/存档/ZIP 字节均未变。`shortcut-verification/shortcut-verification.json` |

### 主实例、游戏与故障的实际证据

专门实机主 PID **7708**、HWND **18547416**。普通二次启动 **22212**、最小化 **7328**、最大化 **21844**、最大化后最小化 **20632**、快捷方式 **21632**、不同版本副本 **13852** 均转发退出 0，实际前台 HWND 为原窗口。前台断言在截图工具重新聚焦之前完成；仅测试自有窗口可临时置顶辅助真实合成输入，产品无置顶逻辑。鼠标按下前核验当前 HWND/PID/光标/命中位置，未点击或结束外部用户窗口。

游戏前台再次启动 **5940** 和后台再次启动 **21704** 后，游戏仍是 **`1a5bcebf-186b-425c-844a-36c3f4a058d6`**，未保存昵称 **“派蒙·单实例未保存输入”** 与已提交存档保持。后台召回保留桌面和运行标记，双击才开继续/结束面板；继续恢复原实例。原桌面回归另验证实际结束并新建游戏实例，未放宽原规则。

仅挂起脚本自己创建且路径/启动时间已核验的主进程时，次 PID **21404** 在约 **15.12 秒** 返回 20，没有第二套数据/业务窗口；恢复后观察 **2.1106806 秒**仅 2 个启动器事件、1 个 transport_unavailable，后续 **19732** 正常召回，无异常日志自旋。只有互斥无管道的测试 helper 场景，**5948** 返回 20，没有业务数据根。显式崩溃注入只终止隔离测试主 **7708**，随后 **11984** 正常取得新所有权并保留已提交数据；正常退出后 **22372** 再启动通过。资源故障隔离副本 **21076** 缺 PRI/XBF 返回 22，再启动 **16760** 成功，证明所有权可恢复；这些启动器故障项不冒充完整 T01 沙箱/游戏崩溃矩阵。

**6 次冷启动在 33 ms 内发起**，只有 **20964** 成为业务主实例，9644/22164/14272/13644/4724 转发退出 0，仅一次 primary/window_ready。真实不同版本 B 在 A 存活时只转发；A 退出后 B 的 **7472** 才用自己的版本与 EXE 旁目录运行，B 的数据哨兵不变，未复制 A 存档。

最终发行目录双快捷方式补验主 **20956**、HWND **6227946**，脚本将自有普通窗口设为 RECT **(37,43,1217,847)**；两个不同工作目录的真实快捷方式分别产生 **19296/21008**，均退出 0并回报原 PID/HWND，前后 RECT 完全相同。该补验确实观察到 **attention_requested**，前台置前项目保持 **not_run**，不拿 ACK 冒充前台成功；专门 132 项报告已有独立前台实测。补验正常关闭自有主进程退出 0，不遗留测试窗口、不删除数据。补验驱动作为独立 artifact 留存，SHA-256 `90976a3374b80c75a90eb56f05f8c7f405ba4391a01879bf00ba281a4bbbecc9`，不冒充冻结源快照成员。

### 新本地检查包与真实画面

- 新入口：根目录 **`AutumnOS（T02 单实例检查点）.lnk`**。创建前确认不存在；两条旧 T02 快捷方式保留。
- EXE：**`artifacts/packages/T02-20261001-single-instance-04/AutumnOS-0.2.2-t02-single-instance-win-x64-development/AutumnOS.exe`**。必须保留整个发行目录。EXE SHA-256 `bf5f4601b604d91a333ed9541e489183bedd0cc978f9b0ae90cb2a799247d4e0`。
- ZIP：**`artifacts/packages/T02-20261001-single-instance-04/AutumnOS-0.2.2-t02-single-instance-win-x64-development.zip`**，**91,031,405 字节**，SHA-256 **`174a48f7a8def8ddc9b015af4152482ffeea984288d43becd6b4e3d9a6af5d88`**。本地开发包，release_candidate=false；无上传、Git 操作、公共 Release 或云端 CI。
- 最终 EXE 原地验证产生的 `AutumnOS_Data` 保留在该新目录，ZIP 显式排除该目录，没有为打包删除用户数据。旧包和数据均未覆盖。
- 原桌面回归 **21 张**、单实例 **8 张**，共 **29 张**真实客户区截图。实际查看 **9 张**：桌面组设置总览/诊断/双击面板/继续/浅色/深色 6 张，以及单实例组 `03-restored-maximized-window.png`、`05-game-after-repeat-launch.png`、`06-background-after-repeat-launch.png`。逐图 SHA 和观察在 `visual-review-desktop.json`、`visual-review-single-instance.json`；其余 20 张未宣称逐图视觉审查，派蒙设计审阅 **not_run**。烟测原始报告捕获时的 captured_not_visually_reviewed 保留，后续审查由独立报告承接，不改写原运行记录。

### 验收范围、仍未执行与下一步

R058/A058 在上述 **Windows 11 普通用户、同用户/登录会话、支持 v1 的真实不同版本副本**范围 verified/passed，详细证据写入需求与验收文件；旧 local_increment 进入历史，正式发布门禁未改成通过。旧二进制未实现协议无法追溯协调，不承诺旧包也单实例，不为兼容强杀旧程序。

Windows 10、不同用户/登录会话、UAC/提权组合、干净系统、安装 EXE、物理鼠标键盘/触控/IME/DPI 矩阵继续 **not_run**。不同完整性级别可能受 Windows 权限拒绝，拒绝时不会启动第二个业务实例。文件关联、深层链接、账号回调尚未接入启动协议；Logto Native 与回调登记继续 unverified，真实登录 not_run，没有改公开配置或索要秘密。启动互斥仅协调启动器，未来 T05 必须另做维护锁、更新交接及健康回退，未宣称自动更新完成。

完整 **T01/T02 继续 in_progress，T03 未进入**；T01 网络隔离/物理输入/真正浏览器挂起等，T02 多页/文件夹/编辑拖放/搜索/通知控制中心/其余个性化，及 T04 商店发现 pm/sq/版本筛选/代理、T05 plus/meta 安全更新、T06 安装与完整 SDK/对接验收全部保留。下一步由派蒙运行新检查包审阅本增量，再按授权范围继续未完项；本次不扩展到 T03。

最终状态写入后实际执行 `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T02-20261001-single-instance-04/after-delivery`，源快照仍为 **7045aa9f…9b68**，与最终交付构建完全一致，`after-delivery/verification.json` passed。随后 `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T02-20261001-single-instance-04/after-delivery/source-check.json` **35 项 passed**。使用本机已有 `<USERPROFILE>/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe` 分别执行 `autumnos-spec/tools/check_plan.py` 与 `check_public_config.py`，**58 需求/66 验收/7 阶段结构、公开配置静态校验 passed**，日志 `plan-validation.log` / `public-config-validation.log`；未联系 Logto 或验证登录。只读 `Get-Process -Name AutumnOS -ErrorAction SilentlyContinue` 最终未发现遗留启动器，`final-process-observation.json` 留存；没有按进程名称清理任何用户程序。新快捷方式可直接打开完整交付目录中的 EXE。

## T03 账号、权限、数据与开发者能力（2026-10-01，用户已授权）

制作人：派蒙。以 T02-20261001-single-instance-04 为当前基线；本次用户明确授权按 T03-A/B/C/D/E 连续推进，T01/T02 剩余验收不因此补写通过，已完成桌面不重做。原八项主责 R030/R031/R033/R034/R037/R038/R039/R040 与 A030/A031/A033/A034/A037/A038/A039/A040、X02 全部保留；X01/X07 仅记录实际新增覆盖，不提前通过跨 T04/T05/T06 项。

本地规范核对：公开 Logto 六项值与本次用户约定完全一致；registration-status 仍 Native unverified、两个回调 proposed_not_confirmed_registered，已请派蒙核对准确字段，没有索要秘密。旧记录的发现校验成功不能替代类型/回调/真实登录。源码中的 IIdentityService/IPermissionService 原先并未形成真实接口，仅在规范中列名；Runtime 现有实现是游客存档授权，通知页也明确尚未接入。这是本轮待补实现，不把规范描述当成已完成能力。账户系统应用的产品约定将接到现有双栏设置，既有单实例协调继续位于业务之前。

开始新增真实身份合同与固定 OIDC/DPAPI 适配，模块并行实现但按检查点集成验证：身份与回环/会话；权限与 Runtime 绑定；Storage/Save 迁移与句柄；Shell/SDK/受控样例/开发者工具由集成维护。基础框架不升级，新增依赖仅从官方源恢复，更新并保留实际锁文件。此处记录的是正在实现，真实登录、负面测试、最终 EXE/ZIP 尚未执行，状态 not_run。真实命令、失败和最终结果将追加，旧包、源快照、测试与数据均保留。

### T03-A 至 T03-D 首轮整合与真实执行（辅助构建，尚未最终交付）

公开六项值保持原样。派蒙已确认精确登录回调和「Third-party app (Native app)」；已更新登记记录，退出回调仍 proposed_not_confirmed_registered。本机公开 HTTPS 发现读取实际通过（discovery-live-01.json），原开工包 DNS 失败存入 probe_history；未把可读发现文档视为认证成功。

新增固定 Duende.IdentityModel.OidcClient 7.1.0、Microsoft.IdentityModel.JsonWebTokens 8.23.0、ProtectedData 10.0.0，官方 NuGet restore 成功并保留锁；原基础依赖不变。完成宿主身份/回环/签名验证、DPAPI 与退出标记、epoch 租约、应用资料权限/撤销、按账号/来源绑定存档和文件句柄、桌面扩展实际服务、双栏账号及权限 UI、开发者模式工具与独立服务器验证样例。SDK 为 0.3.0，protocolVersion 仍 1，保留累计长会话保护与有界去重。受控样例 0.3.0 延续游戏状态与原 DOM，新增默认折叠的真实 SDK 操作面板；game.js 仅两处账号/权限文案改为真实当前语义。

实际日志均在 artifacts/reports/t03-foundation/，当前辅助 build_id=local-untracked，不能充当最终冻结包证据：

- Invoke-Dotnet restore AutumnOS.slnx -p:Platform=x64：restore-01/02 成功，固定新增依赖及锁。
- Invoke-Dotnet build AutumnOS.slnx -c Release -p:Platform=x64 --no-restore：solution-compile-01/02/03/04 均成功，最近两次零警告/零错误；最初单独身份测试编译的 2 项平台警告随后修正。
- Invoke-Dotnet tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll --report ...：identity-tests-01 为 215/216，实际 OIDC wrapped invalid_grant 分类错误已修；tests-02 为 272/274，旧格式备份恢复 Schema 标记与 Windows 文件替换测试修正；tests-03 **306/306 passed**。旧失败 JSON/日志全部保留；tests-03 之后的 late cancellation 和新增恢复测试尚须下轮执行。
- 同 runner --probe-logto --report artifacts/reports/t03-foundation/discovery-live-01.json：真实公开发现 passed，用户认证往返 not_run。
- ./scripts/Test-T03AccountSmoke.ps1 -ExecutablePath src/AutumnOS.Shell/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AutumnOS.exe -ReportDirectory artifacts/reports/t03-foundation/account-ui-01：**30 项 passed**，Windows 11 普通用户真实窗口，主 PID 13640/HWND 9110504，登录中次 PID 21608 转发退出 0。端口占用报 AUTH_PORT_IN_USE，原监听不受影响；再次启动只有原主回调监听、无第二次初始化；取消报 USER_CANCELLED 并释放端口。保留 4 张真实客户区截图：01 抓到转场偏淡，未作为合格视觉展示，04 已实际查看正常；脚本追加 350ms 稳定等待待重跑。浏览器已打开授权流程但未由用户认证；login/refresh/provider logout/第二账号均 not_run。
- 独立 server-identity csproj restore/build：server-restore-01.log/server-build-01.log，零警告/零错误。Test-ServerIdentityHttp-01.ps1 实际 **7/7** 本机 HTTP 检查：无配置 exit 2 先于监听；受控虚构配置仅验证本机负面请求，裸 ID、昵称、缺 Bearer、错误 header 等被真实 HTTP 401 拒绝。真实 Logto API 资源/独立应用登记与联调 not_run，未创建服务端配置、密钥或服务。

后续继续完整 T03-E：新增审计修复/回归、最终发行 EXE 原地检查、不同版本单实例、真实权限/通知/开发模式窗口、源快照/包哈希及 ZIP。真实认证需派蒙亲自浏览器操作，不收集密码/验证码/令牌；本地功能及模拟协议测试不会代替真实认证验收。T04/T05 未实施，旧包和用户数据不变。

### T03 提前交派蒙手动验证（2026-10-01）

用户最新指示「直接给出exe文件我来验证」，因此在现有源码上创建独立提前审阅包，不等待完整 T03-E 全套门禁，也不声称 T03 完成。制作人：派蒙。未覆盖旧包、快捷方式或用户数据。

- 实际命令 `./scripts/Build.ps1 -BuildId T03-20261001-review-01`：locked restore + Release Rebuild passed，0 警告/0 错误。版本 `0.3.0-t03-checkpoint`，源快照 `sha256:7f920f906ee73f0b06a70bd770243a3817a82e4682b5fbbb22828ba662bf3e85`，commit=not_applicable。
- 按本构建 `artifact-hashes.json` 逐文件核验源产物并 Copy-Item 到全新审阅目录，再次核验目标 SHA-256；包含完整自包含运行依赖、PRI/XBF、公开配置、样例和第三方许可证。不是只复制一个脱离依赖的 EXE。
- 实际命令 `./scripts/Test-DeliveredLauncher.ps1 -ExecutablePath artifacts/packages/T03-20261001-review-01/AutumnOS-0.3.0-t03-checkpoint-win-x64-development/AutumnOS.exe -ReportDirectory artifacts/builds/T03-20261001-review-01/delivered-executable`：最终交付路径原地 **13/13 passed**，普通用户真实窗口、不同工作目录次进程转发、原 PID/HWND 保持、正常退出、产物和数据保留。未机械统计 WebView 子进程；只关闭自己启动的窗口。该烟测未完成首启引导或游戏测试，不代替这些验收。
- 以上通过后才用 ZipArchive 生成新 ZIP，显式排除 AutumnOS_Data；保留真实运行创建的数据目录。ZIP 逐项与交付目录 SHA-256 比对、PRI/XBF/公开配置检查写在 `artifacts/builds/T03-20261001-review-01/zip-verification.json`。
- EXE：`artifacts/packages/T03-20261001-review-01/AutumnOS-0.3.0-t03-checkpoint-win-x64-development/AutumnOS.exe`；SHA-256 `32b11545480061dc4fcc2f6ab89f7e05ce37cfd130cd158452e3b2ac290c839a`。
- ZIP：同目录名加 `.zip`；91,734,315 字节，SHA-256 `9895756bbdc5529ef38501de4c041afaa976a6b307fdf4dd14f1c1d20652c316`。入口：根目录新建 `AutumnOS（T03 提前审阅 01）.lnk`。详细元数据 `review-package.json`。

辅助轮近期实际结果：solution-compile-05/06 均零警告错误；tests-04 为335/335（新增ES384的4项未包含在该轮），SDK与样例28/28，独立身份客户端新增ES384后21/21，server-http-02为7/7，desktop-ui-03为81/81及21截图，features-ui-04为32/32及7截图。这些是辅助构建证据，不能冒充本冻结包完整回归。recovery-files-ui-01至05失败报告保留；恢复流程已有20项实测通过，但文件选择器驱动窗口层级/控件等待仍未整轮通过，不写 passed。完整 Package.ps1、最终构建全部单测、最终构建完整桌面/权限/恢复/单实例矩阵本次 **not_run**，等待派蒙审阅后继续；失败与未执行不删除。

派蒙操作：打开上述 EXE 或新快捷方式，完成首启后进入桌面「账号」或设置「账号」，点击浏览器登录，自行在 Logto 页面认证；无需提供密码、验证码或令牌。只反馈界面状态/固定错误码，不复制带 code/state 的 URL。原有支持协议的 AutumnOS 如仍运行，新 EXE 会唤回它；需验证新版本时先正常退出旧窗口。新目录不会自动搬迁旧进度。

Native 类型与登录回调已获用户确认；退出回调仍未确认。真实登录往返/刷新/服务端退出、第二账号、独立服务器真实资源/应用登记与联调继续 not_run/blocked；未伪造成功。T03 保持 in_progress，T01/T02历史缺项保留，T04/T05未实施。本包为本地提前审阅，release_candidate=false；无上传、Git、云端 CI 或公共 Release。

### 2026-10-02 · 关闭整个程序后保持登录修复（正在最终实测）

派蒙明确反馈关闭整个 AutumnOS 再打开后账号丢失，已确认不是关闭账号页。保留制作人、旧包和旧数据；用户已自行正常关闭旧窗口，未代为结束用户进程。

查明并修改：旧UI登录后勾选保持登录未接服务；旧服务错误要求必须有refresh token才写密文；真实WinUI最终目录还暴露了NTFS权限问题，Identity目录属于当前用户且可写，但冗余SetOwner需要WRITE_OWNER，导致AUTH_CREDENTIALS_WRITE_FAILED。目录/文件安全改为只提交禁继承、当前用户FullControl的DACL，仍使用CurrentUser DPAPI、原子替换及退出标记，不降级明文。

界面主按钮为明确的「登录并保持登录」，另保留「仅本次登录」。新增宿主专用SetRememberSignInAsync，已登录时开关立即保存/清理，不改变账号代次或游戏实例、不偷偷申请scope。无刷新令牌也可保存尚有效的已验证会话，但不能延长有效期；真正失效仍要求认证。关闭窗口只Dispose内存任务；主动退出账号才SignOut。迟到刷新使用最新持久化意图；本地写失败不抹掉有效内存身份；429/5xx/暂时网络错误保留真实缓存状态，轮转后UserInfo暂时失败不会丢掉刚收到的新凭据。

实际失败记录：T03-20261002-session-fix-01 Release零警告/错误、364/364 C#通过，但account-smoke及account-smoke-02失败。真实浏览器SSO先于截图完成，安全脚本拒绝截图；白名单诊断记录「已登录」与AUTH_CREDENTIALS_WRITE_FAILED，没有读取或保存昵称、令牌、回调URL。Identity目录无密文且仍继承ACL。artifacts/reports/t03-session-fix/acl-probe-01/results.json在全新自有目录复现：SetOwner+DACL失败UnauthorizedAccessException、HRESULT -2147024891；相同仅DACL操作通过。01失败目录和报告全部保留，未生成ZIP。

02重新冻结：T03-20261002-session-fix-02，0.3.1-t03-session，源sha256:8db871fd6f75683d410277c2d7b2a7ded325344a9c754c254ae667d886bcd359。实际 ./scripts/Build.ps1 -BuildId T03-20261002-session-fix-02 零警告/错误；./scripts/Test.ps1 全365/365 C#、22/22 SDK、6/6样例、21/21独立身份客户端合成协议、7/7服务端负面HTTP通过；./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T03-20261002-session-fix-02/source-check.json 41项通过。新增26项会话/DPAPI/Modify权限/OIDC故障回归与真实Logto验收分开记录。

使用artifacts/reports/t03-session-fix/Prepare-Review.ps1 -BuildId T03-20261002-session-fix-02 -Stage Prepare按产物哈希新建完整目录，含PRI/XBF和依赖许可证，不复制任何AutumnOS_Data。./scripts/Test-DeliveredLauncher.ps1针对最终目录EXE原地13/13通过；真实已登录后重启恢复与最终ZIP继续验证，未在此提前填通过。已有T03 review01 EXE/ZIP哈希仍与原报告相同，artifacts/reports/t03-session-fix/preservation-before.json留存。

### 关闭后保持登录修复：02最终交付与真实认证结果

**本次修复已完成本机实测，不等于完整T03验收。** 制作人：派蒙。最终仍为 `T03-20261002-session-fix-02` / `0.3.1-t03-session`，源快照 `sha256:8db871fd6f75683d410277c2d7b2a7ded325344a9c754c254ae667d886bcd359`。

最终本机实际命令/证据：

- `./artifacts/reports/t03-session-fix/Test-SessionPersistenceSmoke.ps1 -ExecutablePath artifacts/packages/T03-20261002-session-fix-02/AutumnOS-0.3.1-t03-session-win-x64-development/AutumnOS.exe -ReportDirectory artifacts/builds/T03-20261002-session-fix-02/session-persistence -IncludeLocalLogout`：**37/37 passed**。取最终交付EXE的逐字节隔离副本，真实系统浏览器沿用用户已有Logto浏览器会话完成SSO，宿主通过真实回调/协议验证到SignedIn；没有输入账号凭据或读取浏览器内容。真实session.dpapi落盘，目录/文件均禁继承且只有当前用户Allow；原主PID6700正常关闭后，新PID17804重开同一路径自动恢复SignedIn，无新登录按钮点击/回调监听。返回桌面再进账号仍登录；次PID24508转发退出0且保留原窗口和身份。取消保持立即清凭据但当前身份继续；再次关开PID21844为游客。再次真实登录后本地退出清理成功。正常清理仅脚本持有的进程，无强杀用户程序。
- 上述报告是**真实Logto登录/关闭恢复/本地退出**实测，区别于365项合成/本地测试。没有检查或输出实际refresh token、昵称、用户ID、完整claims或回调URL；不将重启成功冒充长期刷新/所有设备退出。存档保留检查使用脚本自行创建的游客记录字节，不冒充真实游戏UI保存。未捕捉已登录个人信息截图。
- `./scripts/Test-T03AccountSmoke.ps1 -ExecutablePath <同一最终EXE> -ReportDirectory artifacts/builds/T03-20261002-session-fix-02/account-smoke`：**32/32 passed**、4张真实非个人账号页面截图。主PID3980/HWND1246698，登录等待中的转发PID6312退出0。取消场景明确使用“切换账号/重新认证”（prompt=login），避免已有SSO瞬间完成而使等待截图失效；真实持久登录由前一专用脚本覆盖。端口占用、取消释放、单实例同事务全部通过。实际查看01-account-guest.png，双栏保留、按钮无重叠；其余截图未宣称逐图视觉审阅。
- 02 Release零警告/错误、C#365/365、SDK22/22（保留12000次长会话）、样例6/6、独立身份客户端21/21、服务器负面HTTP7/7、静态41项、最终目录原地EXE13/13均已实际通过，原始日志在本构建目录。不同版本完整132项/桌面81项矩阵本轮未重跑，不冒用旧结果。
- `./artifacts/reports/t03-session-fix/Prepare-Review.ps1 -BuildId T03-20261002-session-fix-02 -Stage Finalize`：在最终EXE原地验证及真实持久化检查后才创建ZIP；**572/572文件**与交付目录SHA-256一致，PRI/XBF齐全，无AutumnOS_Data、.dpapi或运行日志。生成不覆盖旧入口的新快捷方式。该review driver为本次窄修复本地工具，哈希见after-delivery/verification.json，不冒称跑完完整Package.ps1及全部T03-E。

交付入口：

- EXE：`artifacts/packages/T03-20261002-session-fix-02/AutumnOS-0.3.1-t03-session-win-x64-development/AutumnOS.exe`，SHA-256 `98e9005e45a93a427c8630e605aca9229ad35e67fb9c7f5cbd3355926da6d6a4`。
- ZIP：同目录名加`.zip`，91,737,151字节，SHA-256 `6ab3d54a453b6495c80276b0ba140e8d4c4deb73a510df1d28440390e5d2ea3d`。
- 新快捷方式：根目录`AutumnOS（保持登录修复 T03-20261002-session-fix-02）.lnk`。请保留完整目录，第一次打开新包点击「登录并保持登录」；以后关闭窗口再打开同一目录即可恢复仍有效的会话。新包数据独立，未自动复制旧账号/存档。

R030/A030增加关闭/保持要求与本次证据；A030为in_progress，未通过的其余完整条件保留。真实浏览器退出及其回调登记、第二账号、长期实际刷新/过期、换机DPAPI、Win10与干净机器继续not_run；其他T03缺项、T01/T02历史缺项、T04/T05/T06不变。01失败目录与两份失败账号报告保留，不伪装为通过。此前review01 EXE/ZIP字节再次确认不变；最终New-SourceSnapshot结果与02构建一致，after-delivery源检查41项通过，无遗留AutumnOS窗口。没有上传、Git操作或公共Release。

### 2026-10-02 · 登录回调页视觉与动态效果增量

制作人：派蒙。按用户截图修饰真实浏览器回环响应页，未重做桌面或身份服务。最终构建 **T03-20261002-callback-ui-01**，版本 **0.3.2-t03-callback**，实际源快照 **sha256:f8f17cb61e99ccf50d74528137c18067e758dee13189aa7ddd132958d44e0517**，commit=not_applicable。

新增 `src/AutumnOS.Identity/CallbackResponsePage.cs`，由现有 `LoopbackCallback` 返回。原创叶片 SVG、暖色渐变、半透明圆角卡片、淡入与有限次数漂浮；支持系统浅/深色、窄屏、减少动态效果与强制颜色。品牌和制作人来自集中 BrandInfo。有效接收仅显示「请求已送达」，最终认证结果仍由原生客户端校验；错误/重复请求显示中性重试说明，不伪造登录成功。Alt+Tab 是真实快捷键提示，不是虚假启动按钮。纯固定 HTML/CSS，无 JavaScript、第三方资源、URL 参数反射或账号信息；CSP 只允许该固定 CSS 的 SHA-256，原 no-store/no-referrer/nosniff 与路径、方法、state、单次消费约束保留。实现及验收边界见 `docs/t03-callback-page.md`。

实际执行与结果（报告根 `artifacts/builds/T03-20261002-callback-ui-01/`）：

- `./scripts/Build.ps1 -BuildId T03-20261002-callback-ui-01`：固定依赖 locked restore、Release Rebuild **零警告/零错误**，源与产物身份一致。之前预览编译零警告/错误，不冒充正式构建。
- `./scripts/Test.ps1`：**370/370 C#、22/22 SDK、6/6样例、21/21独立身份客户端、7/7服务器负面 HTTP 通过**。新增5项回调响应/CSP/真实本机 TCP 测试覆盖正确回调、错误 state 后可继续接收、拒绝回调、退出回调、UTF-8长度与敏感参数不反射；既有取消/过期/重复回调、会话和12000次SDK长会话测试保留。合成身份协议与下述真实Logto测试区分记录。
- `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T03-20261002-callback-ui-01/source-check.json`：**41项 passed**。
- `./artifacts/reports/t03-callback-page/Prepare-Review.ps1 -BuildId T03-20261002-callback-ui-01 -Stage Prepare`：新建独立完整目录，逐文件匹配编译产物哈希，保留PRI/XBF、配置及许可证；未复制用户数据。
- `./scripts/Test-DeliveredLauncher.ps1 -ExecutablePath artifacts/packages/T03-20261002-callback-ui-01/AutumnOS-0.3.2-t03-callback-win-x64-development/AutumnOS.exe -ReportDirectory artifacts/builds/T03-20261002-callback-ui-01/delivered-executable`：最终交付路径原地 **13/13 passed**，真实主窗口、不同工作目录启动只转发、PID/HWND保持、正常关闭，保留产生的数据。
- `./artifacts/reports/t03-session-fix/Test-SessionPersistenceSmoke.ps1 -ExecutablePath <上述最终EXE> -ReportDirectory artifacts/builds/T03-20261002-callback-ui-01/session-persistence -IncludeLocalLogout`：最终EXE逐字节隔离副本 **37/37 passed**，真实系统浏览器Logto SSO及新版回调完成；主PID26152正常退出后PID5508恢复受DPAPI保护的会话，转发PID22208退出0且不新建登录事务，取消保持后PID23964重开为游客，本地退出清理成功。未读取浏览器凭据、用户资料、令牌或回调URL；存档保留使用测试自建记录字节，不冒充实际游戏操作或长期刷新。
- `./scripts/Test-T03AccountSmoke.ps1 -ExecutablePath <上述最终EXE> -ReportDirectory artifacts/builds/T03-20261002-callback-ui-01/account-smoke`：**32/32 passed**，主PID18168/HWND2033244，登录期间次PID23456转发退出；真实端口冲突不假登录、不结束占用者，prompt=login等待期间取消释放监听，仍只有原窗口和登录事务。用户回复已关闭旧窗口后才执行，测试仅正常关闭自己的进程。
- 从最终交付程序集反射导出固定响应页到 `callback-render-input/`，执行本地Node `artifacts/reports/t03-callback-page/render-preview.cjs <输入目录> <callback-visual输出目录>`：本机Chromium151.0.7922.34实际渲染与录屏，5类检查 passed；984×718无水平/垂直溢出，390×844无水平溢出，浅/深色正确，采样transform实际变化，减少动态效果时0个运行中动画，无外部请求/控制台错误。这是实际编译HTML的浏览器渲染证据，不是认证成功截图；真实认证由前述专用报告证明。
- `./artifacts/reports/t03-callback-page/Prepare-Review.ps1 -BuildId T03-20261002-callback-ui-01 -Stage Finalize`：在最终EXE实测之后生成ZIP，**572/572文件字节**与交付目录一致；ZIP不含AutumnOS_Data、凭据和运行日志，PRI/XBF齐全。`./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T03-20261002-callback-ui-01/after-delivery` 与正式源快照一致；驱动和截图哈希、旧包保护检查见 `after-delivery/verification.json`。

实际效果文件位于 `callback-visual/`：`01-light.png`、`02-dark.png`、`03-narrow.png`、`04-invalid.png` 均已人工查看，`callback-motion.webm` 为真实浏览器录屏。未合成图片冒充产品截图，也未截取个人账号资料。

失败/调整记录全部保留：`artifacts/reports/t03-callback-page/compile-preview-01-setup.json` 因日志目录未建立在启动编译前失败，标记not_run；建立目录后的compile-preview-02成功。`visual-01` 首次预览发现984×718垂直溢出，正式冻结前提高短窗口断点并复测通过，旧预览不覆盖。无依赖升级或全局组件安装。

本次本地交付：

- EXE：`artifacts/packages/T03-20261002-callback-ui-01/AutumnOS-0.3.2-t03-callback-win-x64-development/AutumnOS.exe`，SHA-256 **4ec3a4495626721211729480dbd66a47fe2d11309c36a8454b3a391a05f3fec6**。
- ZIP：同目录名加`.zip`，**91,742,931字节**，SHA-256 **b49b2505615d4d5000acd2ef99868d93981d09c347f7d8c388897dc3c9c77cc1**。
- 不覆盖旧入口的新快捷方式：根目录 `AutumnOS（回调页美化 T03-20261002-callback-ui-01）.lnk`。保留整个EXE目录。用此版本重新发起登录即可看到新响应页，已经打开的旧浏览器标签不会自动改变。新版使用自己旁边的数据，不静默搬迁旧包存档或账号。

本轮是视觉窄增量与相关回归，**完整T03仍in_progress**，不是正式发行候选；完整Package.ps1、桌面/不同版本全部矩阵、Windows10/干净机器、服务端退出登记与真实退出、第二账号、长期真实刷新/过期仍not_run或沿用既有阻塞，不冒用历史通过记录。下一步由派蒙审阅新回调页并继续原T03待验收项；不进入T04/T05。所有旧包、用户数据和失败报告保留，无Git操作、上传或公共Release。

## 2026-10-02 T04 开发切换与辅助验证（冻结前）

派蒙明确授权直接进入 T04，暂缓 T03 完整收尾。首先发现根 `AutumnOS_T04_Codex_Prompt.md` 不存在，如实说明；随后按派蒙提供路径完整读取 `G:/下载/AutumnOS_T04_Codex_Prompt.md` 与原开工包两层目录的 `autumnos-spec/tasks/T04.md`，同时保留工作区有效规范。输入文件哈希：`artifacts/reports/t04-foundation/prompt-inputs.json`。T03 已有保持登录、DPAPI/DACL、回调页及未完验收均保留。

已实现源码：新增 Store 模块（真实 GitHub 查询/三层清单/版本分页/缓存/安全下载/网络配置），原生商店详情、下载、我的应用与设置接线；Packages 增量安装事务、来源绑定、不可变版本内容、运行租约、修复卸载和存档降级检查；受控独立 storeprobe 样例与真实回环 HTTP；本地发布 CLI。一般第三方运行 gate 保留，不以安装成功替代安全隔离验收。原 Runtime/Identity/SDK 和元素配对延续，仅补安装/启动/账号切换必要接口。

辅助命令与实际结果（`local-untracked`，不能代替最终冻结 EXE）：
- `Invoke-Dotnet -Arguments @('restore','AutumnOS.slnx','-p:Platform=x64') -LogPath artifacts/reports/t04-foundation/restore-01.log`：通过；仅新增项目引用及锁文件，无基础框架升级。
- `dotnet build AutumnOS.slnx -c Release -p:Platform=x64 --no-restore`：compile-01 因 Shell 局部变量 title 命名冲突失败，并带 XAML 连带错误；修正后 solution-compile-02/03 零警告零错误。solution-compile-04 为 Shell 小修编译，通过。日志全部保留。
- `dotnet tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll --report artifacts/reports/t04-foundation/tests-preview-01.json`：459/459 通过。之后新增的历史页来源与关闭竞态不冒用这次结果。
- 安装模块 24/24，CLI 实际进程 12/12；下载→安装集成从 10/12、12/14 的实际失败修到 14/14。`artifacts/reports/t04-install/` 保留 pipeline-run-02（重复元数据权限与重复 JSON 字段）、pipeline-run-04（提交后 Dispose 回执竞态）失败及后续修复证据。
- 生产 GitHub 适配器真实匿名查询 `topic:autumnos-app is:public`，2026-10-02 UTC 02:27:50 HTTP 200，0仓库，非缓存、无截断，search remaining=7；报告 `artifacts/reports/t04-catalog-live/live-01.json`。真实公开 Release 安装 not_run；没有填充商品、创建仓库或上传。
- `Test-T04StoreSmoke.ps1` 对编译输出的隔离副本运行，ui-preview-01/02/03 原生窗口和真实公开搜索已观察；01失败于脚本等待无 UIA peer 的 Grid，02还发现筛选控件重挂载导致内容未完整显示，03修正内容后真实网络设置保存/重启恢复，但脚本刚切分类立即取控件存在时序竞态。截图、失败报告与测试数据均保留，不能标整条通过。脚本只正常关闭其自行启动的进程。
- 工具调用错误曾把新建 Store.csproj 路径误传为日志路径，覆盖的是本轮新文件；原错误输出保存在 download-agent-build-failed.log，恢复该新项目 XML 后统一编译通过。没有覆盖用户旧源码或旧包。

当前仍在完成原生流程、冻结构建/最终目录测试和 ZIP；这些步骤此时 not_run。UI 审查 `ui-review.md` 是源码审查，不冒称运行通过。T04 保持 in_progress，T03 由用户暂缓收尾，T05 不实施。

## 2026-10-02 T04 本地商店检查包交付

制作人：派蒙。当前交付构建 **T04-20261002-store-04**，版本 **0.4.0-t04-store**，实际源快照 **sha256:7558aa6362f13317c6bce3f657e1e1d26539c83826e1f7107fbc012f4d703784**。commit=not_applicable。Windows 11 10.0.26200 x64、普通用户；项目内 .NET SDK 10.0.401，现有固定 WinUI/Windows App SDK/WebView2 依赖和锁文件保留。两份用户提供的 T04 文件均已读取；原始开工包不修改。以下为新执行事实，覆盖前段“冻结前”的尚未运行状态，历史记录和失败报告均保留。

### 实际实现与接口

- **T04-A**：已有桌面与 Dock 接入原生 Autumn Store；发现/搜索、详情、版本历史与稳定/预览筛选、兼容性/来源/类别/运行环境/离线/已安装/更新筛选、我的应用和错误/空状态均调用真实服务。GitHub 匿名查询固定包含 `topic:autumnos-app is:public`，不要求 Logto/PAT。PM/SQ/未分类/标签冲突按仓库自标展示，PM 未核验且没有额外权限。分页、去重、ETag/Last-Modified 缓存、离线和限流分开处理；兼容/离线/更新筛选明确作用于已载入详情。历史 Release 有 34 页/1020 条保护上限，不假装无限历史。
- **T04-B**：新增 Store 下载模块，真实队列/字节/速度/剩余时间、暂停/继续/取消/重试、1–4 并发、总限速、缓存和有界历史；Range/If-Range/强 ETag 校验与安全重新下载。GitHub 加速进入既有双栏设置，分别保存系统代理、公开 API 模板和附件模板，支持 HTTPS/模板校验、直连回退、分开的连接测试。运输层无 Cookie/PAT/Logto 令牌；不代理身份服务或应用自己的流量。服务端异常、HTML 代替包、断流、空间/大小/摘要错误都有失败状态。
- **T04-C**：网络、本地选择、拖入单个 `.autumn` 共用安全验证与安装服务；拒绝普通 EXE。三层 `autumn.store.json` / `autumn.release.json` / `manifest.json` 核对来源、应用、权限、版本、环境、入口、存档格式、大小和 SHA-256。注册表原子提交，版本内容不可变，提交后才产生桌面入口和真实原生通知；重复请求幂等。运行租约覆盖启动到实际 WebView 释放，后台也阻止更新/修复/卸载。支持固定版本、独立应用预览、修复、更新、降级前格式扫描/备份和卸载保留存档。下载完成与安装完成分开记录；提交后收据写入失败不伪称回滚，重启仅在来源/元数据及安装文件全部核对后协调收据。
- **T04-D**：真实本地发布 CLI 提供 pack / generate-release / validate-publication；复用清单/包校验，无远端写入。新增模块接口、事件顺序、生命周期、并发/幂等/取消、错误、数据归属、正反例、源与附件要求、故障排查和 Windows 操作说明。`docs/t04-store-checkpoint.md`、`t04-catalog.md`、`t04-downloads.md`、`t04-installation.md`、`t04-developer-cli.md` 与 `sdk/store.schema.json` / `release.schema.json` 保持实现对应；原 JavaScript SDK 和类型未另造安装协议。格式版本统一限制 1..1000000。

受控测试使用独立 `cn.labchronicles.storeprobe`、真实 `.autumn` 和本机有限 HTTP 服务，通过同一目录/下载/安装服务，界面明确标识“本地集成测试数据，不是 GitHub 实时结果”。只能由游客在开发者工具明确开启，不默认启用、不持久化启用配置；索引与包哈希绑定当前编译内样例。关闭开发模式/账号切换会撤销测试服务和能力，保留存档。**一般第三方运行门禁没有放开**，仍待 T01 网络隔离完整证据；受控样例成功不等于任意社区应用已具备完整安全隔离。

原登录保持、DPAPI/DACL、回调 HTML、Logto 公开配置、SDK 长会话保护和单实例协议保留。既有元素配对与样例存档延续；安装内置样例的首次注册不冒充商店下载通知。04 仅在 03 之后修正桌面第三排长名称裁切，未重做桌面。

### 最终构建实际命令与结果

报告根：`artifacts/builds/T04-20261002-store-04/`。以下结果全部关联上述冻结源和最终交付字节。

| 实际命令 / 操作 | 结果与证据 |
|---|---|
| `./scripts/Build.ps1 -BuildId T04-20261002-store-04` | locked restore、Release Rebuild **passed，0 警告/0 错误**；构建前后源快照一致；`build-result.json`、`build.log`、`artifact-hashes.json` |
| `./scripts/Test.ps1 *> artifacts/builds/T04-20261002-store-04/test-driver.log` | **463/463 C#、22/22 SDK、6/6样例、21/21独立身份客户端合成提供者、7/7服务器合成HTTP、12/12实际CLI进程 passed**。C# 包含安装24、下载/传输22、下载→安装协调14及原安全/存储/账号/启动器/12000次长会话回归；各类报告分开，不称为真实 Logto 联调 |
| `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T04-20261002-store-04/source-check.json` | **49 项静态检查 passed**；不代替 Windows 产品测试 |
| `. ./scripts/Common.ps1; Invoke-Dotnet -Arguments @('tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll','--probe-store','--report','artifacts/builds/T04-20261002-store-04/github-live.json') -LogPath artifacts/builds/T04-20261002-store-04/github-live.log` | 2026-10-02 **03:09:16 UTC**，真实生产适配器匿名 GET，**HTTP200、0仓库、非缓存、无截断、search remaining=9**。`real_release_install=not_run`，不注入演示商品 |
| `./scripts/Package.ps1 -Checkpoint T04 -Stage Full` | publish --no-build 与构建逐文件匹配；6个 PRI/XBF 资源保留；先执行下列三个 Windows 门禁，再创建 ZIP 和独立快捷方式；`package-result.json` |
| Package 内 `Test-T04StoreSmoke.ps1 -ExecutablePath <最终交付EXE> -ReportDirectory <证据>/store` | **64/64 passed、19张真实截图**。最终 EXE 字节相同的隔离副本：公开商店浅深色、真实网络设置保存/重启、明确开启测试源、详情/版本、下载字节、安装/桌面/通知、SDK存档、后台召回/继续/结束、后台阻止升级、真正结束后重试升到1.1.0、固定/预览重启恢复、坏摘要拒绝、修复及卸载保留数据 |
| Package 内 `Test-DesktopInteractionSmoke.ps1 -ExecutablePath <最终交付EXE> -ReportDirectory <证据>/desktop` | **81/81 passed、21张真实截图**。hello→品牌→初始化、首启记录、原有五类双栏切换、主题/壁纸真实持久化、元素配对单击/双击/长按/右键、同实例继续、结束后新实例、未保存输入及存档、正常重启均重新验证 |
| Package 内 `Test-DeliveredLauncher.ps1 -ExecutablePath <最终交付EXE> -ReportDirectory <证据>/final-path` | **13/13 passed**；最终交付路径原地运行，不是只运行 bin/Release。主 **PID26828 / HWND32180148**，不同工作目录次 **PID26708** 转发退出0、只有一次主窗口；原主保持，正常关闭退出0，未向不同工作目录建数据 |
| ZIP 白名单构建及 ZipArchive 逐字节读回 | **586/586 文件匹配**，6个 PRI/XBF 保留；无 AutumnOS_Data、用户凭据/存档/敏感运行日志或已启用测试源配置。最终目录烟测产生的独立数据保留，不为压缩而删除 |

原生证据目录：**`artifacts/builds/T04-20261002-store-04/package-t04-f2c671110f404c9e9c5d691c0fd2d105/`**。所有 UI 驱动只正常关闭自身记录的测试进程，没有强杀用户窗口。

商店样例主 PID24992，转发 PID26980；前后实例均 **2367405d-1ba6-4d5a-9786-b5a12afc009a**，后台唤回没有把游戏切前台，双击面板的“继续”保留未保存文字与 WebView。升级 1.1.0 在后台租约存在时显示等待安装，原注册/资源/存档不变；真正结束后明确重试才提交，重新打开得到新实例 **06a2b3ba-2aa2-4b53-bac4-cf5f99c50356** 并读取原存档。日志/状态/字节证据见 `store/store-smoke.json`，不是靠截图推断实例相同。

真实产品共捕获40张截图，实际逐图查看12张并记录 SHA-256 与观察于 **`visual-review.json`**：商店浅色/深色、网络设置、详情、真实下载进度、安装桌面、后台操作、继续后的输入、升级提交、卸载后桌面、原双栏外观和深色桌面。第三排应用名称裁切已在04截图确认修正；后台菜单位于窗口下方，两项动作可见，完整 DPI/触控矩阵仍未审查。其余28张仅记录捕获，不冒称逐图审阅；派蒙设计确认 not_run。

### 构建与失败历史（全部保留）

- 辅助 solution-compile-05 通过；06 因通知事件 `ApplicationRegistryChange` 与 `InstalledApplication` 类型接线错误失败，修正后07零警告/错误。原 ui-preview-01/02/03 失败保留；04定位到确认按钮自动化命中底层同名按钮，05定位到 PowerShell 动态作用域 `$condition` 被辅助函数修改。驱动分别改为精确 ContentDialog PrimaryButton 和独立变量名；没有为通过而删除断言。
- **T04-20261002-store-01**：源 `8a3b2f45edc8642c69146ceeaae9ca7c611056c35e2466abcc1e73c041de94ba`，构建通过，C#462及其余回归通过、静态45项，完整64商店/81桌面/13最终路径通过；旧01开发目录、ZIP、快捷方式保留。
- **T04-20261002-store-02**：源 `aaa05f86f616157075af373996548a86dadcbffd1eeddb2ce4d6d51f4823977c`，构建通过；新存档格式上限负面测试期待了错误的映射码，实际 **462/463，failed**。生产代码按合同正确返回 `STORE_RELEASE_INVALID`，测试原期望 `RELEASE_METADATA_INVALID`；修正期望后另用新构建。`test-runs/a3832776a9874d1d91b0c288634e0d12/tests.json` 保留；该轮后续门禁 not_run，无ZIP。
- **T04-20261002-store-03**：源 `b0cb4218be48d064822cec188569b1575bf31d685e01afa062c5423de8e94ee0`，构建及463/22/6/21/7/12、静态49、64/81/13原生门禁全部通过；补齐历史上限/Schema入口一致性说明。真实截图发现第三排长名称被滚动区域裁切，随后修正并新建04；03旧包和证据保留，不冒用03结果作为04。
- **T04-20261002-store-04**：以上最终结果。没有覆盖失败报告，没有删用户数据让测试通过。EXE apphost 在多个构建中可以相同，所以证据同时关联 DLL、源快照、BuildId、deps 和完整文件清单，不仅比较 EXE 单个哈希。

### 本地交付与派蒙验证入口

- 快捷方式：根目录 **`AutumnOS（T04 商店 T04-20261002-store-04）.lnk`**，新文件，不覆盖任何旧入口。
- EXE：**`artifacts/packages/T04-20261002-store-04/AutumnOS-0.4.0-t04-store-win-x64-development/AutumnOS.exe`**。保留完整目录。EXE SHA-256 **e0614f16b813cb9828ed7fcbd10cf090b7552558dbf65438b26663f32a839dff**。
- ZIP：**`artifacts/packages/T04-20261002-store-04/AutumnOS-0.4.0-t04-store-win-x64-development.zip`**，**91,970,291字节**，SHA-256 **771de34a8a89701ab54c5829da5af62d48fe2cf3e9aa8ed93a95b04c692a3d4a**。本地开发包，public_release=false、未签名，不是正式发行候选。
- 派蒙先保存并正常退出旧版，再通过新快捷方式打开。协议要求已有兼容启动器优先，因此旧窗口未退出时新版只会唤回旧窗口；不是本包没有启动。每个发行目录继续使用自己的数据，不搬迁旧账号和存档。
- 桌面进入 Autumn Store 查看真实公开结果；设置 → GitHub 加速可以保存配置并测试公开接口。没有可用附件时附件测试有明确限制，公开 API 成功不等于附件加速成功。
- 需要本地验证安装链路时，游客进入设置 → 开发者诊断 → 开启开发模式，返回桌面打开开发者工具 → 本地商店集成测试 → 确认。选择1.0.0下载安装、从桌面打开、保存测试文字、返回/继续；后台尝试升级1.1.0应等待，结束应用后重试才提交。此源明确不是真实 GitHub；关闭/重启不自动启用，不与元素配对存档混用。

### 验收边界、保留待办与下一步

需求 R020–R029、R054 与验收 A020–A029、A054、X01/X03/X04 已逐项追加04证据和缺项，详见 **`acceptance-coverage.json`**。全部保持 **in_progress**，不因模块/样例通过就标完整阶段 verified。

- **真实公开 Release 安装/更新/附件 CDN**：blocked by no matching public repository；本轮 not_run。真实 GitHub 返回0与本地受控HTTP成功分别记录，不创建仓库/Topics/Release填补结果。
- **完整一般第三方运行隔离**：前置 T01 缺项继续保留；当前受控样例成功不扩展安全承诺。PM无例外，没有关闭现有隔离。
- **真实下载加速服务**：无已配置服务，真实 API 直连已测，实际附件加速/故障回退未实网联调；相应受控运输测试已测。
- **UI 尚未跑满的矩阵**：本地文件选择器/物理拖入、下载暂停/继续/取消全部原生操作组合、全部筛选组合、原生降级、物理输入/DPI/触控与真实大量历史结果 not_run；相关服务/CLI/清单/格式与故障测试不冒充这些 UI 验收。便携包没有注册 `.autumn` 系统文件关联。
- **跨环境与故障**：Windows10、干净机器、安装器、签名、物理断电 not_run；本轮安装故障为受控注入。完整单实例不同版本/冷并发/挂起/崩溃矩阵未整轮重跑，本轮实际覆盖冻结C#协议、不同工作目录原地召回、样例后台召回和原桌面生命周期；不借用旧轮132项作为本轮新结果。
- **T03 暂缓**：真实登录保持已有实现与历史证据保留，当前身份/SDK/服务端测试为合成提供者回归；没有把历史真实登录、第二账号、长期刷新或未确认的服务端退出当作本轮通过。T03完整E收尾继续暂缓。
- **T05/T06**：未实施主程序更新、维护锁/交接、plus/meta签名与密钥轮换；安装写入的关键操作协调不代表T05完成。原需求和跨阶段验收继续保留。

下一步先由派蒙审阅本地 T04 检查包，再补齐上述 T04 原生操作矩阵；出现真实合规公开应用后补真实Release链路。通用第三方执行仍需前置隔离证据。当前授权不延伸到 T05，不初始化/提交Git、不上传、不建远端资源、不使用云端CI。

最终记录后执行 `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T04-20261002-store-04/after-delivery`：**218 个实际源文件，源 ID 仍 7558aa63…3784，与构建完全一致**。重新运行静态检查49项通过；项目已有 Python 执行 `autumnos-spec/tools/check_plan.py`（58需求/66验收/7阶段）与 `check_public_config.py` 均静态通过，未联系 Logto 或证明登记/登录。只读后验 `after-delivery/verification.json` 确认 ZIP 哈希未变、586条目无用户数据/凭据路径、新快捷方式目标正确、旧 T03 EXE/ZIP 哈希未变、没有遗留 AutumnOS 进程；没有结束任何用户进程。此次后验未再次运行快捷方式，不把目标字段校验冒称快捷方式实测。

补充只读保护核对 `artifacts/reports/t04-foundation/preservation-check-final04.json`：28个指定 Identity/Launcher/Logto配置/SDK及框架配置文件的当前字节同时匹配T03基线与04冻结源。此范围不包括有意增量修改的Store/Packages/Shell，不宣称所有文件或所有锁文件不变，也不代替运行验收。

## 2026-10-02 桌面多任务与移动增量（开发中，尚未交付）

派蒙要求将“图标双击后台面板”改为“桌面空白双击多任务”，并允许移动桌面图标。以 T04-20261002-store-04 为基线增量实现，没有重做 Runtime/账号/商店，也没有进入 T05。旧源码改动、旧包、快捷方式和数据保留。用户已明确正常关闭旧窗口；自动化仅启动并正常关闭自己记录的测试进程。

当前源码：真实多实例切换器、空态/F6/Esc、单击图标恢复原实例、拖动网格排序和独立原子 desktop-layout.json；长按/右键与键盘移动入口保留。完整阶段状态不因两个交互改变。docs/desktop-multitask-arrange.md 记录范围：当前三列排序，不冒充 Dock/跨页/文件夹/抖动编辑全部完成。

冻结前实际命令与结果（local-untracked，不冒用最终构建）：
- 项目本地 dotnet build AutumnOS.slnx -c Release -p:Platform=x64 --no-restore，compile-01/02/03、compile-debug-04/05 均零警告/错误，日志在 artifacts/reports/desktop-multitask/。
- Test-DesktopInteractionSmoke.ps1 对编译输出的隔离副本运行：ui-preview-01 为81项通过（不包含后加拖动检查），空白双击、继续原实例、桌面/Dock/键盘单击恢复及旧桌面回归通过。ui-preview-02/03 与 ui-debug-04 的新增拖动均 failed，松手后游戏打开，布局未落盘；截图和报告保留，后续整链未执行。ui-debug-04 保留有界手势诊断，只记录控件与指针状态，不记游戏内容或凭据。脚本12次 SetCursorPos 实际移动光标，但宿主只收到一次阈值内 PointerMoved，正在修正输入驱动并验证产品捕获逻辑，不把该失败隐藏。
- dotnet tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll --report artifacts/reports/desktop-multitask/layout-tests-preview-01.json：483/483 passed，其中新增 desktop_layout 20/20。报告、真实命令日志、二进制与源哈希审计分别保留。是辅助预览，不是最终冻结包通过。

当前最终 EXE/ZIP、新快捷方式、冻结构建和最终目录验证仍 not_run。T03收尾继续按派蒙决定暂缓；T02/T04完整缺项保留。下一步修好实际输入链，验证多实例切换与布局持久化，再新建构建 ID/源快照、跑回归和最终路径 EXE，最后生成不含用户数据的 ZIP。

冻结前补充：ui-debug-05 的真正 mouse_event 移动证实 ButtonBase 持有指针，直接向网格 CapturePointer 返回false。修为先释放按钮捕获，再由稳定网格接管并抑制Click，处理同一指针、Esc/失焦取消及低优先级清理。compile-debug-06 零警告/错误；ui-debug-06 桌面原生 **106/106 passed，23张截图**，拖动、取消不落盘、不误开、重启实际位置与布局字节通过。诊断代码已从最终源码移除；旧未绑定的图标双击Flyout代码移除，新行为只有桌面切换器入口。额外图标间隙/取消后键盘断言将在冻结包执行，不借用106项结果。

Store 多实例预览：store-preview-01 已发现两个真实实例，随后截图helper错误地等待StoreHeading而失败；store-preview-02成功截图双实例，随后取消确认脚本错误地寻找PrimaryButton而非CloseButton失败。均为本轮新测试驱动问题，已修精确就绪控件与对话框按钮ID，失败报告和截图保留；store-preview-03继续验证中。最终构建 T04-20261002-desktop-01 已开始，结果待后续记录。

T04-20261002-desktop-01：源 sha256:974ebf483d21c2fd9daedf136660734d861ee1264d519323ba3c54de0b8fcb28，Build.ps1 passed零警告/错误；Test.ps1实际483 C# /22 SDK /6样例 /21合成身份客户端 /7合成服务器HTTP /12CLI通过；静态49通过。Package Full中最终字节的隔离商店69/69通过（20截图），桌面新增“滚动区图标间空白双击”通过，但“取消拖动后立即Enter开设置”超时failed。保存的失败截图仍在桌面，并非对话框误识别；该轮完整桌面、原地最终路径和ZIP未通过/未执行。整个目录保留，不发布这个失败包，ZIP未生成。

进一步修正输入捕获：保留ButtonBase本身的捕获直到真实松手，让原生控件收到release并清理其键盘状态；原位抑制拖动Click，取消时抑制延续到松手，不再把捕获提前转给Grid。准备compile-07与ui-preview-07验证；不会覆写desktop-01报告。完整T03/T04未宣称完成。

修正后的 ui-preview-07 已实际 **112/112 passed，23截图**：保留按钮捕获使取消后Enter恢复；滚动区图标间隙双击、真指针拖动/取消、保存字节与重启位置、同实例/输入、长按/右键、主题/壁纸全部经过新检查。compile-07 零警告/错误。新建冻结 **T04-20261002-desktop-02**，源 **sha256:36f5ff28f2c98cc017d51804164a625c818e357d1117f09080b6ab9b562d53d8**；Build.ps1已passed零警告/错误。最终回归和Package门禁进行中；不会将desktop-01失败目录覆盖或压缩交付。

## 2026-10-02 桌面双击空白与图标移动增量交付

制作人：派蒙。最终构建 **T04-20261002-desktop-02**，版本 **0.4.1-desktop-multitask**，实际冻结源 **sha256:36f5ff28f2c98cc017d51804164a625c818e357d1117f09080b6ab9b562d53d8**（222文件），commit=not_applicable。本节是本轮重新执行后的结果，历史失败与旧交付全部保留，不用旧测试替代新构建证据。

### 实现与操作

1. **双击桌面空白处打开“正在运行”**，包含图标滚动区域的真实空隙；F6是键盘入口，Esc/完成/遮罩关闭面板。单击空白不打开，图标、Dock、按钮、小组件和滚动条不当作空白。切换器展示实际存活实例，有进入动画并尊重Windows关闭动画设置；没有实例则显示空态，没有伪造游戏截图或实例。
2. **图标单击打开或恢复原实例**；双击应用图标不再是后台管理入口。切换器支持两个真实应用同时显示；继续保留原WebView/未保存输入，结束要求确认并真实释放实例；取消结束不影响两者，结束一项不关闭另一项。长按/右键继续、结束仍保留。正常返回桌面仍不结束游戏，单实例唤回不抢切游戏页面。
3. **桌面图标按住拖动排序**，松手保存，其他图标依次让位；Esc/拖出网格取消。按钮保留原生指针捕获直到松手，避免拖动触发Click，也避免取消后键盘失效。长按600ms与8 DIP拖动阈值协调，菜单增加向前/向后移动。当前为现有三列网格内排序，不把Dock/跨页/文件夹/抖动编辑算作完成。
4. 布局独立写入EXE旁 **AutumnOS_Data/Config/desktop-layout.json**，原子提交、唯一稳定宿主ID、512条上限、保留隐藏ID位置、严格格式/大小/重解析点检查，坏记录或失败保留旧数据。缺失布局只读取默认排列，不自动改旧设置；主题、壁纸、首启、账号与存档格式不变。文档 **docs/desktop-multitask-arrange.md** 与实现同步。

### 真实构建和验证

环境：Windows 11 10.0.26200 x64、普通用户；项目内SDK10.0.401和固定依赖锁。报告根 **artifacts/builds/T04-20261002-desktop-02/**。

| 实际命令 | 结果 |
|---|---|
| `./scripts/Build.ps1 -BuildId T04-20261002-desktop-02` | locked restore、Release Rebuild，**0警告/0错误**；构建前后实际源快照一致 |
| `./scripts/Test.ps1` | **483/483 C#**（新增布局20），**22/22 SDK**（含12000次长会话）、**6/6样例**、**21/21独立身份客户端合成提供者**、**7/7服务器合成HTTP**、**12/12真实本地CLI进程**；分别保存报告，不冒称真实Logto登录 |
| `./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T04-20261002-desktop-02/source-check.json` | **49项静态检查 passed**，不代替产品实测 |
| `./scripts/Package.ps1 -Checkpoint T04 -Stage Full` | publish与冻结字节匹配，保留6个PRI/XBF；先执行下列门禁，再生成ZIP和新快捷方式 |
| Package内 `Test-T04StoreSmoke.ps1 -ExecutablePath <最终交付EXE> ...` | **69/69 passed，20真实截图**：公开商店、明确标识的本地受控源、安装/保存/桌面/升级/卸载，以及两个实际实例同列、取消结束、只结束选中项、单实例后台唤回、原实例和未保存输入保持 |
| Package内 `Test-DesktopInteractionSmoke.ps1 -ExecutablePath <最终交付EXE> ...` | **112/112 passed，23真实截图**：空白与滚动区间隙双击、空态、真实指针拖动、Esc/越界取消、不误启动、取消后Enter、主题/壁纸、布局字节与实际位置重启恢复、继续/结束/长按/右键、hello首启和存档 |
| Package内 `Test-DeliveredLauncher.ps1 -ExecutablePath <最终交付EXE> ...` | **13/13 passed，1真实截图**：最终交付路径原地启动、不同工作目录次实例转发、主PID/HWND不变、只一个主窗口、正常退出、数据在EXE旁 |
| ZIP按冻结白名单创建并ZipArchive逐项读回 | **586/586文件哈希匹配，6个PRI/XBF**；不含AutumnOS_Data、凭据、真实存档/敏感日志或启用的测试源配置 |

原生报告目录：**artifacts/builds/T04-20261002-desktop-02/package-t04-4466b2d5653b49728f97068e29389a1d/**。前两项对最终目录字节一致的隔离副本执行，最后一项对最终路径原地执行，不混称只测试bin/Release。脚本只关闭自己记录的进程，没有强杀用户窗口。

原地主PID **18376**、HWND **6358878**，不同工作目录次PID **3304** 转发退出0，原主窗口保持后正常退出0。商店样例继续前后实例同为 **d4e1e932-29a2-44d3-8814-cbd2553a9bc9**；元素配对原实例 **29a1b053-e6fa-4132-b2e0-2c6558a17281**，结束再开另建实例。布局与存档/输入证据在JSON和运行事件内，不单凭截图推断。

新捕获共44张真实窗口截图，实际查看6张（浅色双应用切换器、拖动后的桌面、深色切换器、继续后的游戏、结束后桌面、重启后桌面），路径、SHA与观察在 **visual-review.json**。其余仅记录捕获，不冒称逐张审阅；派蒙设计审阅not_run。自动输入为本机Windows受限合成鼠标/键盘/UIA，物理触摸/笔/DPI矩阵仍not_run。

**artifacts/reports/desktop-multitask/preservation-final02.json** 证明：对store-04源新增4/修改14/删除0；Runtime、Packages、Identity、Launcher、SDK、样例、Contracts、Store模块、12个依赖锁、固定依赖和Logto公开配置字节不变，旧EXE/ZIP真实哈希仍匹配原交付。新源码只增量修改Shell/布局存储/测试/脚本/文档及品牌版本；不初始化/提交Git、不上传、不建远端资源。

### 本地入口与派蒙检查

- **EXE：artifacts/packages/T04-20261002-desktop-02/AutumnOS-0.4.1-desktop-multitask-win-x64-development/AutumnOS.exe**。SHA-256 **8a5bc61cb017b5fafa0548bd24dbf084f97a57c8e96dd8c174749ceb8b32e659**；请保留整个目录。
- **ZIP：artifacts/packages/T04-20261002-desktop-02/AutumnOS-0.4.1-desktop-multitask-win-x64-development.zip**，**91,982,144字节**；SHA-256 **770f7d12672fa678e3f1ec54d2f878c9b0d261a5024ec4b3760c6cc6e8e36492**。
- **新快捷方式：AutumnOS（桌面多任务 T04-20261002-desktop-02）.lnk**。已核对目标路径；本轮未再通过快捷方式启动，不将字段检查冒称快捷方式实测。旧入口未覆盖。
- 先正常关闭旧版再打开新EXE，避免单实例把启动转给旧窗口。新目录使用自己的数据，旧账号/存档/布局不被静默搬迁。先拖动任一桌面图标并重启查看位置；打开元素配对写入测试昵称、返回桌面，双击空白后点继续核对原内容，再结束核对运行标记消失。长按/右键也可继续和结束。
- 最终目录烟测自行创建的数据保留；ZIP严格排除该数据。不得把测试数据混入派蒙旧目录，亦没有为通过而清空旧数据。

### 验收边界和下一步

**desktop-increment.json** 为本轮范围证据；R010/R011/R012/R015/R058 与 A010/A011/A012/A015/A058 已追加对应证据，当前两个交互与所跑回归passed；完整T02/T04仍in_progress，A058原历史通过保留但本轮只证明列出的回归子集。旧“双击图标”验收保留为历史，并以派蒙最新规则覆盖当前交互。

保留所有原待办：Dock移动、多页/文件夹/抖动编辑、拖动边缘滚动、完整无障碍/触摸/多屏DPI、Windows10/干净机器/安装器not_run；没有因这两个交互删掉功能。完整单实例冷并发/跨版本/崩溃/超时矩阵本轮未重跑，已有历史与本轮不同工作目录/后台唤回分别记录。

本轮商店UI继续访问真实公开GitHub，受控本地下载安装来源有明确标识；没有额外跑独立实网探针，也未获取合规公开Release，真实Release安装仍not_run，不以受控源冒充GitHub实网。一般第三方运行仍受T01安全门禁约束。真实Logto、第二账号和长期刷新本轮not_run；登录保持/DPAPI/DACL/回调页实现与历史证据保留，**T03完整收尾仍由用户暂缓**。不进入T05、不声称整产品发行通过；ZIP为未签名本地开发检查包，无公共Release。

前述 ui-preview-02/03、ui-debug-04/05、store-preview-01/02、desktop-01 的失败报告全部保留；store-preview-03实际69通过，ui-preview-07实际112通过。本次通过不覆盖这些历史失败。下一步由派蒙审阅新版两个交互，后续按授权补齐原T02/T04待办。

交付记录后再次执行 New-SourceSnapshot.ps1：222文件源ID仍为36f5ff28…53d8，与desktop-02冻结构建一致；Test-Source.ps1再次49项静态通过。after-delivery-verification.json确认ZIP哈希未变、独立快捷方式目标正确、没有遗留AutumnOS进程；未触碰任何旧包与用户数据。

## 2026-10-02 T05 开发与首次真实演练（进行中）

- 授权主责 R044/R045/R046/R047/R055；保留所有历史验收。原 `tasks/T05.md` 和任务索引改为 in_progress，其他阶段不提升。
- 新增 Update、UpdateProtocol、稳定 Bootstrap、独立 Updater、Setup、Update.Cli；Shell 实际业务入口为 AutumnOS.Client.exe，用户入口保持 AutumnOS.exe。系统更新接入原双栏设置，使用独立主程序预览配置；生产源固定 paimeng5201314/autumnlab，读 Release 列表、SemVer、签名/渠道/RID/兼容/序号与有效期验证，复用 T04 下载、缓存和加速。
- 统一关键操作协调覆盖真实游戏生命周期、配置/存档/恢复/迁移/安装/身份持久化；主程序退出前取得资格并再验证，独立更新器持有独立维护 Mutex + 旧业务 Mutex + 按物理根占用租约。主机和更新器分别验签，稳定入口/原数据根保留，持久事务和核心健康 IPC；没有把单实例 Mutex 当更新维护锁。
- 清单/签名/信任根 Schema 和真实生成/签名/验签工具已实现；RSA-PSS/SHA-256，可信公钥通过编译输入接入。仅测试私钥在当前用户 ACL 限制临时目录，尚留待修复后签名，未进入源码/包；结束需清理。生产公钥及 Authenticode 证书未配置，不做 unsigned 降级。
- 本机无 ISCC/NSIS/WiX，已用项目内固定 SDK 编译真正自包含 Setup，而非 ZIP 改扩展名；默认当前用户 Programs 安装，HKCU/开始菜单登记仅测试唯一名称。安装/卸载都有持久日志，不清用户数据或未知文件。固定 WebView2 154.0.4258.53 官方 CAB 下载/微软签名验证成功并完整随包；.NET/WinAppSDK 自包含、PRI/XBF/deps/许可证分别检查，没有全局安装。
- 主要接口/恢复/签名/构建文档：docs/t05-updates-and-recovery.md、t05-signatures.md、t05-maintenance.md、t05-release-engineering.md、adr-0007-safe-update-transaction.md；现阶段数据 schema=1，拒绝不可逆或不兼容迁移，动态在线信任根轮换不宣称实现。

真实命令与证据（这部分为当时结果，后续修复必须新构建）：

| 命令/范围 | 真实结果与证据 |
|---|---|
| Build-T05LocalDrill.ps1 -RunId 20261002-drill01 ... | B 生成并签名；第二个 Bad 构建被 Build.ps1 的 Assembly.LoadFile 自占 DLL 阻止。修为读字节，保留所有失败日志；artifacts/reports/t05-foundation/drill01-console.log |
| Build-T05LocalDrill.ps1 -RunId 20261002-drill02 ... -RunNativeDrill | A=0.5.0、B=0.5.1、Bad=0.5.2（测试核心健康失败编译）均实际 Release 0警告/0错误；源 sha256:0b1fe33c3bd5b070ecd25249aee7fb37125c572dc9cef7ffd382b046b64a14ad，包含测试公钥/feed公开输入；artifacts/t05-drill/20261002-drill02/drill-builds.json |
| A 上 Test.ps1 | 548/548 C#、22 SDK、6 mock样例、21合成Native身份、7本地合成HTTP、12实际CLI通过；artifacts/builds/T05-20261002-drill02-A。身份协议fixture不是真实账号登录。 |
| Package-T05.ps1 Prepare/Finalize -SkipNativeRegression | 851条目ZIP、实际Setup EXE、848文件signed payload；artifacts/builds/T05-20261002-drill02-A/package-result.json。Skip只限隔离演练，原生更新专项另记录，setup_execution原始not_run保留。 |
| Test-T05UpdateSmoke.ps1 | 真实原生A页面、默认开关、实际保存/拖排、篡改签名拒绝、签名恢复后下载、Foreground/Background各35秒不提交、未保存输入保留均通过；文件替换失败，本次整体failed。报告 artifacts/t05-drill/20261002-drill02/native-drill/update-smoke.json，截图和数据目录保留。 |
| 失败现场只读核验 | journal=rolledBack/UPDATE_APPLY_FAILED；848旧受管文件及6个真实数据文件hash全部一致，尚不等于重新启动A通过。native-drill/post-failure-rollback-bytes.json。根因实际文件fixture复现：持FileShare.Delete占用保护时File.Move(overwrite=true)返回0x80070005，正在改为有持久日志覆盖的同卷替换。 |
| Test-T05Setup.ps1 实际A02 Setup | 安装真实成功、交付和安装文件一致、原生首次引导/单实例/系统更新页检查通过。随后卸载被测试脚本LoadFromAssemblyPath自己占用的Contracts.dll阻止；脚本进程退出后同一卸载日志重试exit0成功，所有现场保留。artifacts/reports/t05-setup-exe-development-01；正在修正测试元数据读取，不将失败报告改通过。 |
| 静态发行隔离审查 | drill02-B客户端PE嵌入测试公钥/feed与公开输入hash一致；独立Updater压缩bundle中的Update.dll与客户端同hash，851文件无禁止私钥扩展/用户数据路径。artifacts/reports/t05-drill02-distribution-audit.json。只读审查不代替验签/原生更新。 |

当前仍未完成：修复后A→B与健康失败回退的真实通过；实际Updater中断恢复与旧v1竞争；最终普通无测试根包、安装/卸载/安装版自更新；正式公钥/签名Release与真实身份、Windows10/干净机器范围独立blocked/not_run。真实GitHub只读Release探测HTTP200、0 Release（artifacts/reports/t05-production-release-probe.json），不创建假Release。T03收尾继续暂缓，不开始T06。

## 2026-10-02 制作人后续范围调整

派蒙明确指示：“暂时不做安装包版本了，只把可以直接使用的 exe 版本实现要求即可”。本轮暂停新的 Setup 构建、安装/卸载/安装版更新验收，保留已写源码、此前真实产物、成功/失败报告和隔离数据；不删除历史、不补写通过。交付调整为完整便携目录的 AutumnOS.exe + ZIP + 更新 payload/签名材料，继续 T05 的安全更新、A→B、回退、故障恢复和普通构建隔离验证。Package-T05 默认不再生成 Setup，报告记 deferred_by_user；T03 收尾与 T06 边界不变。

## 2026-10-02 T05 便携更新主链实测通过（继续故障与最终交付验证）

当前遵循派蒙“只做直接可用 EXE，安装版暂缓”的调整。drill03 的真实 A=0.5.0、B=0.5.1、Bad=0.5.2 均来自源 sha256:c132879bf2393e3dcc13fa5ff1a4364c283a820fdb7194fe4cb42b924bdfc496，分别编译了自身 BuildId，A 实际包含完整更新协议；Bad 仅测试条件编译使核心健康失败。测试密钥只用于这些独立构建，签名材料和未改动 A ZIP 保留。

`Test-T05UpdateSmoke.ps1` 使用 drill-builds.json 中的实际 EXE、B/Bad signed_material、编译绑定 feed，最终报告 **artifacts/t05-drill/20261002-drill03/native-retry-06/update-smoke.json：72/72 passed，cleanup passed，无遗留进程**。driver/helper 摘要单独记录，测试是在发行目录的逐字节隔离副本，不是 bin/Release；本地 FileStream Release fixture 不冒充公网 GitHub。

实际通过：篡改签名拒绝；真实下载并验证 B；真实 Foreground/Background 各35秒等待，未保存输入保持；用户保存、结束并实际释放资源后才提交；独立 Updater 替换848受管文件、新版完成核心初始化；原 AutumnOS.exe 字节与原快捷方式保持；新旧实际程序集身份与 DLL 摘要核对；首启/外观/桌面布局/游戏存档/应用登记/权限共6份数据字节保持，并在新B通过游戏 SDK 实际读回。故意健康失败恢复可用A和同样6份数据，记录坏构建，显式再检查不重新暂存，观察20秒无自动更新回退循环；原快捷方式再次启动得到预期A。两场维护中重复启动均拒绝第二业务实例。真实Starting/Suspended/Closing竞态仍由独立协调fixture覆盖，不借前后台UI试验扩展断言。

本轮失败和编排历史全部保留：
- drill03/native-drill：前台等待后窗口几何无效，随后未保存输入查找超时；无更新事务，非产品升级成功。driver后补仅自持窗口的最小化恢复和几何证据。
- native-retry-01：受保护的拖动坐标与实际指针不符，失败保留，未删除断言或重试掩盖输入。
- native-retry-02：负责人错误地把全局协调C# fixture与实机同时运行，两者出现 UPDATE_COORDINATION_BUSY；取消自启任务，保存 orchestration-interruption.json。此非合格测试成绩。协议fixture/实机后改为串行。
- native-retry-03：调用中的B材料目录拼写错误，真实failed报告保留；改为从drill-builds.json读取所有参数。
- native-retry-04：产品实际提交B，但driver误限定MessageBox确认按钮ID=1；本机真实唯一“确定”为ID=2。报告及post-failure-product-audit.json保留；848受管文件匹配，后续残留B期间desktop-layout记录发生改变，因此该轮不宣称全部数据保留。负责人按精确PID/创建时刻/父进程/路径核对后正常关闭自启B，未按名称强杀。
- native-retry-05：driver读取存档恰好遇到真实FileShare.None提交锁，整体failed，cleanup passed。仅对IO共享/锁冲突32/33加入最多3秒读取等待，JSON坏内容及其他错误仍失败；产品锁和数据验证没有放宽。
- native-retry-06：上述全链72项重新实测通过，不覆盖历史失败。

普通构建中间产物 portable01/02/03保留。portable02 串行 Test.ps1 553/553 C#及22 SDK/6 mock样例/21合成身份客户端/7合成HTTP/12实际CLI通过；此前与实机并行的test-runs/580b17afe9e74a5ca3b75bb9a832d4b7保留为中断失败。普通包最终编译为 **T05-20261002-portable-04 / 0.5.0-t05-portable / sha256:d792d2a18b7c84b3b28fe9ec42c6a46ed0bd6b8bb965293ceb5ae1dbfcee512f**，Release 0警告/0错误，Prepare和55项静态检查通过；最终同构建模块回归/原生门禁/ZIP将在故障专项结束后串行执行，目前不提前标通过。About中遗留T02字样已改为“本地开发检查版 · 未公开发行”。

正式公钥仍未配置，普通版独立验签器保持fail-closed；portable02/03最终DLL执行probe及Updater bundle审查通过，最终04另记新报告，不套用旧包。正式更新材料仅本地未签名准备稿，Release/Asset ID为明确占位，必须在未来另行授权发布并获得真实身份后重建清单；不会使用测试签名冒充生产签名。临时测试私钥已精确删除，公开公钥/签名/负载保留，证据 artifacts/reports/t05-foundation/test-key-cleanup.json；无生产私钥进入本机。

接下来继续独立Updater替换/健康阶段中断恢复、真实旧v1启动竞争、文件占用中止与暂停自动重启专项，然后最终便携交付回归。安装包、T03收尾、T06公开发布仍按用户指示暂缓/未授权；没有Git操作或远端写入。

## 2026-10-02 T05 便携 EXE 最终本地交付

制作人：派蒙。**以此节和首部当前阶段为最新状态**，前述进行中、失败和旧包描述保留为历史。安装版按派蒙要求暂缓，没有本轮新 setup.exe 交付；已写 Setup 源码及旧演练产物保留。解决方案回归仍编译既存 Setup 项目，不代表恢复安装器打包/安装授权。T03 收尾继续暂缓，未开始 T06、未上传、未执行 Git 或创建远端资源。

### 当前实现与检查点

- **T05-A**：原双栏设置的系统更新接通当前程序集/BuildId、独立主程序预览设置、Release 列表候选、说明、下载、签名/负载验证、等待、重启与恢复状态。固定生产仓库 `paimeng5201314/autumnlab`，预览关闭仅 plus，开启仅 meta；数字 SemVer 比较和严格 Tag/清单/预发布/RID/兼容/资产绑定，不按发布时间或字符串排序、不降级，切渠道立即废弃旧候选。复用 T04 的下载缓存、限流和 GitHub 加速服务。
- **T05-B**：真实游戏资源租约从 Starting 持续到 WebView/能力/流清理结束，Foreground/Background/Suspended/Closing 及资源未释放均阻止提交；安装、存档/配置/备份恢复/迁移/身份关键写入统一协调。准备维护时原子阻止新冲突操作，复查状态后才交接，保留未保存输入。独立维护互斥、旧业务互斥、物理根租约与受限管道协作；稳定入口不会在半更新目录另开业务实例。未按进程名强杀游戏。
- **T05-C**：独立 Updater 自行验证原始清单 RSA-3072-PSS 签名、负载、交接身份及目标根；严格受管文件白名单、持久日志与已核验备份。稳定 AutumnOS.exe、AutumnOS_Data、未知文件和凭据不覆盖。新版核心初始化及健康协议成功后才确认，不以窗口/进程存在代替。失败恢复旧版与兼容数据，坏构建隔离；临时占用中止另用持久暂停状态，明确重试前不自动重启循环。当前仅接纳兼容 schema 1；不可逆迁移拒绝。在线动态信任根轮换未实现，已有离线可信根更新文档。
- **T05-D（本轮便携范围）**：真实同构建 EXE 完整目录、ZIP、更新 payload、清单和校验材料；普通构建无测试信任根/feed，测试构建绑定独立本地源与测试公钥。正式签名材料缺少生产公钥/发布身份，准确保留 blocked；没有以 SHA-256 代替签名或将测试私钥放进发行包。

接口与规范：`schemas/autumn.update.schema.json`、`schemas/autumn.update.sig.schema.json`、`schemas/autumn.update.trust.schema.json` 和 `tools/AutumnOS.Update.Cli/`；用户操作见 `docs/t05-updates-and-recovery.md`，签名/密钥轮换见 `docs/t05-signatures.md`，维护/时序/错误和超时见 `docs/t05-maintenance.md`、`docs/adr-0007-safe-update-transaction.md`，依赖/构建/本地演练/未来另行授权发布见 `docs/t05-release-engineering.md`。

### 冻结构建与分类证据

最终普通构建 **T05-20261002-portable-05**，版本 **0.5.0-t05-portable**，源 **sha256:e8455e0aa53be5864c361a62795996e544c579faf15435d3fadfba31b56a1a1b**，272个源文件。环境 Windows11 10.0.26200 x64、普通用户、项目内 SDK10.0.401，固定锁依赖。以下为本构建重跑，不套用 portable02/04 的成绩。

| 实际命令/范围 | 结果与证据 |
|---|---|
| `./scripts/Build.ps1 -BuildId T05-20261002-portable-05 -BuildVersion 0.5.0-t05-portable` | locked restore + Release Rebuild，0警告/0错误，源前后相同；`artifacts/builds/T05-20261002-portable-05/build-result.json` |
| `./scripts/Test.ps1`（与实机串行） | 553/553 C#，22/22 SDK，6/6 mock样例，21/21合成Native身份客户端，7/7合成HTTP服务，12/12真实本地CLI；同构建各独立JSON，不算真实Logto账号 |
| `./scripts/Package-T05.ps1 -Stage Full` | publish、受管清单、独立自包含Bootstrap/Updater、资源/依赖/许可证检查后，串行执行以下实际门禁，再生成ZIP；`package-result.json` native_tests=passed |
| Package内 `Test-T04StoreSmoke.ps1 -ExecutablePath <最终EXE> ...` | 75/75 passed，20截图；最终目录字节一致的隔离副本，真实本地商店下载/安装/存档/升级等待/卸载及双应用后台回归。真实公开Release安装仍not_run |
| Package内 `Test-DesktopInteractionSmoke.ps1 -ExecutablePath <最终EXE> ...` | 120/120 passed，23截图；最终目录字节一致的隔离副本，拖排、取消、后台继续/结束、外观、存档和重启等 |
| Package内 `Test-DeliveredLauncher.ps1 -ExecutablePath <最终EXE> ...` | 18/18 passed，**最终路径原地**：稳定入口→同根业务EXE、实际程序集身份、次实例不同工作目录转发、原PID/HWND、正常退出、数据在原根；真实设置→系统更新显示0.5.0-t05-portable/portable-05/plus与“自动安装尚未配置：缺少生产可信公钥，不能安装未签名更新”。此门禁未点网络检查、未截图，不多报覆盖 |
| Package ZIP白名单逐项读回 | 851文件与冻结交付一致，无用户数据/凭据/私钥/误启用测试源；实际 .NET10.0.12 与 WindowsAppSDK 自包含、Fixed WebView2 154.0.4258.53 完整随包；Windows10/干净机器实测仍not_run |
| Update.Cli `prepare`，普通05同一冻结目录 | 848受管文件payload/清单与receipt一致；`artifacts/releases/T05-20261002-portable-05/unsigned-local-preparation/`，生产签名not_created；Release/Asset ID=1仅明确标注的本地占位，不可作为正式更新投放 |
| 最终实际DLL与Updater bundle审计/FromEmbedded探针 | `artifacts/reports/t05-portable05-distribution-audit.json`、`t05-portable05-production-trust-probe.json` passed；普通程序无测试root/feed，实际拒绝测试签名 UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED；独立注入测试公钥的正控制验证通过 |
| drill03→普通05源对照 | `artifacts/reports/t05-portable05-source-diff.json`：83个更新/协议/协调/存储身份等核心文件hash相同。7项差异为去掉2个测试公开编译输入、发行文档、About旧T02字样和3个测试driver；签名A/B测试与普通无生产root构建分别报告，不能宣称普通包完成公网升级 |

原生门禁根：`artifacts/builds/T05-20261002-portable-05/package-t05-7899a4d7cbb64e68ab35b39f3221f638/`。原地业务PID29156、HWND32311042；次入口PID26876/短暂转发客户端24644退出0，原窗口保持，最终业务和入口正常退出0，无遗留。本构建商店/桌面共43张截图，负责人实际查看3张（About、拖排后桌面、两个后台应用），记录在 `visual-review.json`；不宣称全部逐张审阅，派蒙设计审阅仍not_run。

portable04 Package Finalize 曾在商店 `resource_AutumnOS.pri` 门禁失败，未启动UI：旧driver硬编码旧业务名，实际资源已是 `AutumnOS.Client.pri`。修正driver按实际业务EXE选择PRI/deps/runtimeconfig，继续严格检查XBF与哈希，重新编译portable05并重跑以上全套；失败 `artifacts/builds/T05-20261002-portable-04/package-t05-bc28b61de1bc47148f76a3345af3f325/store/store-smoke.json` 保留。

### 真实升级、回退与中断恢复

drill03 A/B/Bad 是具有不同真实构建身份的发行程序，源 **sha256:c132879bf2393e3dcc13fa5ff1a4364c283a820fdb7194fe4cb42b924bdfc496**，测试构建清单 `artifacts/t05-drill/20261002-drill03/drill-builds.json`。A含实际更新协议，不用不支持更新的T04伪装。各试验运行的是交付EXE的独立副本，旧工作包及用户数据未替换。

主链报告 `native-retry-06/update-smoke.json` **72/72 passed**，真实签名A下载/验证B，前台和后台各35秒等待、输入保持、结束释放资源后提交、B原入口/快捷方式启动、848受管文件与实际程序集身份核对、6份原配置/存档/登记/权限文件及SDK实际读回；故意Bad健康失败回退A并隔离坏版本，20秒窗口内无循环。所有试验cleanup passed。

故障报告根同为 `artifacts/t05-drill/20261002-drill03/`，`fault-drill-summary.json` 汇总 **144/144 passed**（每场36项）；每场结束另外核对848旧程序文件和6份真实数据hash，并实际重开游戏读档、原快捷方式重开A：

| 报告子目录 | 实际注入与观察 |
|---|---|
| `fault-u09-applying-04` | 在journal=applying时终止精确持有的独立Updater进程，再由原入口持久恢复A。不是物理断电，不终止用户业务进程 |
| `fault-u09-health-01` | 在journal=health且新版childPid7748已经存在时终止精确Updater；新客户端自行结束，原入口恢复A与数据 |
| `fault-u06-legacy-v1-01` | 真实旧T04的隔离副本在applying期间启动；旧v1赢得交接空隙的业务锁，只出现一个业务实例。正常关闭本次自启空白欢迎窗口后更新安全回退A，两个根的数据不混用；不是B升级成功，也不是跨Session测试 |
| `fault-u10-busy-file-01` | 对实际App.xbf持Read/FileShare.Read锁，更新替换前aborted、UPDATE_FILES_BUSY并自动返回A；35.747秒持锁及解除后显式检查再20秒均保持暂停，无自动重启循环，合法B不被误记坏构建 |

故障早期失败报告applying01/02/03全部保留：01测试昵称超出样例24字符约束；02高频日志观察未共享Delete导致原子日志替换被占用，产品安全恢复但未完成计划的中断注入；03注入和恢复已成功、最后About即时文本绑定断言有竞态。artifact driver修为契约内昵称、按ReadWrite|Delete共享读取后再解析和有界等待About实际身份，最终applying04完整重跑。未放宽产品验签/健康/数据断言。驱动真实文件、输入和hash位于 `artifacts/reports/t05-fault-tools/drill03-preparation-07/` 及各报告，不计为发布程序功能。

### 最终产物与派蒙手动检查

- **直接入口**：`<WORKSPACE>\artifacts\packages\T05-20261002-portable-05\AutumnOS-0.5.0-t05-portable-win-x64-development\AutumnOS.exe`。SHA-256 `13129ece06ce89c5548bda7d6e2839e7b655ead22636c3836cabc84c8b82ee29`。必须保留同级完整目录，不能只拷贝EXE。
- **ZIP**：`<WORKSPACE>\artifacts\packages\T05-20261002-portable-05\AutumnOS-0.5.0-t05-portable-win-x64-development.zip`，475,417,170字节；SHA-256 `798790ca7288ccc2254b59bfe17b80c776709f3d98e5829303d7bee8db959a47`。ZIP不含原地烟测创建的数据；原目录测试数据按规则保留，没有清空用户数据。
- **新普通快捷方式**：工作根 `AutumnOS（T05 便携安全更新 T05-20261002-portable-05）.lnk`，COM字段和目标核对通过；该新lnk尚未单独启动，实际EXE启动已经过上述原地门禁。所有旧快捷方式保留。
- **普通同构建更新材料**：`artifacts/releases/T05-20261002-portable-05/unsigned-local-preparation/`；payload 410,373,626字节、SHA-256 `163352eceb4d4635bdc660085411b24c305309c4d4924a22b2697402be4d6485`，另有autumn.update.json、checksums.json及release-blockers.json；没有签名，不会被自动安装器接受。
- **仅本地手动演练入口**：`artifacts/t05-drill/20261002-drill03/manual-tools/prepared/Upgrade-20261002-100350-71f6254897604a71922d9009522d18bf/仅本地测试-Upgrade-AutumnOS.lnk`。该新目录干净A已逐字节复制、测试公钥验证B成功、唯一feed候选B=0.5.1，10/10准备检查通过。状态prepared_not_launched，新副本人工升级not_run；前述72/144为其他独立自动试验。演练说明 `artifacts/t05-drill/20261002-drill03/manual-tools/README.md`，测试元数据过期后应重新签发独立测试材料，不能关闭有效期/签名验证。

派蒙普通验证：正常关闭已开的旧版，打开新普通EXE；设置→关于核对portable-05，系统更新核对plus和未配置自动安装说明。主程序预览切换不应改变商店各游戏偏好。打开元素配对保存昵称、返回桌面、从“正在运行”继续核对原内容。普通版目前没有可用生产签名源，不能用它期待安装演练B；旧存档不会偷偷迁移到新目录。

派蒙演练：先关闭普通版，使用明确“仅本地测试”的新演练快捷方式，完成游客引导；打开游戏写入/保存测试昵称，在系统更新检查并下载B。游戏前台及返回桌面保留后台时应等待，未保存内容保留；正常结束真实游戏后回系统更新执行重启，确认B=0.5.1/BuildId后缀B及原存档。故障回退需按README用 `Prepare-ManualDrill.ps1 -Scenario Rollback`另建干净A，关闭此前所有客户端后再切测试feed；保留每次测试目录和报告，不对普通/旧使用包注入失败。

### 验收状态、阻塞和 T06 交接

`artifacts/builds/T05-20261002-portable-05/acceptance-coverage.json` 逐条记录U01–U14实际范围。R044/R045/R046/R047/R055、A044/A045/A046/A047/A055已追加本构建证据、保持in_progress；X01/X04/X05只补真正跑过的断言，不把真实GitHub应用安装、pm标签/公网代理攻击、完整多会话矩阵改通过。历史证据数组不清空。

生产固定源只读探测HTTP200/0Release，证据 `artifacts/reports/t05-production-release-probe.json`；**可信生产公钥、正式签名Release/资产身份、Authenticode blocked**。普通根实测无测试root，不能接纳源附带任意公钥，不请求派蒙私钥/密码/PAT。测试私钥已按精确路径删除，证据 `artifacts/reports/t05-foundation/test-key-cleanup.json`；源和ZIP无私钥。旧T04 EXE/ZIP/快捷方式原始hash/字段保留核验见 `artifacts/reports/t05-foundation/baseline-preservation.json`，不把该核验扩展到未逐个核查的全部历史文件。

**not_run / 暂缓**：安装版由用户暂缓；T03真实Logto账号、第二账号、长期刷新收尾暂缓；Windows10/干净机器、物理断电、下载/解包/所有日志写入和迁移子边界逐个断电、跨Session/多用户实机、Starting/Suspended/Closing实机竞态、完整物理输入/触屏/多屏DPI/无障碍矩阵未运行。协调和协议fixture覆盖另记，不以fixture代替实机。T01/T02/T04其余完整需求未取消。本轮有明确产物及已测便携检查点，**不是整个T05或整产品verified**。

未来T06仅交接、不执行：先由制作人另行授权发布；离线提供可信公开根并接入可审计构建，结合真实Release/asset身份重建并签署原始UTF-8清单，复核正式构建无测试源；补齐上述平台/账号/故障矩阵和签名，再按 `docs/t05-release-engineering.md` 的核对步骤决定是否发布。安装版待派蒙恢复范围后继续，不以云端CI/仓库授权阻断本地开发。

最终记录完成后的核对：`Test-Source.ps1 -ReportPath artifacts/builds/T05-20261002-portable-05/source-check.json` **55项静态passed**；`artifacts/reports/t05-audit-tools/Test-PlanAndPublicConfig.ps1 -ReportPath artifacts/reports/t05-spec-structure-equivalent-03.json` **680计划断言+27公开配置断言passed**，58需求/66验收/7任务完整。后者是原两个Python检查脚本的明确PowerShell等价执行，当前Python入口不可用，未声称运行Python、真实登录或产品验收。

`artifacts/reports/t05-portable05-final-evidence-audit.json` 独立重读851个交付文件及ZIP全部851项，名称/长度/SHA-256与冻结白名单匹配，ZIP最终hash仍为798790ca…959a47。原地实测产生的19个Data文件及运行锁未入ZIP，没有额外未知条目。`New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T05-20261002-portable-05/after-delivery` 确认272个源文件仍为e8455e0a…a1a1b；`after-delivery-verification.json` 核对入口字节未变、无遗留客户端/更新器进程。静态核验和包字节核验分别记录，不冒称新增产品测试。此轮本地便携产物、隔离演练入口和证据交付完成，完整阶段仍遵守上面的blocked/not_run/暂缓边界。

## 2026-10-02 · T06 首轮真实构建（实机主链待执行）

本轮有效提示词是附件 `G:/下载/AutumnOS_T06_Codex_Prompt.md`，根目录同名文件仍缺失；已按附件读取规范，未声称根目录文件存在。用户明确不制作安装包，原安装版验收继续保留为 `not_run_user_deferred`。完整 T03 原收尾继续暂缓，未执行 Git 或远端写入。

以 T05-20261002-portable-05 实际源字节和旧包为基线；新建 `artifacts/reports/t06-foundation/baseline.json` 核对源快照 `sha256:e8455e0aa53be5864c361a62795996e544c579faf15435d3fadfba31b56a1a1b`、旧 EXE 与 ZIP。旧产物与历史失败报告保留。

实际新增/修复：宿主本地脱敏诊断导出及九个边界测试；维护资格异步等待后复查UI空闲；共用创建/校验/打包/发布资料服务、自包含CLI和原生确认的独立预览/调试管道；同appId同版本不同内容的预览缓存按SHA隔离，并强制预览不回退旧游客数据接口；四个真实内部应用模板、独立后端样例、自包含开发交付；17个版本化实文页面、26方法/6事件说明、真实TypeScript/Ajv/OpenAPI检查；同目录商店安装→游戏存档→A/B主程序更新/回退及性能驱动。开发模式的离线/账号模拟未实现，不声称通过。

命令：`./scripts/Build-T05LocalDrill.ps1 -TaskId T06 -RunId 20261002-drill01 -PublicTrustFile <受限临时测试公钥> -PrivateKeyFile <受限临时测试私钥>`。三版本是 `T06-20261002-drill01-A` 0.5.1、B 0.5.2、Bad 0.5.3（编译注入核心初始化故障）；均从快照 `sha256:2caea9b3ddca5dea000c23c19c78e1108a7ff06277221904d89fd27350aaff3e` 构建，0警告/0错误，真实payload/清单/签名验证完成，1699项受管理文件。精确路径和哈希见 `artifacts/t06-drill/20261002-drill01/drill-builds.json` 与各 `artifacts/builds/<BuildId>/`。

A本轮自动报告：574/574 C#；22 SDK；313/313真实机器合同；6 DOM/SDK模拟；21独立身份客户端合成协议；7真实本机HTTP/合成JWT；12 CLI检查。分别归类，均不等于原生交互或真实Logto登录。此前 `compile-preflight` 572项和合同预检失败/修复报告保留，不能替代这些冻结构建成绩。

当前首轮原生跨模块演练尚未运行，便携ZIP仍在打包；后续结果追加，不提前填写T06-A通过。测试根仅供本地演练，私钥仅在受限临时目录，待全部签名工作结束清理；普通构建不配置测试信任根。正式公钥/Authenticode缺失，公开发布、派蒙审阅、干净Win10/离线/DPI输入法矩阵仍未通过。构建包含既有Setup工程不表示交付或执行了安装器。

## 2026-10-03 · T06 接续联调与实际缺陷修复（最终候选尚未冻结）

上一节末尾是当时状态，不代表当前仍未执行。保留三次失败：`artifacts/t06-drill/20261002-drill01/native-01` 的过早编辑器查找及窗口截图问题、`native-02` 的未触发桌面入口/坐标二次转换、`native-03` 的真实应用 Starting 后启动失败。截图工具修复坐标重置并保留归属/离屏检查；鼠标观察补充按压/释放坐标。跨模块驱动使用真实桌面 Button 的 UIA InvokePattern，仍经过产品原入口和手势消费检查，此链不冒充物理单击验收。

同一 A EXE（bb33bb4f…7209234）短目录对照 `native-04-short-root/integration-smoke.json` 已完整执行：同一数据根商店搜索、详情稳定/预览选择、下载校验安装、桌面入口、真实内部运行、资料拒绝/游客 AUTH_REQUIRED、SDK 存档、前后台各35秒更新等待、重复启动唤回原窗、真正结束后 A→B、核心健康确认、保留1699项受管文件和15份原数据、Bad健康失败恢复 B、坏版本隔离/35秒无循环、原快捷方式重开。维护期间每场两次重复入口均 busy、没有第二业务实例。该报告只绑定 drill01 冻结字节及较短测试目录，不能代替修复后深目录或最终普通构建结果。

真实缺陷：原 WebView profile 将账户和应用来源的两个64字符身份目录嵌套，深目录 profile 根237字符/Variations258字符，在本机 LongPathsEnabled=0 时无法完成初始化；同字节短目录已成功。产品改为 `AutumnOS_Data/Runtime/wv/<完整域隔离SHA-256>`，仍保留完整账户/应用/来源隔离，原存档与旧缓存保留，不修改系统长路径策略、不偷偷改数据根。独立3项绑定/隔离/输入拒绝测试通过，证据 `artifacts/reports/t06-native-profile-fix-01/`；新版深目录实测待重新构建。启动日志新增固定阶段、异常类型和数值 HRESULT，排除异常消息、绝对路径及内容；诊断导出同步白名单投影及恶意嵌套字段测试。

开发交付独立实测：`artifacts/reports/t06-developer-kit-clean-01/` 20项真实CLI与313项合同检查通过，四模板在独立新中文目录创建/校验/打包/生成及检查发布资料。`t06-developer-backend-exe-01/delivered-backend.json` 14项交付EXE检查通过，客户端合成身份21项、服务器真实本机HTTP负面检查；两者使用自包含运行时，真实Logto仍not_run。属于作者干净目录自测，不是外部新人验收。对应源README已修复隐藏工作区入口；最终新包须重新复现自身字节。

本机新DOTNET_CLI_HOME首次启动曾自动添加仅测试目录的用户PATH项；已精确删除该新增项并保留其他条目，未修改机器PATH，既有localhost证书未改动。证据 `artifacts/t06-drill/20261002-drill01/clean-backend-source-01-environment-repair.json`。Common/SDK/独立后端构建脚本已设置禁用开发证书生成和全局工具PATH修改的进程环境变量；最终交付清洁构建仍需核对前后PATH/证书元数据。

正在补齐原 `docs/06-developer-docs.md` 的预览会话专用账号、权限拒绝、离线模拟；不得影响真实Logto、凭据或正式数据。新增源快照内容导出工具、真实WebView负面驱动及维护中的健康child观察；工具静态编译不算产品验收。后续连续执行新签名A/B深目录、故障矩阵、最终普通EXE/ZIP、开发者/安全/性能回归，再写完整发布就绪报告。安装器用户暂缓、T03原收尾暂缓、Win10/干净机器/物理输入及派蒙审阅仍保留。无Git、上传或远端资源写入。

## 2026-10-03 · T06 深目录联调通过，安全回归发现新缺陷（候选尚未完成）

新签名演练 `20261003-drill02` 的 A/B/Bad 全为真实不同编译身份，源 `sha256:4b141a9a060f2950d9a1974f0281b5bc1e4b05fecde5d8ed064793a1a7f86d3b`。A 构建587 C#、22 SDK、358机器合同、6 DOM模拟、21合成独立身份客户端、7真实本机HTTP/合成JWT、12 CLI检查通过，零编译警告/错误。`native-01-deep-root/integration-smoke.json` 在默认深层中文目录完成102/102原生检查，cleanup passed、无残留：同一安装/数据根商店→下载校验安装→桌面→内部运行→权限拒绝/游客资料错误→SDK存档→前后台各35秒等待→重复入口唤回原窗→真正结束→A升级B并核心健康确认→Bad失败恢复B→原存档读回→坏版本隔离/35秒无循环→原快捷方式重开。此前237字符WebView profile故障修复后，此默认路径已真正通过；不再依赖缩短目录。

普通 `T06-20261003-portable-01` / `0.5.1-local.1` / `sha256:fdf57a1b9628ffd7be9c8b61799b97663e62ccfc314aa958fb84bc433379c4ff` 完成Release/Prepare和同组587/22/358/6/21/7/12检查；`artifacts/dv03/developer-smoke.json` 115/115原生开发者检查通过且无残留，包括四模板真实内部预览、模拟A/B资料与存档隔离、资源释放后切换、取消、拒绝/恢复、离线事件、返回真实游客、原业务数据/正式权限文件不变。它们是预览专用模拟，不是T03实际Logto账号收尾。原包尚未Finalize，不是最终合格候选。

最终普通包后端实际14/14、客户端合成自测21/21，`t06-delivered-backend-final-01`。干净套件首次公网固定npm恢复失败 `CERT_HAS_EXPIRED`/连接错误/Exit handler never called，报告 `t06-clean-kit-final-01` 保留，没有关闭TLS；`artifacts/ck03/中文 空格` 以此前官方内容缓存、原SHA512锁和npm ci offline在全新node_modules独立重建，20 CLI及358合同通过，不声称本轮公网恢复成功或干净OS离线首次运行。独立后端源码构建 `t06-backend-source-final-01` 通过，用户PATH、系统PATH及当前用户证书集合未变。

普通实际DLL生产根检查 `t06-production-trust-portable01-01.json`：无测试root/feed，固定生产仓库，FromEmbedded真实拒绝测试签名 UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED；外部显式测试公钥正向验证单独通过。普通真实Storage DLL的合成恶意日志导出12/12通过，ZIP仅support/events，私密标记不泄漏且拒绝覆盖；不是实际故障日志采集或制作人审阅。

真实安全缺陷记录 `artifacts/sec03/webview-security.json`：六种网络API已真实尝试并被CSP限制，原生新窗口拒绝已观察；外部导航虽Cancel=true，随后的NavigationCompleted错误地将已运行原页视为Crashed并关闭预览。失败图与运行日志保留，不把16项局部通过称安全全通过。正在修正真实NavigationId/初始文档生命周期，取消后续拒绝导航应保持原实例、未保存内容和维护租约；初始加载失败和真实进程崩溃仍应处理。监听器无HTTP头的TCP连接另做准确分类，不能把零HTTP请求宣称完整网络沙箱通过。

修复后将创建全新drill03和普通portable-02，重跑受影响构建、最终EXE、安全、开发者、升级与回退。不重命名旧包冒充新结果；所有上述旧成功/失败证据保留，安装器和T03原收尾继续暂缓，无Git/上传/远端写入。

## 2026-10-03 · T06 普通02完成原生联调，开发交付实测发现环境恢复缺陷（继续修复，不作为最终入口）

普通 `T06-20261003-portable-02` / `0.5.1-local.1` / `sha256:a2bffb4d540d230b23bc0a5a2d89d3df8fce4e9db62540ab0e4453822e1a2f7a` 已实际完成 Build、Prepare、Test 和 Package Finalize。591 C#、22 SDK、358 TypeScript/Ajv/JSON Schema/OpenAPI 合同、6 DOM SDK 模拟、21独立客户端合成 PKCE/JWT 协议、7实际本机HTTP负面/前置条件、12 CLI 全部通过。更正历史含混表述：7个服务器HTTP用例不是成功JWT经HTTP集成，也不是实际Logto登录；加密正向及错误材料验证另归C#或合成客户端用例。

对应自身交付字节的新原生报告：最终目录桌面122/122、受控真实商店75/75、原地EXE18/18，`package-t05-366c51f73fac41ccaf3b178ca6ac553b`；`artifacts/dv04`115/115、`artifacts/sec04`33/33并清理零残留。拒绝导航/下载后原实例、随机观察标记、DOM、未保存输入与维护阻塞保持；全部网络沙箱仍false。ZIP1703项逐项hash吻合，实际ZIP解压到`artifacts/zip02/中文 空格`原地启动18/18通过；正常目录中仅本轮创建的测试数据已精确移至报告目录保留，原用户数据未访问。普通ZIP600,944,015字节，SHA a2b73aa766d665adc2b3126b8bf75b0048f26f68c0ef0a5c7fb250523df578f6；EXE SHA726bdfdfd6eb049a960acb4478c484e4c1a940d561c99bbc7aefab78e751f1f8。

`20261003-drill03` 新签名A/B/Bad源 `sha256:34756a493f02947e281e95996afe4edfac88ddefb708b877f776180d9a143ad9`。深目录同数据根跨模块102/102，实际更新替换中断36、核心健康确认中断36、占用真实文件36、真实旧T04协议维护竞争34通过，四类合计142、清理零残留。报告`fault-matrix-01.json`及各native报告。它们只支持drill03实际字节；不冒充物理断电或所有事务边界。

普通02本机性能`artifacts/reports/t06-performance-final-02/performance.json`执行completed，事前目标missed：首次7.3526s、三次复启1.9663/1.8886/1.8978s；首个游戏5.5259s超过2s目标，前台/后台进程树工作集峰651.203/669.902MiB超过600MiB。后台静止12.278s窗口累计CPU报告0.0%，不能外推持续零占用；结束后290.273MiB。包含实际入口/UIA查询开销、普通缓存、20逻辑CPU、96DPI，不测冷启动/GPU/FPS/低配。

开发者新目录20 CLI+358合同通过，SDK ZIP852项读回一致；后端交付EXE14/14、客户端合成21/21、真实StorageDLL恶意日志导出12/12、普通生产根实际拒绝测试签名通过。第一次在线npm恢复的证书/连接失败仍保留，没有关闭TLS；本轮使用官方内容缓存、原SHA512锁在新node_modules执行npm ci offline，不声称公网TLS已恢复或干净OS离线首启。源码ZIP和审阅ZIP真实存在，各报告路径见ordinary02机器映射。

但交付后端源码脚本进一步严格检验发现真实缺陷：实际两项目restore/build成功且0警告0错误，用户/机器/进程PATH和52个当前用户证书集合未变；finally调用.NET SetEnvironmentVariable(null)却把四个原本未设置的进程选项变成空串，综合报告`artifacts/reports/t06-backend-sources-final-02/result.json`失败，精确诊断另存。没有把失败说成工具误报。已仅修`samples/backend-identity/Build-Sources.ps1`：null明确移除Env变量，其他值原样恢复；抽取实际finally源码，unset/empty/nonempty×正常/异常的6场景24断言通过。报告`t06-backend-environment-fix-01/result.json`，脚本SHA053de948447b9f8a759fc031b91a2aacf534ed0649182e166ad9e6d1b5ad31ac；前置AST工具失败也保留。

因此普通02及drill03封存为历史联调证据，包、快捷方式、存档、截图、失败报告均保留，不作为下一最终交付入口。新普通源`sha256:276cacd133f5ac04145a42643c777556cf68519ad52b4deb881568ec6964834f`相对普通02只改该wrapper，运行环境源码零变化；正在生成`T06-20261003-portable-03`/`0.5.1-local.2`与`20261003-drill04`真实A/B/Bad并重跑自身交付，不提前填写通过。安装器继续用户暂缓；正式密钥/签名/项目LICENSE、完整社区网络通道、真实Logto/T03收尾、Win10/干净OS/输入法/DPI/触屏及外部新人/派蒙审阅仍缺。无Git、上传、远端写入或公开发布。
## 2026-10-03 · 派蒙要求加快EXE交付并指定版本标识

最新要求：不做安装包，完成必要检查后立即交付EXE，版本填写`meta0.0.1-20261001`。正在真实编译`T06-20261003-meta001-01`：程序集ProductVersion、关于页及系统更新显示该确切字符串；规范更新比较/应用最低宿主兼容版本保留`0.5.1-local.3`并单独写SemanticVersion程序集元数据，不以含meta前缀的非SemVer破坏旧商店兼容范围或放松版本比较。Build命令明确`-BuildVersion 0.5.1-local.3 -ReleaseLabel meta0.0.1-20261001`，不是只改文件名冒充升级。

普通03及drill04真实重新Build/Prepare/Test完成，03后端43源环境恢复严格复测、非UI14/21/20/358、852项SDK ZIP通过，源/包字节不被改写。drill04首轮跨模块的前台/后台等待、原PID/HWND、输入保持已观察，后续owned editor UIA查找超时，失败截图仍显示原输入与前台真实应用；未完成整个链且最终报告未产出，保留隔离目录/失败截图。用户新输入时长工具会话已结束，不冒充drill04通过；四fault、03 PackageFinalize、dv05/sec05均未执行，不把drill03/普通02成绩套上。普通03转为保留的阶段产物，下一准确入口以新meta构建的实际最终报告为准。

本轮测试私钥精确删除操作被工具安全策略拒绝（仅返回blocked by policy），`t06-foundation/test-key-cleanup-final-01.json`记录blocked/retained，不绕过策略。仅测试私钥仍在受限系统临时目录，不在源码、客户端或任何交付ZIP；未索要/接收正式私钥。普通03更新材料CLI已产生1700项payload/清单，后续附件脚本因不存在的handoff Schema文件名失败，不宣称完整材料通过；meta最终材料会只引用实际存在的Schema/文档，不覆盖历史。
## 2026-10-03 · 最终本地EXE交付 meta0.0.1-20261001（T06仍在进行，不公开发布）

制作人：派蒙。遵循最新要求：不做安装包，完成必要检查后交付可直接运行的EXE。最终入口为 `<WORKSPACE>/artifacts/packages/T06-20261003-meta001-01/AutumnOS-0.5.1-local.3-win-x64-development/AutumnOS.exe`；新快捷方式 `<WORKSPACE>/AutumnOS（meta0.0.1-20261001）.lnk`。旧快捷方式未覆盖；新快捷方式只核对字段，真实最终EXE和ZIP解压后EXE均已直接运行。保留完整同目录依赖，不只搬EXE。

构建 `T06-20261003-meta001-01`，源 `sha256:c33abf1af0daf3d295475aa934465aa16d96f38b6b54dad47fbe668c2a562939`。程序集ProductVersion及关于/系统更新当前版本真实显示 `meta0.0.1-20261001`，实际编译日期2026-10-03。更新规范比较和应用MinHostVersion保留独立内部SemVer `0.5.1-local.3`；SemanticVersion程序集元数据明确存在，不放松Tag/SemVer，也不只改文件名。最终EXE SHA256 `1c22312ac86b926f4f3324ff85bc0f5d8dfa1227101863bfad829b69ab1cc4a0`。

实际执行：`./scripts/Build.ps1 -BuildId T06-20261003-meta001-01 -BuildVersion 0.5.1-local.3 -ReleaseLabel meta0.0.1-20261001`；随后 Package-T05 Prepare、Test、Finalize，具体参数/日志保留在该build目录；`Test-T06DeveloperSmoke.ps1`、`Test-T06WebViewSecurity.ps1`实际输入为最终交付目录，报告分别为`artifacts/dvm1`和`artifacts/secm1`；`Measure-T06Performance.ps1 -ExecutablePath '<WORKSPACE>/artifacts/zm01/中文 空格/AutumnOS.exe' -ReportDirectory '<WORKSPACE>/artifacts/reports/t06-performance-meta001-01'`。Windows Release 0警告0错误；591 C#、22 SDK、358 TS/Ajv/Schema/OpenAPI、6 DOM SDK模拟、21独立合成身份协议、7实际本机HTTP负面/前置条件、12 CLI全通过。7HTTP不代表成功JWT经HTTP/真实Logto；合成RSA/PKCE与后端C#验证独立分类。

当前字节的真实原生：`artifacts/builds/T06-20261003-meta001-01/package-t05-7153917369564fbf81d3e382209f770b`商店75/75、桌面122/122、最终路径单实例18/18；`artifacts/zm01/native-launcher-01/delivered-launcher.json`中文空格路径原地18/18、1703项ZIP逐项hash匹配；`artifacts/dvm1/developer-smoke.json`115/115及`artifacts/secm1/webview-security.json`33/33均passed/cleanup passed/零残留。受控真实HTTP商店搜索/详情/渠道/下载校验/安装注册/桌面图标/两应用内部运行/权限/真实SDK存档/后台/真正结束资源/应用版本替换/数据保持与卸载保留已实测；正常默认生产源恢复。开发preview的账号/权限/离线模拟与guest持久数据区分，不声称真实Logto。WebView真实6网络API/CSP与拒绝导航/下载后原输入、DOM、原实例、维护阻塞保持实测；complete network sandbox仍false，未放开社区任意包执行。

新目录开发实测见`artifacts/reports/t06-non-ui-evidence-meta001-01/summary.json`：交付后端真实EXE14/14、独立合成客户端21/21、全新中文目录CLI20/20及机器合同358/358；43交付源文件真实restore/build0警告0错误，6进程环境原unset/empty/nonempty严格恢复，PATH与52个证书未变。SDK ZIP852项实际回读通过。使用原官方SHA512锁和本机官方内容缓存，在全新node_modules执行npm ci offline；旧在线TLS失败保留，不声称公网TLS已恢复或干净OS离线首启。没有外部开发者参与，只称干净目录自测。

性能本版重新实测completed、target_result missed、cleanup passed/零残留：Win11 10.0.26200/i7-12700/20逻辑CPU/约16GiB/96DPI/普通缓存，包含UIA与启动器开销。首次5.4448157s，复启1.3521942/1.4804981/1.3885633s；游戏首启5.5843591s与继续2.3154148s超事前2s；前台632.51953125MiB/后台653.24609375MiB超600MiB。后台静止13.0675905s观察CPU0.0%，不能外推持续零占用。结束后290.03125MiB。不测冷启动/GPU/FPS/低配，不改目标来通过。

真实交付文件：
- 便携ZIP `artifacts/packages/T06-20261003-meta001-01/AutumnOS-0.5.1-local.3-win-x64-development.zip`，600,945,367B、1703项、SHA `dd413fd0a7706f34a435d6a0b297d6212440dbfb628e0ea10eaab5c1a29edf79`。
- 源码ZIP `artifacts/source-delivery/T06-20261003-meta001-01-source.zip`，348源文件+快照，SHA `447fa2f82e552d900a7f96fd798d7e101760cc5e07e1f7180cf8c548b22c05bb`。
- SDK/模板/五样例/文档ZIP `artifacts/developer-delivery/T06-20261003-meta001-01-developer.zip`，852项、125,393,997B、SHA `d34fa72547566889202992caa7630b044578d02d14e5f4591ac1b934a730f631`；SDK0.3.0/协议1、17版本文档/26方法6事件/9Schema/真实后端OpenAPI。
- 更新材料 `artifacts/update-materials/T06-20261003-meta001-01`：1700项payload/清单/3真实Schema/4文档及校验材料，status payload_verified_signature_blocked。没有正式公钥/签名，正常自动安装尚未配置；Release/asset/序列占位，不作为正式上传材料，不允许未签名安装。测试源与测试公钥未默认进入正常包。
- 派蒙审阅材料 `artifacts/review/T06-20261003-meta001-01.zip`，8张本轮真实测试窗口截图+10报告白名单脱敏摘要，SHA `d75988e646dbb9058970a6b67b0fcee23279c1f6da1fce889afc976e9eeb7c3a`；未包含原始敏感日志，制作人审阅not_run。
- 安装setup不存在，明确deferred_by_user。Authenticode未签名。随包.NET10.0.12、WindowsAppSDK1.8、FixedWebView2 154.0.4258.53，PRI/XBF/deps与依赖锁保留；不是干净OS验证结论。

最终目录测试自建Data在确认此前不存在数据后按精确hash移入本轮报告目录保留，`t06-foundation/meta001-test-data-preservation.json`通过。最终入口Data当前不存在，正常首启创建；不删除派蒙旧存档。原T04/T05包和旧shortcut哈希/字段保存，旧失败/源修改/测试隔离目录均保留。ZIP无Data/真实凭据/私钥或默认测试feed。

规范同步：requirements、acceptance-tests、release-status中增加当前局部证据，原58需求/66验收/218断言、全部历史evidence与条件保留；254项状态保留核对通过（仅文档状态检查，不计产品测试）。24个X01–X08原断言逐句对应partial/未覆盖范围，`artifacts/reports/t06-final-status-sync-01/meta001/cross-assertions.json`。G01–14当前局部in_progress，G06/G14 blocked；没有整项promote为passed。原T06保持in_progress。完整入口/手动步骤/门禁报告：`artifacts/reports/t06-final-readiness/LOCAL_HANDOFF_META001.md`。

明确未完成：本版完整A/B升级/独立更新器回退not_run。drill04第一次真实链失败/未完成，报告未产生、失败截图/目录保留；drill03升级链102和四故障142只能是旧字节历史，不能套给Meta。Starting/Suspended/Closing实际进程竞态、全部journal中断/迁移/磁盘满/断电矩阵未完成；C#夹具与原生演练分开。真实GitHub合规应用安装/正式更新资产与生产信任根/签名缺失；真实Logto Native与回调登记未证实，T03第二账号/浏览器退出/长期刷新继续暂缓。社区完整网络隔离、旧Dock/文件夹/多桌面/搜索/控制中心等原缺项、根LICENSE/部分第三方归属仍未完成。Win10/干净OS无SDK/离线首次运行/DPI/输入法/触屏/外部新人/制作人审阅均not_run。原03部分材料附件脚本失败、普通02环境恢复失败和性能超标均保留，不自动通过。

本轮测试私钥精确清理被工具安全策略拒绝，返回blocked by policy；仍在受限临时目录，未读内容、未绕过策略、未进入任何交付ZIP，记录`t06-foundation/test-key-cleanup-final-01.json`。没有索要正式密钥。

派蒙最少操作：正常退出现有程序→打开新快捷方式/EXE→设置关于核对meta0.0.1-20261001及新build→游客内置游戏保存、后台、继续原实例、正常结束。系统更新如实提示正式自动安装未配置。当前结论：可交付本机直接使用的联调EXE，待派蒙审阅；T06未全量完成，不可公开发布。后续补齐真实验收/上述阻塞，发布动作仍需另行明确授权。未执行Git、上传、创建远端资源或进入公开发布。

交付收尾核对：`artifacts/reports/t06-final-status-sync-01/meta001/final-entry.json`passed，最终EXE/ZIP字节与Package结果一致、实际ProductVersion精确为meta0.0.1-20261001、源快照在规范/进度同步后仍c33、新快捷方式存在、最终自建Data已保留移出。后续拟追加的meta实际DLL独立trust/support12及综合跨ZIP审计未执行（not_run），不引用普通02/03报告当作本轮成绩；现有Package/源码/SDK/ZIP实测按各自报告范围成立。未完成的辅助报告任务已停止，不在交付后异步改写本包或规范。

## 2026-10-03 · 派蒙最新单文件要求已实际交付（不做安装包）

派蒙新增明确要求：全部程序内容装进一个EXE，点击即可游玩，首次启动只能在同级生成一个Data文件夹，并剔除程序内不参与运行的文件。此指示替代原允许同目录DLL的交付形式；没有授权删除旧包、旧shortcut、源码或真实用户存档。

**最新准确入口**：`<WORKSPACE>/artifacts/single-file/T06-20261003-single001-03/AutumnOS.exe`。交付目录现在只有这个EXE，无DLL/ZIP/setup/README旁文件，体积488,573,230字节（465.94MiB），SHA256 `d6fe6e54b20ed76f2829573de877585c60cc8696b56fb36a79033f5e447f3381`。版本ProductVersion及真实关于页均`meta0.0.1-20261001`，制作人派蒙；内部规范兼容版本`0.5.1-local.4`。构建`T06-20261003-single001-03`，真实源`sha256:65737bd0c4b759354d832795cd98aafa5feabd711e8096918379220cb4aefb39`，最终复核未变。

实现真实单文件内容容器，不改成安装包或网页：`src/AutumnOS.SingleFile/Program.cs`是Windows自带.NET Framework4.x的准备入口，保留产品.NET10/WinUI。程序/游戏/固定WebView2/SDK模板文档/许可证/独立更新器全嵌入EXE，验证编译固定payload SHA、清单、逐文件内容与相对路径后，在同级唯一`AutumnOS_Data/System/Product-dde0a7d56a58a9b6b876`展开；.NET原生提取路径限定同一Data/System/CLR，只修改子进程环境。无需全局.NET10/WinAppSDK/WebView2安装，不改系统PATH/注册表。此为实际产品资源自包含，不等于已做干净OS验收；准备器依赖目标Windows自带Framework4.x，不能说没有任何OS组件依赖。

`PortableLayout`用固定标识/目录形状及拒绝reparse识别程序和数据边界，不相信任意环境变量/工作目录。InstallationRoot新增ProgramDirectory，Saves/Config/账号仍使用入口旁同一个Data。更新服务、handoff和独立事务核对真实DataRoot，不再错误指向展开目录的嵌套Data；程序受管文件在内部ProgramDirectory。保护布局标识与种子，原单实例/维护协议及独立更新器验签/恢复保留。发生不完整事务交给受保护内层Bootstrap恢复；不同已安装版本不重播旧种子。包完整性摘要不是发布签名，生产信任根仍未配置，不能自动安装未签名包。新布局完整A/B演练not_run，不套旧102/142成绩。

清理新交付内容：剔除19个PDB共584,844B；按SHA合并EXE内612份重复字节，1685个必需运行路径只存1073份唯一负载。展开后各程序路径仍完整，不用硬链接，不盲删DLL/PRI/XBF/deps/游戏资源/许可证，不进行未验证框架裁剪。旧包/源码/存档未删除。`cleanup.json`原aggregate字段null为工具统计限制，真实字节数由保留19项逐文件记录求和另存`artifacts/reports/single-file-delivery-03/delivery.json`，未伪改原字段为有值。

实际命令：`Build.ps1 -BuildId T06-20261003-single001-03 -BuildVersion 0.5.1-local.4 -ReleaseLabel meta0.0.1-20261001`；`Package-T05.ps1 -Stage Prepare`；`Test.ps1`；`Package-SingleFile.ps1 -BuildId T06-20261003-single001-03`；`Test-SingleFileSmoke.ps1 -BuildId T06-20261003-single001-03 -ReportDirectory artifacts/sf01`；同命令`-ReportDirectory artifacts/sf02-final-entry -InPlace`；artifact-only真实负面工具`artifacts/reports/single-file-negative-02/Test-Negative.ps1`。

本版Windows Release0warn/0error、595/595 C#（4新增数据布局/路径标识/保留原存档/更新Data边界）、22SDK、358TS/Ajv/JSONSchema/OpenAPI、6DOM模拟、21合成身份协议、7实际后端HTTP负面/前置条件、12CLI全通过。实际单文件两次41/41：新中文空格目录只复制一个EXE启动，以及最终交付目录原地执行；逐项核对1685展开文件hash、真实首启/欢迎/桌面/版本、内置游戏/权限覆盖层/真实SDK保存、后台仍阻止维护、再次双击只唤回同PID/HWND、继续保留未保存输入、真正结束资源、正常退出和重启原保存字节。各41/41清理passed/零残留。本版没有重跑旧普通包122/75/115/33，不能写成当前成绩。最终入口首次观测26.778604s（含解压/UIA），复启3.9371815s，普通缓存，不是冷启动或全性能目标达标；首启准备展示实际进度。

三项真实自建进程负面全部passed：负载字节篡改→PORTABLE_PAYLOAD_HASH_FAILED、不接受参数选择命令/目标→PORTABLE_ARGUMENT_REJECTED、Data真实junction在写入前拒绝→PORTABLE_REDIRECTED_PATH。都没有启动业务子进程，正确退出41，只检查自己创建的窗口/目录，目标sentinel无变化；不是只测试函数或模拟协议。

最终原地测试新建的Data已核对1855项全部SHA后移至`artifacts/sf02-final-entry/test-owned-AutumnOS_Data`保留，报告`data-preservation.json`；确认此前只有一个EXE、当前报告正常退出零残留且无reparse，不接触真实旧数据。交付目录恢复为仅一个EXE，派蒙第一次打开会创建干净Data和真实欢迎；不把测试存档/账号/测试源/私钥打进EXE。

失败记录保留：single001-01实际Build有2个平台标注警告；预检相对正斜线给旧csc导致路径错误，绝对Windows路径复测编译通过；single001-02 C595等通过，但ZIP写入后设LastWriteTime失败，未生成假EXE；修写入顺序后的03重新编译和自身测试通过。真实负面首轮已观察正确拒绝，却因工具把标题栏Close计入确认按钮而失败，原failure.json保留；第二版工具只点击已验证拥有的实际确定按钮，三个控制全部通过。没有把工具失败冒充产品通过。

发布/阶段：原58需求66验收218断言14门禁全部保留，增加当前局部证据；T06仍in_progress，无整项验收自动通过。新布局完整升级回退、真实Logto/T03第二账号/浏览器退出/长期刷新、完整社区网络隔离、原UI全部功能/迁移/磁盘满/断电、Win10/干净OS无SDK/离线首启/DPI/输入法/触屏、独立新人及派蒙审阅仍未完成/not_run。正式签名/根LICENSE/部分第三方归属和以前临时测试私钥清理策略blocked仍保留，新的EXE未包含密钥。旧源/包/shortcut与所有失败报告保留。本轮没有Git、远端写入、上传、公共Release或安装器。

派蒙最少操作：正常关闭旧程序→直接打开上述单EXE→等待真实首次准备→完成欢迎→点击内置元素配对。存档和设置都在同级唯一AutumnOS_Data；复制程序时只需EXE，搬迁已有存档时同时保留这个Data。交付说明/路径/证据/未完成项：`artifacts/reports/single-file-delivery-03/HANDOFF.md`。本轮“单EXE/唯一Data/无用调试内容清理”要求已实测完成；这是本地可用联调程序，不代表完整T06或公开发行获准。


## 2026-10-03 · 制作人授权 GitHub 上传（实际结果）

派蒙明确要求上传到 `paimeng5201314/autumnlab` 并在 Release 交付单文件 EXE；本请求替代此前对本次远端写入和 Release 操作的限制，不代表 T06 全验收通过，不授权其他仓库、云端 CI、认可名单或生产签名配置。

- 通过 GitHub 插件 API 实际上传源码、SDK、五类样例、机器合同、构建脚本、完整规范、公开进度摘要及交付说明，共 395 文件。354 个构建源文件保持源快照 `sha256:65737bd0c4b759354d832795cd98aafa5feabd711e8096918379220cb4aefb39` 对应的原字节。远端原 LICENSE（AGPL-3.0，Git blob `0ad25db4bd1d86c452db3f9602ccdbe172438f52`）保留；本地缺失 LICENSE 未冒充已存在于 EXE。
- 实际上传源提交 `2fc09076651d2366ea8ecc0d8b7ae4821cfea824`，父为既有 `45a26d4e43f01170f7af89fb57386c4bf2c3a6a7`；395 blob 哈希核对后非强制更新 main。最终公开状态提交 `bb4758a28f2010a64ca45c1fa4609a44ab42901e`；只改公开 README/进度以说明草稿未发布。
- 仓库：https://github.com/paimeng5201314/autumnlab 。完整本机历史进度、失败和测试数据保留，远端 AUTUMNOS_PROGRESS.md 为脱敏摘要。没有上传 AutumnOS_Data、用户凭据、私钥、真实存档、敏感日志、旧快捷方式、运行缓存或 artifacts；自动测试中的合成令牌字符串经核对为 fixture，不是真实凭据。未执行本地 Git 初始化/克隆/提交/推送，使用 API 创建 tree/commit/ref，没有云端 CI。
- 现有 Release 草稿 ID 402398412 已带 AutumnOS.exe，asset ID 607406432，状态 uploaded，大小 488573230，GitHub digest `sha256:d6fe6e54b20ed76f2829573de877585c60cc8696b56fb36a79033f5e447f3381` 与最新单文件产物完全相同。这是已有附件的本轮核对，不是假称本轮重新上传；无需重复发送 488 MB。
- Release 仍 draft=true、published_at=null、原 tag=meta、原标题=v0.0.1-meta0.0.1、prerelease=false。当前插件无 Release 创建/修改/发布或二进制上传工具；本机 gh 不存在，标准 Git Credential Manager 非交互检查未返回可用凭据，GH/GITHUB token 环境变量不存在。未读取浏览器 Cookie、应用密钥存储或索取令牌；未伪造公开下载成功。因此 Release 发布为 blocked_no_available_release_write_interface_or_local_auth，非工具审批拒绝。
- 派蒙最少操作：打开 GitHub Releases 的既有草稿，将新标签和标题设 `meta0.0.1-20261001`，target 选择 main，勾选预发布，复制公开 `release/meta0.0.1-20261001/README.md` 的说明后点击 Publish release。正确 EXE 已在草稿，不必再上传。此 Release 为手动下载预览交付，不向 plus/meta 自动更新源附送未经签名的更新清单。
- 实际本地记录：`artifacts/github-delivery-20261003/github-result.json`、`upload-inventory.json`、`RELEASE_NOTES.md`、`prepare-result.json`。新源 ZIP `artifacts/github-delivery-20261003/AutumnOS-T06-20261003-single001-03-source.zip`，354 源文件+快照，逐项读回通过，SHA-256 `6b3f71966c0a14c40522a7db5b6864c719856f06a40fb0460b1b97bd1e762d17`，只在本地，不冒充已附到 Release。

本轮没有改产品源码或 EXE，没有补跑产品功能测试；上传哈希和 API 读回属于交付完整性核对，不是 T06 产品测试。完整 T06、T03 原暂缓、新单文件完整 A/B、正式生产信任根、Authenticode、第三方许可补齐、环境矩阵和制作人候选审阅保持既有状态。安装包继续 deferred_by_user。

## 2026-10-05 · autumnOS 官网本地独立交付

派蒙明确要求在当前工作区制作完整 autumnOS 品牌官网，并禁止本轮上传/线上发布。已在 `website/` 独立实现 Vite + TypeScript + GSAP 纯静态站点；原生客户端源码、解决方案、.NET 依赖、公开 Logto 配置、存档和 T00–T06 状态未改动。未执行 Git、远端写入、云端任务或网站发布。

真实实现：10 段原创 SVG 手写签名与约 3.35 秒统一开场时间线，签名缩至导航；跳过/Escape/会话只播一次/页脚重播/独立超时兜底；原创秋色 SVG+Canvas 光幕；连续 sticky 桌面三章及手机普通纵向布局；可打开/聚焦/最小化/恢复/关闭/有界拖动和键盘移动的窗口；可完成与重置的配对游戏、演示壁纸；精细指针光标状态、磁吸、局部倾斜；设计章节、真实客户端截图、派蒙制作人区、结尾下载；完整/简化动态切换、无 JavaScript 回退。官网明确标注界面演示，不把概念交互或未完成客户端能力宣传为已发布功能。所有三个下载入口由 `src/content.ts` 唯一 DOWNLOAD_URL 指向派蒙指定论坛帖子。

实际本机验证：Node24.19.0，Windows，固定依赖与 package-lock.json；TypeScript 严格检查和 Vite 静态构建 passed。Playwright1.63 / Chromium Headless151.0.7922.34 完整28/28 passed，0 skipped/0 flaky，82.374秒；包含开场/重播/RAF冻结超时、存储拒绝、Canvas回退、窗口/配对游戏、鼠标/键盘/触摸模拟、滚动往返、390/768/1440/1920宽度、1366x768短屏、无JS和axe无障碍。生产静态产物7/7 passed，真实在 `/autumn-preview/` 子目录服务并检查资源、下载、窗口和无JS回退。全部断言对应浏览器实跑，不计为客户端产品验收。

环境问题如实保留：Node官方源恢复首次 CERT_HAS_EXPIRED；使用正常验证HTTPS的PowerShell官方源转发恢复成功，不关闭TLS，所有锁条目保留官方registry URL与完整性。普通Chromium启动失败、后续默认GPU进程崩溃；最终使用已有Headless Chromium与ANGLE SwiftShader软件渲染，Chromium sandbox保持开启。测试发现Dock遮挡、transform位置争用以及卡片生成后高度变化未重新约束，均实际修复并复验；失败记录在 `website/reports/history/`，未删除。物理移动设备、Firefox/Safari、硬件GPU性能/长期运行/实际上下文丢失、人工屏幕阅读器、论坛公网内容/安装包、线上部署均not_run。

本地源码和说明：`website/README.md`、`ASSETS.md`、`QA.md`。正式本轮静态文件8项、254943字节，位于 `website/dist/`；本地ZIP `website/reports/autumnos-website-df3a44a3f25d.zip`，128915字节，SHA-256 `ea27592e25cbcd802b464f71ef420bb8ceb379bfb1b8ba31ee0e7a53f0433d23`，逐文件ZIP读回哈希passed。最终实际源快照31文件 `sha256:df3a44a3f25d67a303779d7a4afe5648fad8aff5ec7fc8cffa7e08abe8c39c28`；清单在 `website/reports/source-snapshot.json`，最终生产检查与交付清单分别在 `production-checks.json`、`delivery.json`。完整28项报告在 `results.json`/`playwright/index.html`，桌面/手机截图在 `screenshots/`，实际开场录像在 `intro-recording/autumnos-intro.webm`。

实际命令：在website中执行项目已有npm CLI的 `run build`（tsc +实际源哈希+vite build）、`node node_modules/@playwright/test/cli.js test`、`node scripts/verify-dist.mjs`、`node scripts/capture-intro.mjs`，并打包/读回校验ZIP。最终构建只在文档完善后重新生成，运行时代码与通过完整验收的版本一致。常规使用 `npm ci`、`npm run dev`、`npm run build`；本机npm入口与浏览器环境的替代命令均已写入README。当前本地预览 `http://127.0.0.1:4173/`，绑定回环地址。

## 2026-10-17 · 本地收尾和单文件交付

制作人：派蒙。按最新要求完成本机打包，不清理、不归档现有文件、不上传GitHub。只读核对云端提交92c7889a25461b7c92e2981b1c20fa2b9b23181b后在本地整合；原文件备份在artifacts/local-refresh-20261017/base-files，源/规则/许可证/原始开工包与真实数据保留。

- 实现：应用尺寸原生准备窗口与真实进度；6排多语言反向滚动；hello上移及手写autumnOS；欢迎页主题稳定和独立可跳过账号页；游戏顶部操作栏移除，Esc返回并保留会话/输入，Ctrl+Shift+Q确认结束，F6桌面多任务。更新遵循Latest/预览设置、说明确认、手动目标选择与回滚固定，普通包缺生产信任时保持不可安装。
- 最终构建T06-20261017-refresh-06，Windows Release 0警告0错误；源快照ee6caca9f3a122d17f7930f923fecff10aa1f3bfd9a91ddd38662485dbc6d660，与05源码相同。打包后再次源快照复核一致。
- 实际测试：C#702/702、SDK22/22、机器合同358/358、样例6/6、独立身份客户端21/21、HTTP7/7、CLI12/12。最终EXE原生47/47（single-file-smoke-04），正常退出且无剩余进程；准备器专项24/24是同源补充证据，不冒充最终EXE重测。
- 新原生界面截图已检查：应用准备、语言滚动、手写品牌、主题选择、独立账号和游戏全视口。完整报告及每张截图在artifacts/local-refresh-20261017/single-file-smoke-04。
- 隔离测试签名目录更新native-update-04：80/80。手动选版/回滚/固定native-manual-pin-04：53/53，实际A→B→A→B全部提交/核心健康通过，原快捷方式重启、数据哈希、安全水位、稍后重启、成功才固定和恢复跟随均通过。手动新测试副本在G盘独立目录，不移动旧文件；报告仍在chem。
- 失败不覆盖：05空间不足未完成包、04准备超时及晚启动、最早游戏快捷键失败、误并发维护锁失败、manual01等待竞态/02磁盘不足/03驱动$Matches冲突均保留。最后追加深路径启动probe在40文件后遇262字符路径、退出41；Framework未启用长路径支持，这条失败保留。相同最终EXE换新短目录复核，26.82秒启动内层入口、31.55秒出现WinUI，3进程exit0，90秒预算未放宽；两条报告独立存档。
- 最终入口<WORKSPACE>\AutumnOS.exe，488730505字节，SHA256 73c4b22ca18a9a09ba8211b32f4a684eddd34c712296134091978f0ada1495a8。原EXE SHA256 d6fe6e54b20ed76f2829573de877585c60cc8696b56fb36a79033f5e447f3381，备份previous-root-AutumnOS.exe及root-entry-replaced-original-06.exe均在artifacts/local-refresh-20261017内；原子替换与回读记录root-entry-final.json。真实用户数据未修改。
- 交付完整证据：artifacts/local-refresh-20261017/delivery-final06/delivery.json和HANDOFF.md。聚合状态local_verified_with_open_acceptance；当前最终构建全部8组套件passed，包/根入口哈希匹配，warnings为空。完整T06保持in_progress，不宣称生产发布就绪。
- 仍需验证：真实Logto端到端登录（本次公开discovery实际HTTP502）；Native/回调线上登记尚未证实；生产信任和签名发布资产、生产GitHub更新及单文件入口签名升级；干净系统/Windows矩阵和人工辅助功能。安装器继续按此前用户指示暂缓。超长提取路径不在已通过范围。
- 自动审批曾拒绝删除TEMP测试私钥，原因仅blocked by policy；受当前用户ACL保护的测试文件仍在源码和EXE之外，未绕过限制。本轮也未执行被拒绝的旧暂存清理；最新“不需要清理”已遵守。
## 2026-10-17 · 登录页未唤起的复查

用户反馈首次配置点击登录后只显示“账号服务暂时不可用”。实际只读复查：账号首页、OIDC discovery、授权端点、公钥端点，在Windows默认系统代理与NoProxy直连两种路径下，共8次GET均返回Nginx HTTP502（TLS证书验证开启、未跟随重定向）。记录artifacts/login-repair-20261017-01/network-probe.json。

代码调用顺序确认：按钮触发SignInAsync后，先绑定回调端口，再获取校验discovery/JWKS，最后才调用系统浏览器。当前502在浏览器调用前被映射为AUTH_PROVIDER_UNAVAILABLE，和截图一致；没有证据表明这是系统浏览器唤起失败。公开发现URL格式与Logto官方文档一致。本轮未修改客户端、公开配置或线上身份服务；没有把失败转成模拟登录或绕过配置/签名/TLS校验。需要用户提供Logto部署方式，继续定位线上服务或Nginx上游，当前真实登录仍not_run。
## 2026-10-17 · ChemLeap 商店接入（进行中）

用户要求先整理成 AutumnOS 可下载的形态再测试。实际匿名 GET 指定仓库 paimeng5201314/chemleap：repositoryId=1414175562，main 只有 README.md（10字节）、Topics=[]、公开 Releases=[]；本地源码未发现游戏项目。有效只读报告 artifacts/chemleap-20261017/repository-inspection-02/repository.json。01报告存在 PowerShell 空数组枚举导致的空 Release 对象，原报告保留，02实际重新读取修正，不把空对象当发布。

本轮先完成独立可行的启动器增量：IStoreCatalog.ResolveRepositoryAsync 以固定官方 API 解析公开仓库链接/owner/name，商店“打开 GitHub 仓库”和搜索框 Enter 直达、缺标签/清单/版本/附件中文提示与详情刷新。发现保持 topic 查询，版本页重新核对实际 Topic，未加入商店的仓库不能安装；原社区运行门禁不变。新增62项测试，正在冻结源并准备 Windows Release 编译和真实窗口验收；当前测试均 not_run，不把源码等同于可运行EXE。

ChemLeap 本体下载/安装/游玩为 not_run：缺游戏资源与合规发布；已通过异步问题向用户确认是补启动器、从头做基础版还是提供本机源码位置，尚无答复。未复制其他小游戏改名冒充游戏，未创建假包或假下载。本地接入说明 docs/chemleap-integration.md。未执行 Git、克隆、远端写入、上传或发布，真实用户数据未改动，T01/T04/T06及原未完成验收状态保留。

用户已明确补充游戏需求，替代此前缺少游戏定位的假设：从头实现3D化学跳一跳。真实新增 samples/chemleap 的8个运行文件，H₂起始、15条平衡反应、8种可达角色物质、3到6个平台、每轮1或2个安全项，点击/数字键/蓄力、700ms跳跃、唯一落地计分、失败/重开、图鉴、后台暂停与SDK存档。WebGL是真实网格/法线/透视/深度，未用图片或外部网页替代。无网络依赖，仅saves权限，制作人派蒙。

本轮初版商店构建01为 Windows Release 0警告0错误；C#768、SDK22、机器合同358、样例6、独立客户端21、HTTP7与CLI12的实际报告均passed。外层调用曾因最后一个预期负面CLI留下LASTEXITCODE=2而返回2，报告未伪改；后续聚合以实际套件报告判断。用户澄清后02重新构建成功，源sha256:03fe821d6864ade92f792d329c869f039c844d6c98f5fed1aa3b7e6f293b1823，8组套件全部passed（另加游戏模型50）。最终游戏浏览器03为14/14，真实SwiftShader WebGL、20次UI跳跃到200分、错误结束、蓄力/键盘、暂停、持久保存/坏档保护、离线、390/768/1440与减少动画、真实WebGL丢失恢复、零页面异常和外部请求；浏览器源前后hash一致。模型测试包含48,000组平台与500连跳。

本地发布材料 release-01/publication/ChemLeap-0.1.0.autumn 已真实生成：28872B，SHA256 3a9f2f4e977e7adafec424df3223e19e3ddb5a7f14ce1c0005c88212552e8598；游戏源快照sha256:6a909c33279cdb6b4f309ad4d8929dbf5ca4ce003793f5b9e6b4a60747d00f08。三层清单与8项ZIP条目读回通过。源码完整目录ZIP真实解压后模型50项再次通过。尚未上传或公开发布，因此真实GitHub下载仍not_run。

原生第一次游戏驱动 native-game-01 在自动文件夹输入框定位处超时，游戏/SDK未执行；保留失败报告。脚本正常关闭先取消了modal，父进程未退出，随后仅核对自建PID/精确路径后再次正常CloseMainWindow退出，记录normal-close-after-picker.json，没有强杀或接管真实用户窗口。正在改成已交付CLI打包/preview协议+真实原生确认，未绕过开发模式。商店驱动native-repository-01观察取消恢复通过，随后过快重开ContentDialog未等待关闭动画导致定位超时，失败截图/报告保留；已加等待正在重跑。两项驱动修正将形成新源快照后交付，02包保留阶段证据，未替换根AutumnOS.exe/真实数据。

## 2026-10-17 · ChemLeap 基础可玩版与本地下载材料完成

制作人：派蒙。按用户补充玩法真实从头完成 samples/chemleap：WebGL三维跳跃、H₂初始物质、15条平衡反应、八种可达角色各至少两种有效试剂、3～6个平台且每轮1～2个有解项，坏项比例按分数上升，点击/数字键/蓄力、动画抵达唯一结算、反应效果、计分/结束/重开、图鉴、暂停/后台、WebGL恢复和真实SDK存档。没有网络依赖，仅saves权限；模型/渲染/输入分离。完整方案与用户要求的所有代码/表/规则见 docs/chemleap-game.md，源码和独立模型测试均可阅读运行。

最终构建 T06-20261017-chemleap-03，源 sha256:50cf737f7011fa97572c0c31fbd3e95b6ed39d68752b8c244aed382b7c6274b2；Windows Release0警告0错误。本构建重新执行C#768/768、SDK22/22、TS/Ajv/Schema/OpenAPI358/358、原样例6/6、独立合成客户端21/21、HTTP负面7/7和开发CLI12/12，另游戏模型50/50全部通过。tests-03外层正确按实际各套件报告聚合，退出0。源最后核对一致，79项源检查通过，规范58需求/66验收/14门禁原状态、断言和条件保留，只追加当前局部证据。

最终游戏实际字节：release-02/publication/ChemLeap-0.1.0.autumn，28,872B，SHA256 3a9f2f4e977e7adafec424df3223e19e3ddb5a7f14ce1c0005c88212552e8598。三层清单及8项ZIP资源逐项读回通过；游戏源 sha256:6a909c33279cdb6b4f309ad4d8929dbf5ca4ce003793f5b9e6b4a60747d00f08。正式源码ZIP ChemLeap-0.1.0-source-final.zip，11项hash读回通过，SHA256 f480115dc7d08b7536005e36410049ab4159cb2dc82ab178d988cf4c98e945aa；较早布局ZIP不删，最终ZIP保留真实目录供node模型测试独立执行。

最终游戏浏览器03真实14/14（当前源前后hash一致），模型包含48,000组有解/唯一平台、500连跳。软件WebGL、连续20次UI跳跃到200分及难度4/5/6平台、错误结束不加分、点击/数字/蓄力、暂停不提前结算、真实localStorage重载/坏档、离线、390/768/1440、减少动画、真实WEBGL_lose_context丢失/恢复、零脚本异常/外部请求。浏览器试玩与宿主SDK区分，没有拿mock存档冒充宿主。

最终宿主03的原生游戏 native-game-03 为16/16、cleanup passed/零记录残留：官方交付CLI真实校验/打包与用户启用开发模式/原生确认内部预览，真实H₂跳Cl₂成为HCl、得10分，实际SDK申请并允许存档，写盘核对appId/SourceKey/guest/slot/format/score；结束释放资源后同包新实例真实读回物质及分数。实测包SHA与release-02完全相同。第一GUI目录选择驱动失败保留；改用既有官方CLI协议，不注入配置、不改沙箱、不放行普通社区包。最终仓库直达 native-repository-03 为29/29、cleanup passed，真实GitHub公开状态/中文缺项/取消/非法链接/刷新/禁装与同repositoryId通过。它不代表游戏已公开发布或下载。

单文件最终03真实47/47：隔离中文空格目录首次准备、唯一Data、欢迎/主题/账号、版本、内置游戏权限与真实存档、后台、原实例回唤/保持输入、释放、重启读回/目录及字节完整性，cleanup passed。根入口 <WORKSPACE>/AutumnOS.exe 已原子替换为相同实测字节，488,734,744B，SHA256 71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1；原SHA73c4b22ca18a9a09ba8211b32f4a684eddd34c712296134091978f0ada1495a8在 artifacts/chemleap-20261017/previous-root-AutumnOS.exe 保留并hash核对。根真实Data未改动，未在真实用户Data上做原地测试。显示版本0.0.3-20261017-local、内部SemVer0.5.1-local.8。

当前本地试玩：根AutumnOS.exe→设置开启开发者模式→开发者工具“选择并检查.autumn”选择上述游戏包→内部预览→确认→开始跳跃。现在是明确的开发预览路径；普通社区启动门禁仍保留，不能把本轮成绩说成一般第三方执行已全部通过。

交付总报告 artifacts/chemleap-20261017/delivery.json 与 HANDOFF.md；截图和每项实际报告均在该目录。商店Topic/根展示清单/公开Release仍未写到GitHub，真实公开游戏下载not_run；本地展示/Release清单和真实.autumn已备齐。未执行Git、远端源码同步、远端写入、上传、公共发布、认可名单、系统PATH或凭据变更。真实Logto、生产签名与普通社区完整网络隔离、干净OS/Windows10/硬件与物理输入矩阵、完整T06仍未完成，原验收与用户暂缓安装器要求保留。本轮游戏基础可玩与本地分发材料完成，不宣称AutumnOS整体或公开发行完成。

## 2026-10-17 · ChemLeap 获授权上传 GitHub（实际完成）

派蒙明确要求“把这个化学跳一跳发到github我的仓库里去”，替代此前对本次 ChemLeap 上传的限制；范围仅 paimeng5201314/chemleap，不上传宿主和真实用户数据，不授权其他仓库、云端CI、生产配置或降低安全门禁。

通过 GitHub 插件 blob/tree/commit/ref API 上传21个白名单文件到main。实际提交c2d77bddc21e26fb90687af790fb27a489147703，父提交6bd5a311ebfaf47e30b3807f5f4744db19c5569b，tree182708acab3479aa8bd03c1c482945a4c1e74784；非强制更新且校验expected head，保留原README标题与初始提交历史。包括游戏8个运行文件、模型测试、中文方案/接入/测试文档、实际截图、根autumn.store.json、dist安装包/元数据/校验值、源清单与待用Release说明。没有上传AutumnOS.exe/Data、原始报告、用户路径、凭据、私钥或存档；没有初始化/克隆/提交本地Git，无云端CI。游戏运行源码及根EXE未变更。

上传前从独立仓库布局重跑模型50/50 passed、官方validate-publication passed、ZIP8项资源逐项hash匹配。游戏源仍sha256:6a909c33279cdb6b4f309ad4d8929dbf5ca4ce003793f5b9e6b4a60747d00f08。远端21个路径/大小/Git blob全部与本机清单匹配，main提交读回一致；实际HTTP200下载包、版本清单和商店清单，3项摘要均匹配并再跑官方三层校验passed。包仍28872B、SHA256 3a9f2f4e977e7adafec424df3223e19e3ddb5a7f14ce1c0005c88212552e8598，与原生16项预览实测相同。这是公开文件下载和交付完整性验证，不是普通商店安装/启动验收。

仓库 https://github.com/paimeng5201314/chemleap 。真实包下载 https://raw.githubusercontent.com/paimeng5201314/chemleap/c2d77bddc21e26fb90687af790fb27a489147703/dist/ChemLeap-0.1.0.autumn 。实际核对Topics=[]、Releases=[]。当前GitHub连接无Release创建/附件上传/Topics修改写接口；本机gh与token环境变量不存在，标准GCM非交互检查未返回可用凭据，未访问其他凭据存储。Release/Topics状态blocked_no_available_write_interface_or_local_auth，非审批拒绝。仅仓库dist直接下载已验证，不能当作自动商店已上架。远端release/v0.1.0/RELEASE_NOTES.md已备说明和操作清单；包0.1.0/channel stable对应v0.1.0且不标prerelease。

本地实际证据 artifacts/github-chemleap-20261017/github-result.json、upload-inventory.json、package-entries.json、remote-download/及HANDOFF.md；独立上传目录repository/仅为产物准备，没有.git。原生开发预览可玩路径和SDK16/16证据沿用同字节包；普通社区门禁不变，商店Release下载安装/普通启动仍not_run。T01/T04/T06保持in_progress，未重跑或冒充全产品验收。

## 2026-10-17 · ChemLeap 公开 Release 与商店检索已验证

用户明确要求“发布出去让它变成可以在autumn商店里检索到的样子”，并完成本机GitHub设备授权，授权范围限paimeng5201314/chemleap的公开Release/附件和商店Topics。替代此前本次发布限制，不授权其他仓库、云端CI、生产签名配置或关闭社区门禁。

已实际发布v0.1.0，Release ID415671030，draft=false、prerelease=false、published_at=2026-10-17T08:13:05Z（北京时间16:13:05），目标游戏提交c2d77bddc21e26fb90687af790fb27a489147703。先创建草稿，上传真实ChemLeap-0.1.0.autumn、autumn.release.json、SHA256SUMS，逐项核对GitHub asset.size/state/digest后公开，再保留已有Topics并添加autumnos-app/autumn-app-sq。公开网址 https://github.com/paimeng5201314/chemleap/releases/tag/v0.1.0 。包asset ID649938719，28872字节，SHA256 3a9f2f4e977e7adafec424df3223e19e3ddb5a7f14ce1c0005c88212552e8598；元数据asset ID649938788，校验文件asset ID649938812。

实际匿名下载三个Release附件均HTTP200、大小/hash与本地材料一致；下载后连同远端根store再跑官方validate-publication通过。CLI输出中的publishing=not_performed仅表示校验命令本身不发布，liveGitHubRegistration=not_verified是该命令固定职责边界；不修改该原报告，本轮真实发布与注册另由公开API和生产商店适配器报告证明。

实际匿名Topic查询及带ChemLeap文本查询均total_count=1、incomplete_results=false，目标ID1414175562正确。最终已编译AutumnOS真实生产适配器分别执行--probe-store、--probe-store-repository paimeng5201314/chemleap，两项均passed；发现total_count=1/loaded_count=1，分类Sq，详情store_present=true、installable_release_count=1，版本0.1.0、release_id415671030、can_install=true、warnings=[]。这是本轮“商店可检索/版本符合安装条件”已实跑验证。并未执行完整商店UI安装和普通社区启动，普通安全门禁仍保留，不能把元数据资格当作安装/游玩通过。

公开仓库说明已更新：main提交c94fc4f6d86ece2e5c5162c15ad7f8376fcde443，仅README/接入/TESTING/Release说明4文件；21项远端树逐个核对，其他17个blob包含8个运行文件保持原值，已发布标签未移动。游戏源快照仍sha256:6a909c33279cdb6b4f309ad4d8929dbf5ca4ce003793f5b9e6b4a60747d00f08，原生16项内部预览对应同一字节包。根AutumnOS.exe、用户Data及游戏运行源码本轮未变更，无需为纯发布/说明更新重建产品。

凭据通过标准Git Credential Manager官方设备授权后仅在进程内使用，不进入源码、日志或命令行；没有读取浏览器Cookie/其他凭据存储。首次普通管道未显示授权码，用户网页授权未形成可用本机凭据；两次非交互预检失败GCM_CREDENTIAL_UNAVAILABLE、remote_write_attempted=false，报告保留。仅停止本轮精确PID/路径的未完成登录助手，改用真实终端后用户设备授权成功，发布脚本真实执行完成。没有上传用户数据/真实存档/宿主EXE/私钥或原始机器日志，没有初始化或克隆本地Git、云端CI或其他仓库写入。

完整交付 artifacts/chemleap-store-publication-20261017/delivery.json 与HANDOFF.md；实际发布run-20261017T081250219Z-d2688b21/publication-result.json，真实生产适配器final-store-repository.json/final-store-discovery.json，公开附件public-release-download/，检索topic-search.json/text-search.json。制作人派蒙。用户可在兼容AutumnOS商店刷新并搜ChemLeap；游玩目前仍采用已实测的开发者内部预览路径。T01/T04/T06及其他原未完成验收不提升。

## 2026-10-17 · AutumnOS 公测宣传片本地交付

用户要求流畅美观，含程序原图、真实视频和使用流程；确认温暖电影感、深色与秋橙。实际完成48秒1920×1080/60fps宣传片，含品牌手写动画、真实首启欢迎、桌面原图、打开元素配对/翻牌/保存/Esc返回/F6多任务/继续原会话的真实操作、主题原图和公测邀请；配本次原创100BPM器乐与转场音，无旁白。

最终文件 `artifacts/promo-20261017/AutumnOS-Public-Beta-1080p60.mp4`，12,953,463字节，SHA256 `26ea498b5bf3406e153d10a95f89d46e87d45150cd66df89953781af429704f0`；H.264、48.000秒、2880帧、AAC48kHz立体声，完整解码passed，最终音频峰值-3.1dBFS。真实渲染2880/2880完成，耗时3分32秒；不是只交付HTML。最终工程lint/runtime/layout/motion/contrast全部passed、零错误零警告，60项文字对比通过；实际导出9帧已目视核对，报告`review/FINAL_VISUAL_REVIEW.md`。代理关键帧复核不代替制作人审片；全片人工试听/派蒙最终审阅保持not_run。

素材在`capture/`，源版本冻结为T06-20261017-refresh-06，EXE SHA73c4b22ca18a9a09ba8211b32f4a684eddd34c712296134091978f0ada1495a8、源ee6caca9f3a122d17f7930f923fecff10aa1f3bfd9a91ddd38662485dbc6d660；不拿官网网页演示冒充客户端。操作源`workflow60-final.mp4`为17.97秒真实60fps客户区录制，213次无遮挡检查与完整解码通过，压为14秒并标剪辑加速；欢迎原PrintWindow采样13.68fps并保留时间戳，10秒压为8秒，不虚称原录制60fps。源帧、来源哈希与失败take均保留。先遇其他任务单实例，用户确认退出后才采集独立副本；本任务只写自建宣传数据，真实用户数据、当前根EXE及源码未修改，自建客户端进程正常退出零残留。

可编辑工程`artifacts/promo-20261017/film/`；脚本`author-film.mjs`、`prepare-media.mjs`、`Run-Video.ps1`、`visual-qa.mjs`、`verify-delivery.mjs`；原创音轨/生成器在`audio/`。固定HyperFrames0.8.167、GSAP3.15.0与package-lock，FFmpeg9.0.2上游hash通过，Node系统CA验证正常恢复官方依赖，未关闭TLS或改系统PATH。源清单`source-manifest.json`，实际字节ID `sha256:306d9dc839476c3a752e7df0cc58bfa3f7195cc5360814b61457866417086150`；排除真实用户数据、运行缓存与生成视频。真实最终报告`delivery.json`、`check-final.json`，本地Studio `http://127.0.0.1:3017/#project/film` 已启动并status核对运行。

本轮没有Git操作、远端写入、上传或公开发布；宣传片不把Logto真实登录、生产商店安装、正式签名更新或完整T06宣传为通过，原产品验收状态未提升。论坛入口沿用既有官网配置，本轮公网内容未核验。制作人统一为派蒙。

## 2026-10-17 · 普通应用启动隔离实现与验证（进行中）

按用户要求补齐隔离后允许普通启动。正在实现通用来源规则与每实例强制隔离自检，不设置ChemLeap认可名单、不开放网页网络能力。固定WebView2 154使用浏览器原生Connection-Allowlist、X-DNS-Prefetch-Control和严格CSP；所有成功及拒绝资源响应均带策略。先仅加载宿主随机挑战页面，经原生ExecuteScript、精确origin/nonce/instance绑定及严格报告校验后，才服务包资源/接通SDK。证明只属于当前实例，关闭或崩溃撤销；启动超时、环境覆盖、缺固定运行时均失败关闭。SDK0.3.0协议1保持，networkSandboxVerified由常量false扩展为实例实际boolean。普通github/local/bundled应用采用同一流程；显式测试来源仍需开发预览条件。

独立真实WebView2探针直接编译当前生产RuntimeIsolationPolicy，固定运行时三轮证明3/3成功，A无header证据拒绝、B有header通过。最终network-direct-final-bf58e3e82c3748289d7d36a273b1a3ad实跑29通道A/B：包含27夹具及2个随机DNS/preconnect，旧策略观察额外4TCP/43UDP且两目标各4次解析，完整策略额外TCP/UDP及目标解析均0；两组真实观察者TCP/UDP正控通过，零ProcessFailed，直接P2P候选亦被原生失败处理。报告在artifacts/isolation-probe-20261017。它是独立引擎证据，不冒充主程序UI验证。最初代理条件不满足DNS正控及iframe导致renderer exit3等失败均保留；iframe异常不宣称稳定成功，主宿主需验证关闭/释放。

开发中Windows Release初编0警告0错误（非最终冻结候选）。已新增策略/会话单测、原生恶意包驱动和公开商店普通启动驱动；当前主宿主UI负面、商店下载安装/跳跃/存档读回及最终EXE验证均not_run，后续按同源快照真实运行并追加结果。根AutumnOS.exe与真实AutumnOS_Data尚未修改；没有远端写入或产品整体验收提升。

正式冻结构建01：T06-20261017-isolation-01，内部0.5.2-local.1、显示0.0.4-20261017-local，源sha256:3300c9a920f4bdaa493046ea09cec4fd5fad61520f4fab0e7d3666b046ff5114，Windows Release0警告0错误；源码81项通过。完整普通目录与真实单文件已打包准备（不等于验收完成），单文件488745685B、SHA256 da3bc796e8345f3cb65e86a934294d57892707ce6ca6cd56dc69324365c70bf2。根入口尚未替换。

正式C#首轮回归运行中，更新恢复出现UPDATE_COORDINATION_BUSY及后续恢复断言失败。实际同时发现另一任务的<ARCHIVED_WORKSPACE>/qa/中文 空格客户端正在运行。只读源码确认LauncherCoordinator范围仅固定产品+用户SID+WindowsSession，不含目录；Shell持业务mutex，UpdateTransaction.Recover先等同一业务mutex15秒，故临时目录不能隔离该前置条件。已异步请求用户待那边测试正常退出，不关闭/接管其他进程。保留首轮失败报告，待无其他客户端时重跑全部当前构建测试；没有删断言、缩短验收或把环境失败伪改通过。主UI测试暂未开始。

用户确认其他测试正常退出后，检测无其他产品进程。01全套828/833失败记录完整保留；新重跑前源守卫发现共享任务更新Package-SingleFile.ps1、Package-T05.ps1、MainWindow.xaml三个文案文件，保留最新内容并重建02。构建T06-20261017-isolation-02源sha256:a56b18d5e6b95803df57e43772ba1eca10c22a5e7bc1a8c38d075d449b4bb8dc，Release0警告0错误；全部8组真实通过：C#833/833、SDK22、合同360、样例6、游戏模型50、独立客户端21、HTTP7、CLI12，81项源码检查通过。原失败5项及泛Reject可能误过的负例均完整重跑，不只重跑失败项。

02主宿主负面首次实际形成isolation_verified、随后包navigation_completed、SDK报告true，资源已结束释放。但驱动在输入读回处失败9/10；未冒充完整负面通过，cleanup passed。修正驱动名称循环变量覆盖、UIA异步读回/前台检查及aboutblank终态/崩溃事件读取，产品策略源未改。普通单文件商店02真实只从EXE首次准备、逐项核对1687文件、默认开发者模式Off、公开GitHub详情/真实下载/核对28872B及hash/正式github来源安装通过，19个已跑断言通过；到实际桌面点击时被前台其他窗口遮挡，输入护栏明确拒绝，normal_launch/native_gameplay仍not_run。两次截图亦受同一前台遮挡，未造截图。失败与cleanup passed报告在artifacts/runtime-isolation-20261017/native-02和store-single-02。

已请求用户保持解锁、最小化前台Codex、暂时不操作鼠标键盘；不点击/最小化其他窗口或删输入护栏。等待真实桌面就绪后继续串行主UI验证，再冻结最终新源与同字节EXE交付。02单文件488745925B、SHA25641d4f96eb1d08c788eab5d505b7c85798b0aef8f94deee55bd0aebd4a01cc1cc仅候选，根入口和真实Data仍未改。

## 2026-10-17 · 隔离实现候选03完成，桌面验收按用户选择待续

用户选择“暂时不便进行桌面操作”。此后停止全部产品界面启动、鼠标键盘与截图操作，只完成非UI构建、自动测试、打包和报告；不自行关闭/最小化前台Codex、不绕过输入护栏。未将剩余游玩验收冒充通过。

最终本地候选 T06-20261017-isolation-03，内部0.5.2-local.1、显示0.0.4-20261017-local，源sha256:39cd2031ddaf5bedfc0500f8afe310800d1aa7480ef936fd7d8a2b667ea78bc2；Windows Release0警告0错误。全部8组在本构建实际重新通过：C#833/833、SDK22、机器合同360、样例6、游戏模型50、独立客户端21、HTTP7、开发CLI12；源码81项通过。未复用02成绩充作03。最终再次按实际源字节生成快照与构建一致，策略源hash6dda4ad800513b24b2bc630d9d71396dd304233cc9a9821ce5bc805a5accbc90仍与已完成独立原生3轮挑战/29通道A/B一致。

真实候选EXE：artifacts/single-file/T06-20261017-isolation-03/AutumnOS.exe，488745749B，SHA25632555c0aff77c900c50dccc3080013f2e34b9c1dce36e53fa7c9cea2b80a921a；payload SHA25637d95c0497534eadb65b167934d5512d7d8250ad9075aa0da9c77dab09528895，1687逻辑文件/1075去重blob逐项读回，许可证/完整运行时保留，未安装系统组件。普通完整目录也实际准备完毕，autumn.install.json及逐项哈希保留；没有执行Full/Finalize伪造原生验收或生成伪发行ZIP/Setup。

本次变更包括RuntimeIsolationPolicy、WebAppHost强制自检/失败关闭、RuntimeSession证明及撤销、普通来源统一启动、SDK boolean合同/360检查、支持包固定事件白名单、65个新增策略/会话用例、网络夹具/观察器、独立探针和两个主UI驱动、现版与历史文档澄清。驱动SetValue读回/Name覆盖/aboutblank初态误判与崩溃读取已修入03。主宿主所有网络负面/iframe真实失败关闭及03最终单文件首启回归、ChemLeap普通跳跃/存档读回均not_run_user_desktop_unavailable；02公开下载安装与宿主自检的局部通过证据和整体failed状态均保留，不泛化为完整成功。

最终报告artifacts/runtime-isolation-20261017/delivery.json、HANDOFF.md与可重复核对脚本Finalize-Local.ps1。保留根AutumnOS.exe原有已验收入口，不替换为尚未完成GUI验收的03候选；真实AutumnOS_Data未改。没有Git操作、远端写入、上传或候选公共发布；T01/T04/T06及真实Logto、跨账号/干净OS/Windows10/硬件物理输入等原未完成矩阵不提升。待桌面允许后按HANDOFF串行完成剩余UI验收，核对同一候选字节后再备份替换根入口。

## 2026-10-17 · ChemLeap 0.1.1 获授权上传与公开发布完成

用户要求“上传GitHub版本0.1.1”，并明确确认目标为ChemLeap游戏→paimeng5201314/chemleap，不是AutumnOS启动器。该授权仅替代本次游戏发布限制，不上传宿主、本机数据或原始报告；用户暂时不便桌面操作的选择保留，未再启动/操作界面测试。

实际更新samples/chemleap/manifest.json与index.html页脚为0.1.1，Package-ChemLeap.ps1改为从真实manifest生成版本包名，重新真实打包，不是将旧包改名。其余6个运行文件/固定SDK0.3.0、反应及玩法逻辑和存档格式1保持。新源sha256:0ec4ef9a8731bf15d26bb23e8d17d25107157c3eb5fa7f8f48b6153f3e060fb3；包ChemLeap-0.1.1.autumn 28871B，SHA256 e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2。独立仓库模型50/50、官方CLI三层校验、8项ZIP资源逐项hash读回及81项源码检查通过。旧0.1.0原生16/浏览器14仅保留为旧字节证据，未转计新版本UI；历史截图标明0.1.0，不擅加独立许可证。

使用受限GitHub REST和已获授权的标准GCM内存认证上传，不初始化/提交本地Git、不克隆、不用云端CI。main实际提交3ba9f79b05f57a955d9225fe0f1f8fde4cf3f8a4，父c94fc4f6d86ece2e5c5162c15ad7f8376fcde443，tree c8416b6c8cfa8abb2253d24efa604df0bb33f009；精确head核对、非强制更新及远端树回读通过。只准备20项白名单游戏文件，11路径更新、9项原blob一致，.gitignore/旧dist包/release/v0.1.0说明另外3项保留，未覆盖其他仓库文件。两个游戏版本标记、包、源清单/兼容说明与发布元数据一致。

真实发布v0.1.1，Release ID415748507，draft=false/prerelease=false，公开时间2026-10-17T10:48:19Z（北京时间18:48:19），匿名latest读回为v0.1.1。先草稿、三附件真实上传、逐项GitHub digest/大小/状态核对后公开；tag对应上述实际提交，没有移动0.1.0标签或重写其Release，前版fingerprint仍一致。公开网址 https://github.com/paimeng5201314/chemleap/releases/tag/v0.1.1 。包asset650220127、元数据650220186、SHA256SUMS650220278；三个公开附件均匿名HTTP200下载、摘要大小匹配，再跑三层校验passed。CLI报告publishing=not_performed只表示校验命令自身职责，实际公开状态另由发布及匿名下载记录证明。

生产适配器真实--probe-store与--probe-store-repository均passed，发现唯一目标仓库，读取两个版本、0.1.1 can_install=true。观察到仓库Topics已被用户/其他任务变更为autumnos-app/autumn-app-gf，本次完全只读保留，不改回SQ、不加认可名单。现D工作区旧分类逻辑只识pm/sq，而根展示仍原SQ，因此实际category=Unclassified、warning=STORE_CATEGORY_DIFFERS_FROM_TOPICS；未伪造warnings=[]，没有阻止版本安装资格。此分类兼容差异已写入报告，未擅自对抗其他任务更改。

发布说明明确0.1.1是游戏版本标记及兼容文档更新。最低内部宿主0.5.1-local.8保留用于旧开发预览的协议兼容，不代表旧宿主普通启动已修复。原生隔离在独立0.5.2-local.1本地候选，未随本次游戏发布；0.1.1普通游玩、SDK保存/结束/新实例读回与原生升级矩阵仍not_run_user_unavailable。T01/T04/T06及真实身份/平台矩阵不提升。根AutumnOS.exe仍SHA71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1，真实Data未动。游戏/脚本/文档修改改变当前工作树，恢复宿主最终验收时须重新Build/Test/Package，不能用旧03候选冒充新源字节。

完整证据artifacts/chemleap-011-publication-20261017/delivery.json与HANDOFF.md；实际发布run-20261017T104756001Z对应目录下publication-result.json（以delivery记录完整路径为准），source-upload-result.json、public-download/、production-store-*.json、独立模型日志与校验清单均保留。准备过程的参数导入/受限路径数组/自动变量导致差异数检查失败保留；前两无远端写入，第三仅创建合法内容blob后被计数守卫停止、未更新分支，修正后仅11预期变更正式提交。敏感凭据只在标准GCM及HTTP进程内，不进入文件/源码/日志/命令行；没有上传原始本机报告、用户数据或宿主。

## 2026-10-17 · plus0.0.4-20261017 稳定版发布准备

用户明确授权将 autumnOS 源码与 EXE 上传到 paimeng5201314/autumnlab 并发布最新稳定版，最终显示版本/Release/tag 统一 plus0.0.4-20261017。该授权只针对本次发行，其他工作继续遵守本地规则。未修改或覆盖正在开发的第三方运行时隔离增量；发行源码从 ChemLeap03 已验证的393文件恢复后独立构建，实际发布输入保存在 <ARCHIVED_WORKSPACE>/project。

最终构建 T06-20261017-stable-03，源 sha256:18dab870b97cc5785213a4f666205348fb33d4c2253263d988e8170650461e45，Windows Release 0警告0错误；本次8套测试1244/1244（C#768、SDK22、机器契约358、示例6、ChemLeap模型50、合成身份21、HTTP负例7、CLI12），静态源检查79/79。最终单文件 EXE 488,734,276B，SHA256 94ce904433866e4d54d9c5ac3f2b0afb9de1008003391500cb8417850d914b37。ProductVersion完整plus0.0.4-20261017；Windows FileVersion按数值字段要求为0.0.4.0，内部兼容SemVer0.5.1，不拿自定义展示名代替协议比较。

原生最终 <ARCHIVED_WORKSPACE>/qa3b 实跑49/49、cleanup passed、remaining=[]；精确关于/更新版本、多语言欢迎/手写、独立主题/账号、真实游客存档、游戏Esc/结束确认、原实例召回、重启/存档保留均通过。重复启动日志实际21572→30708→19712，forwarded/code0/Foreground，保留主PID32408/HWND853752。首次27.6247s、重启3.8904s为本次普通缓存观察，不作为干净系统指标。

较早stable02的 <ARCHIVED_WORKSPACE>/qa 在重复启动90s门槛失败，未拿到错误对话框正文；同六份启动/锁源码与ChemLeap03一致，事后全1686项hash一致，根因未确定，不声称修复。stable03重测保持原90s门槛未复现。第一次qa3是私有诊断wrapper作用域错误，cleanup passed，所有失败报告保留；wrapper改为与正式脚本相同作用域后qa3b完整通过，未删减/放宽任何断言或超时，未改产品启动逻辑。

公开Logto discovery18:38(UTC+8)通过，但真实登录往返仍not_run；退出回调登记、正式更新信任根/签名、生产升级及单文件外层升级回退、普通社区运行隔离、安装器和完整系统矩阵边界继续保留。不能以稳定通道覆盖未完成T06。

源码暂存仓库仅在 artifacts/github-delivery-20261017/repository，根工作区未初始化Git。393/393实际构建输入hash一致，公开445文件清单覆盖源码、完整规范、原远端历史/许可与本次发行证据；LICENSE原样AGPLv3，未包含真实Data、SDK/缓存/生成物、凭据或测试私钥。最终上传结果稍后按远端读回追加。

18:52根AutumnOS.exe被重新正常启动；发布准备发现实际PID11012/30704/22204，未停止或替换正在使用的根入口，真实Data未修改。新版本EXE在独立验证目录可直接获取，发布不受此实例占用影响。


plus0.0.4-20261017 已完成公开发布：main/tag提交349172a82ea62ccb5382387ccee5791e8ae69e93，Release415751560，draft=false/prerelease=false，Latest读回核对通过。4个附件（AutumnOS.exe、对应445文件源码ZIP、release-build.json、SHA256SUMS.txt）全部uploaded，服务端size/digest逐项与本地一致，未改变旧Release/tag。源ZIP1,505,860B，SHA256 fcebfac8f6dc18fde30c11fc1303197c9faf0e727f3360ce96358a1cb22fa504。发行页 https://github.com/paimeng5201314/autumnlab/releases/tag/plus0.0.4-20261017 。实际记录 artifacts/github-delivery-20261017/delivery-status.json 及 artifacts\github-delivery-20261017\publication-20261017T105456-de6866a4\result.json 。根程序此前重新运行，当前实例及真实存档保留；新EXE使用上述已验证的独立产物。

