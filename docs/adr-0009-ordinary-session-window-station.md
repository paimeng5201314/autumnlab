# ADR 0009 · 普通权限派生会话与自动命名窗口站验证

制作人：派蒙。日期：2026-10-30。状态：元数据试验包已准备，真实新会话/窗口站操作未授权、未运行，尚未采用为产品路线。

## 需要解决的问题

ADR0008中的固定Runtime私有站浏览器/剪贴板六项对照已通过，但创建指定随机名字的新station仍使用一次获准的UAC holder。产品要求普通用户日常运行和便携交付，因此不能把该诊断方式直接当作生产启动方案，也不能静默安装服务、要求日常管理员运行或放宽现有WinSta0/Default。

现存历史文档记载当前普通logon session下命名CREATE_ONLY失败5、NULL自动命名CREATE_ONLY碰撞183；对应最早原始01/02报告当前未找到，不能声称已重新核验原报告。官方API明确只有Administrators可指定station名字，NULL名称来自调用进程logon session标识，且CREATE_ONLY在既有名称时失败。

## 可验证假设

CreateProcessWithLogonW的LOGON_NETCREDENTIALS_ONLY模式在LSA中建立新logon session，保持本地调用者token语义，并将给定值用作网络默认凭据；该模式不验证所给凭据。官方说明该进程创建入口不要求调用者具备CreateProcessAsUser所需的特殊权限。

因此提出一个有限假设：普通权限派生进程的真实AuthenticationId若不同，NULL+CWF_CREATE_ONLY能否得到一个此前不存在的自动命名station。文档并未保证该组合在本机、Windows Home/10/11或特定策略下成功；只有原生实测才能判断。不能将线程impersonation、新token或接口返回成功当作已获得新station。

## 已实现的最小试验

新独立源码在 `artifacts/isolation-completion-20261030/netonly-station-probe/`，只支持冻结manifest入口。普通parent最多调用一次CreateProcessWithLogonW，参数固定NETONLY、显式WinSta0\Default、CREATE_SUSPENDED、无窗口和最小Unicode环境。调用使用独立随机的合成本地用户名和随机合成密码，不读取真实密码，不将这些值放入argv、环境、协议或报告；缓冲区清零。它没有Windows或Logto认证成功的含义。

parent先持有原始process/thread句柄，核OS token同user SID、Windows session、IL8192、未提权、非AppContainer、没有新增present/enabled privilege，且AuthenticationId不同，才恢复原线程。双方通过CurrentUserOnly本地命名pipe、kernel peer PID、原process句柄/镜像与nonce相互绑定。协议仅hello/ready/create/result/ack、4096字节严格UTF8标量消息，没有任意命令、路径或SDDL输入。

child只尝试一次NULL+CWF_CREATE_ONLY窗口站，描述符仅当前user与SYSTEM、protected DACL、Low/NW。它从原HWINSTA回读实际名字、非交互和非继承标记、owner/group/两ACE/Low；若Create API改变本进程关联，则先恢复借用的原station，再关闭新原句柄。没有desktop、window、profile、browser、clipboard或网络API。碰撞失败不重试，不采用未知既有对象。

parent等待原child/process/thread真实signaled，使用其准确新站名进行READATTRIBUTES=2/inherit=false独立后查；只有NULL/错误2或3证明该站不存在。超时、进程仍活、错误5/0、缺结果或缺新站名不能当清理通过。没有强杀、服务启动或策略变更。cleanup范围明确只包含自有进程与station，不宣称清理LSA状态。

## 真实系统影响和授权

NETONLY会产生真实LSA登录上下文、系统审计以及可能的网络provider通知，即使probe自身没有网络API。其网络默认凭据上下文会使用合成值；Windows负责该系统状态的生命周期，进程和station退出不等于LSA记录已清除。不能把该操作称为纯文件读取、模拟登录或已认证身份。

本试验需要一个新的、针对该具体冻结包的明确授权；既往UAC或clipboard授权不覆盖它。授权前仅编译和纯回归，禁止调用NETONLY执行入口。服务若被系统策略禁用则保留实际错误，不启用服务或修改策略绕过失败。仅使用合成值，不索取用户真实账号密码或Client Secret。

## 当前产物与结果

准备包prepared-b398ec28f8cc453db935e770d54735e6，构建probe-36216529fcaf4327a5515af213b51a7e。源快照sha256:80b884deebe75f8609c89bbe27cb4a394cda0a3097a27c9336a7a5093b21f013；源ZIP SHA cf648c526191bd9873b4cf7f8a994ae8dc1d58630db85ad9d402af4089224458，逐项回读通过；manifest SHA2f4a9a52fb33af44dbaab367051588f381fb8913c1e1280c5bb5352ed18b626d。

本机Release与19项实际parser/manifest/result/token纯回归通过，无参数compiled-only入口exit0。上述入口logon/station调用均0；真实新LSA会话、自动名station、AppContainer/browser与生产普通启动均not_run。源码内REVIEW描述冻结时source-only状态，当前构建/回归事实另在build-result与verification报告，不能把纯回归当本试验已运行。

执行后记：派蒙随后明确允许本包一次普通权限metadata试验，已执行并消耗。netonly-case-6a4bfd4689f6410e88a46d82ebf4a471，report SHA15d8b533de0382c36b30487b7bdc5105b82902f5cc92d05e38209875a3ef952e；status=completed_netonly_station_metadata_only。CPWL恰1次，原父子同SID/session1/IL8192/未提权/非AC、present/enabled privilege无新增，AuthenticationId实际不同；NULL/CWF_CREATE_ONLY得到新Service-0x0-3e0f3bb$，原handle上验证protected owner/SYSTEM两ACE/Low/非交互/非继承，原关联恢复、新handle关闭成功，原process/thread exit0、父精确名字Open NULL2后查成立。没有UAC，LSA_cleanup仍not_claimed，不是身份认证成功。该结果支持继续实现普通会话内的受限浏览器/内部显示输入原型，尚未证明后者在新LSA上下文可用。其新的LSA/profile/browser/交互范围须再具体冻结与授权，不能沿用本次metadata-only授权。

即使这个元数据试验成功，也仅允许继续审阅普通私有站broker原型。内部画面、鼠标/键盘/真实中文IME、SDK消息撤权顺序、完整网络与数据矩阵、通用应用入口和最终产物都仍须依赖顺序验收。

## 内部画面与IME的并行核查

2026-10-31修正资源后的28cbc包已单独获一次授权并执行，纯WinUI门禁原23324实际pass/exit0，随后普通无UAC受限浏览器与原生动态图确实成功（case-a37a...，989显示帧）。真实人工pointer10/key27/27/IMEstart1/change5，但Ended(start0,length0,caret7)被桥接严格活动span规则误拒绝，未转commit，链failed；三层资源清理全部闭合。End.Length描述当前组合部分，不保证最终提交文本span，详见[官方TextCompositionEnded](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textcompositionended?view=windows-app-sdk-1.8)与本地SDK XML。下一修复使用真实Ended→pending文本settle事务，保留原生活动阶段和选区，不以0长度猜取消、不以硬编码文字或仅最终注入替代IME。源/纯回归准备期间不自动重用授权；具体终态与独立SHA见AUTUMNOS_PROGRESS及case/independent-postreview.json。G1仍未通过，G2不开始。

官方源码的补充依据：[TextBoxBase.cpp](https://raw.githubusercontent.com/microsoft/microsoft-ui-xaml/main/dxaml/xcp/core/native/text/Controls/TextBoxBase.cpp)中TxNotify在通知Ended前清除compositionInProgress，SendTextCompositionEvent重新查询当前EM_GETCOMPOSITIONRANGE，说明通知范围与最终提交正文不是同一个合同。该main源码不作为本机1.8二进制逐字证明；本机真实Ended0/caret7及固定1.8 API语义才是本次修复的直接依据。PendingEnd时的TextChanged只更新同一隔离原生缓冲，不额外insert；真正settle后只产一条commit或cancel，不能以Dispatcher等待时长本身证明文本有效，不能在未settle事务上启动另一组合或提前发送close。

2026-10-31执行后记：prepared-46389...另获一次明确授权并实际执行，原UI4348在case创建前C000027B退出；Event1000 Microsoft.UI.Xaml.dll、WER1001 combase8000ffff，详见原prepared/ui-startup-crash.json，尚无完整dump因果结论。原WinUI缺应用XAML生成元数据/PRI/App.xbf是已核实的静态缺项，修正源码使用标准App.xaml/InitializeComponent；这是同一WinUI原生技术路线，不更改安全策略。下一包加入同一窗口的纯UI启动门禁（真实Loaded/Rendering/HWND可见/正常退出），原进程0和绑定报告通过后才进入一次NETONLY/browser。新包重新编译/冻结/授权，旧包和一次授权不重用。当前浏览器、画面/物理IME仍not_run，G1未闭合。

2026-10-31新普通权限原生呈现包prepared-46389c3c57984e18aae93d36cc68fc9a已完成本机两项目Release/各compiled-only入口/16项共享协议纯检查及40源文件实际快照、源ZIP回读和精确产物manifest冻结。源sha256:fd32998469b9534eb8e34d9c4149fec6412afcb62e94266de30be483e2fb7fa7，manifest43aed05bdd9ba2ce3a4c817bc94953ad5e8de007fcc6f2328128cdde576ed3d2；构建probe-a9e8dcaf04114fb79b60ce6bb59ff7a3。首次统一发布失败记录保留；当前新LSA/profile/browser/可见WinUI和物理IME均not_run，待本具体包新的授权，不沿用metadata授权。

原型增加原Core/NETONLY Host句柄身份、两级QUERY-only Job见证、各资源独立退出屏障及兄弟终态报告hash串联。真实StartIndex/Length/selection与开始/更新/结束/取消转发、右/中键释放和原子private-paste、Rendering事件后计帧都已源码实现；没有实际运行前不能称有效。平台IME/provider/UIA/全局热键旁路仍not_measured，不由应用层Paste取消宣称完整封闭。

本机WebView2 SDK有CapturePreviewAsync，WinUI1.8.260803003有TextBox组合开始/变更/结束事件。候选是固定viewport的有界frame传输与原生WinUI图像呈现，随后转发真实鼠标/键盘和组合事件；未实测性能或显示。CDP Input.imeSetComposition/insertText是否在固定154运行正常仍需功能探测与实际候选窗/组合更新/提交/取消测试，不用最终文字注入代替IME。

WinSta0上的native输入proxy会接触宿主UI能力：必须在用户剪贴板默认动作发生前阻断Paste/Copying/Cutting事件及拖放/上下文菜单通道，CtrlV只刺激private child；不能让native TextBox从系统clipboard读入内容再转给应用。不能改用仍有平台限制的CoreTextServicesManager并假称Win10通用。上述只属API及设计核查，不计G1通过。

## 主要官方依据

- [CreateProcessWithLogonW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithlogonw)：NETONLY、新LSA上下文、非凭据验证与启动属性。
- [CreateProcessAsUserW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)：普通CPWithLogon入口与特殊权限要求的区别。
- [TOKEN_STATISTICS](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_statistics)：AuthenticationId的logon session含义。
- [CreateWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)：名字权限、NULL自动命名、CREATE_ONLY和句柄生命周期。
- [Administrative tools and logon types](https://learn.microsoft.com/en-us/windows-server/identity/securing-privileged-access/reference-tools-logon-types)：NewCredentials与LSA凭据上下文。
