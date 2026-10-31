# 普通权限剪贴板 AppContainer 六项 A/B 控制端

制作人：派蒙。本目录是待审阅、仅编译的独立机制探针，不是产品宿主，不解除第三方门禁。运行需要本轮新的具体私有窗口站 UAC 授权；过去一次性授权不复用。

## 固定入口和身份

控制端为 WinExe，入口：`--execute <runNonce32> <manifest绝对路径> <holderPID> <brokerPID>`。普通权限 lease broker 验证固定清单后启动控制端；控制端自身绝不请求 UAC、加载 elevated 组件或执行任意命令。

manifest含 schema_version=1、D盘已有case_root、child_directory、runtime_directory、child_executable、runtime_version=154.0.4258.53、child_files/runtime_files完整相对路径/长度/SHA256，以及 broker_executable/broker_sha256。控制端拒绝已有同名 `clipboard-case-<nonce>`，不复用旧报告或用户数据。child与Runtime分别向A/B全新D盘目录逐文件复制核验。项目直接Link复用已经审阅的browser-parent Native/OwnedFiles/ProcessAudit源码，统一构建时需同时绑定这些实际源字节。

控制端通过原生OpenProcess取得并保持原holder、broker句柄，核对两者为固定完整映像路径和摘要、当前用户相同SID/session、holder提升且High IL、broker普通Medium且非AppContainer。不信任ready JSON决定权限。profile名称精确为 `AutumnOS.Paimon.ClipboardProbe.<runNonce>`；先只Derive SID与新station DACL匹配，A无剪贴板preflight通过后才普通权限Create新profile，并核对实际SID完全相同。

每个collector/browser child另创建一个未命名独立Job，CreateProcess前以PROC_THREAD_ATTRIBUTE_JOB_LIST原子绑定；没有启动后Assign的竞态、没有修改已有Job。新Job查询默认LimitFlags必须0，不设置kill-on-close、breakaway、UI限制或其它限额，不把此Job当剪贴板隔离机制。控制端从精确Job的PID列表补充OS进程发现，页面加载前原browser句柄的IsProcessInJob必须证明属于这个Job。Job和同步等待holder句柄都不授予B管理权限；Job handle不进入继承列表。

对应官方依据：[UpdateProcThreadAttribute的JobList](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject)、[Job PID列表及嵌套范围](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_process_id_list)、[IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)。JobList支持Windows10及以后；API/属性/嵌套不兼容一律failed/not_run，不回退成漏Job启动。

## 新对象权限及影响

lease holder只创建本轮新 `AutumnOS_ClipboardProbe_<nonce>` noninteractive station。控制端检查真实对象owner和三条显式allow ACE：当前owner/SYSTEM=0xF037F，精确新package SID=0x23（ENUMDESKTOPS|READATTRIBUTES|ACCESSGLOBALATOMS，不含ACCESSCLIPBOARD或CREATEDESKTOP）。检查Low NW mandatory label。所有已有WinSta0/Default/用户目录/profile ACL均不修改。

控制端仅将自身process station关联到这个新station，在其内创建随机128位唯一 `AutumnOS_ClipboardDesktop_<freshGuid>`。创建前先确认process仍关联精确新station，再用真正OpenDesktopW探测这个精确名字：flags0、不继承、只请求READOBJECTS=1，显式清原生last-error，只有NULL加原生错误2/3才证明不存在；已有handle先关闭并拒绝创建，5、0和其它错误均failed。新desktop三条DACL：owner/SYSTEM=0xF01FF，精确package SID=0x87（READOBJECTS|CREATEWINDOW|CREATEMENU|WRITEOBJECTS），Low NW label。禁止hook/journal/switch权限，不调用SwitchDesktop，也不把此内部不可见窗口冒充产品内嵌界面或中文IME验收。

原EnumDesktopsW断言已删除。首轮nonce4975f16d7d254cf4857b44550a5b2f30实际FALSE/0导致失败，0child/0profile/0seed且cleanup证明保持原报告；不把FALSE/0改成空集合通过。官方API未保证空枚举返回TRUE，而原string callback也没有显式Unicode标注。新guard不依赖枚举。[OpenDesktopW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-opendesktopw)限定当前station和精确打开操作；[CreateDesktopW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw)允许返回已有对象，因此fresh依据是已认证CWF_CREATE_ONLY新station+精确目标Open not-found+返回后name/owner/DACL/label核验，不伪称CreateDesktop有atomic-create-only标志。

CreateDesktop文档说明与调用线程关联，[CloseDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-closedesktop)不能关闭本进程线程仍使用的handle。因此CreateDesktop只由同进程短命metadata线程执行，该线程不先调用任何其它USER/GUI/COM API；主控制端真Join后再读取和持有原HDESK。SD内存保留到线程真实退出，worker的原Win32错误在worker立即采集，不从主线程重取。没有SwitchDesktop、没有显式创建GUI窗口；失败不强杀或遗弃线程。

每个child通过lpDesktop在CreateProcess前绑定新station/desktop。显式继承列表仅含两个新stdio pipe及原holder的第三个只读等待副本：DuplicateHandle请求严格SYNCHRONIZE=0x100000，无QueryLimited、token、VM、创建线程或其它权限。父已经以自己原句柄验证holder身份/创建时间；child收到的PID/creation只是父绑定元数据，不能冒充child独立身份查询。child只Wait0证明同一继承内核对象仍存活，启动清除该句柄的继承标志。任何完整station、desktop、collector窗口站句柄、宿主目录或其它进程句柄都不传给B。

B的SECURITY_CAPABILITIES为精确新package SID，零capabilities。环境只含标准LOCALAPPDATA/SystemRoot、新自有TEMP/TMP及关闭.NET诊断的两项开关，不继承凭据、代理或WebView2覆盖。B的UDF/TEMP在本轮新OS profile下唯一子目录，继承OS标签，仅新子目录给owner和package Modify。所有Windows profile本身ACL不变。

## 测试顺序和真实判据

先A/B两个无剪贴板preflight。浏览器创建后，控制端独立持有原browser及所发现后代句柄，查询实际OS AppContainer/SID/IL/caps；固定版本、策略、InPrivate、UDF hash核验后才发load_owned_page。只注册自有页面handlers，不发perform、不启动collector。正常退出要求operation_attempted=false和operation_succeeded=false；成功之后才允许开始六项copy/cut/paste的A/B。

六case均独立browser/profile目录和caseNonce，顺序A-copy/B-copy/A-cut/B-cut/A-paste/B-paste。唯一实际调用native clipboard API的是普通权限collector，而且每次调用前由child自己重新校验私有station/desktop/noninteractive及原holder等待句柄存活。collector在browser CreateProcess之前写入不可猜测的合成baseline并立即读回；页面、CDP输入和browser IPC从不接收该baseline原文。copy/cut测试marker为可独立计算的 `owned-page-<caseNonce>-<operation>`。

浏览器初始化/页面ready后，collector sample0必须证明baseline和序号均未改变，并枚举私有desktop窗口证明原browser PID实际位于该desktop，之后才允许perform。copy/cut通过真实CDP鼠标刺激可信点击，paste通过可信点击和Ctrl+V，保持旧六case的激活、焦点和实际动作断言。CDP只是刺激方法，不是隔离机制。动作后1..10原生样本每项均检验序号和内容；A copy/cut需真实marker且序号改变，B copy/cut需baseline保持且序号完全不变，不能把未写新文本当作没清空。paste的A必须由网页实际读到baseline（仅hash比较和trusted paste事件），B所有采样页面均为空、native baseline/序号不变。

每个browser都必须先得到绑定原PID的Normal事件、父原持有browser/host/观察后代句柄全部signaled、SDK最终环境集合空，才允许collector final及下一次seed。collector final还需10样本齐全且全部符合实际预期，再自然退出，父观察原collector句柄。没有baseline恢复、用户clipboard备份/还原、JS覆写、Job限制替代或失败断言豁免。

实时paste_event_data在父端按连续event_index永久累计，包含trusted、text_empty及实际文本SHA256；不局限于1..10样本窗口，关闭到host_complete期间的事件也保留。B任意非空或不可信事件立即failed；A非空事件必须匹配native-only baseline hash且至少有一次可信读取。host_complete的总数/非空数/不可信数/累计digest必须与父端完全一致。digest为UTF8逐行 `index|trustedBit|emptyBit|textHash\n` 的SHA256，零事件哈希为空字节SHA256。另要求close阶段真实DOM终态快照，A最终value hash等于baseline，B value_empty=true且hash等于空文本；未观测时null，不能默认空通过。

所有Wait在Tick后先检查全部child失败历史、OS观察缺口与Job查询/成员失败再接收目标事件；Drain即使退出条件已满足也追加最后Tick和EOF队列审计。B最终检查全部已观察host/browser/renderer/utility token，最终成功前还遍历已retired所有child，防止迟到失败或后建进程遗漏。新seed/perform/load及下一case前必须有持久报告；报告IO失败保留固定错误阶段/HRESULT且最终强制failed/非零退出，失败资源清理仍继续。旧probe-a18621...及prepared-8a918...未执行并保持not_run，不能用于本修订验收。

## 失败与资源保留

stdout UTF8 JSONL每行64KiB、总计4MiB；stdin命令1024字节。信封绑定schema/nonce/原host PID。超限、协议错误、子进程错误、超时或取消一律失败，只请求自身browser/collector正常close，不Kill、不升级权限、不切换真实桌面。

事件等待35秒、正常组退出观察20秒只是观察预算。未证明原句柄signaled/Normal/SDK空且精确Job ActiveProcesses=0及PID列表空时，控制端持续pending保存报告、保留原句柄/profile/lease，不以超时退出触发holder释放。初始化失败尚无browser PID时，Job原子绑定+真实Active0/PID列表空及原host/所有已观察句柄signaled可证明无组残留，功能失败保持failed，不将其当正常浏览器验收通过。功能失败与清理分别记账。精确profile删除后用保存原路径GetFileAttributesW（仅错误2/3为不存在）及同SID GetAppContainerFolderPath（仅80070002/3为不存在）核验，不用Directory.Exists(false)替代实际检查。关闭空Job及本控制端持有的新desktop/station句柄并恢复自己的旧station后，才允许控制端退出，lease broker随后释放holder。

另有严格failure-only资源清理分支：用例已经failed后，即使实际browser崩溃而没有Normal/host_complete，只要精确原子Job真实Active0/PID列表空、全部已持原句柄signaled、stdio已EOF且Job成员核验无缺证，资源可记 `failed_case_kernel_group_empty` 并精确清理。这只证明无残留，不改变failed、不补Normal或SDK验收成绩。任何Job0/原句柄/成员归属证据缺失仍保持pending。

报告在 `manifest.case_root/clipboard-case-<nonce>/report.json`，保存完整逐项事件、native launch错误、OS token/映像hash/创建时间和原句柄退出事实。进程快照覆盖固定为sampled_not_exhaustive，不声称未采样短命进程、整个产品、其它浏览器通道、内部呈现或IME均已证明。

## 当前验证状态

源码和本目录锁文件已生成，项目SDK Windows Release编译0警告0错误。本修订尚未运行；上述原UAC失败保留且其一次授权已消耗，不能复用。六case实际机制与产品验收仍not_run。统一冻结构建、外部helper/child复核以及下次具体一次UAC授权由集成负责人处理；不能从编译成绩推导六case通过。

新增纯协议/事实门禁自检入口 `AutumnOS.OwnedClipboardController.exe --self-test <绝对新report.json>` 在任何普通probe参数处理前分支，只调用与生产相同的EvidenceGates及paste协议解析函数。覆盖正常事实正控、有效事件与历史失败同批时先拒绝、最终新增非AC/错SID/额外capability拒绝、十次采样之后的实时paste数据泄露、累计摘要不一致以及报告IO错误拒绝。报告用CreateNew写新文件，明确native_calls=0、profile/clipboard not_run、uac_requests=0。它不调用Native Tick/Start/lease/profile/clipboard，不能证明Windows边界；本轮仅编译，自检执行由集成负责人统一进行并独立保存成绩。

另有真正普通权限元数据自检：`AutumnOS.OwnedClipboardController.exe --desktop-guard-self-test <绝对新report.json>`。它在当前自身station只创建一个全新中文nonce名owner-only desktop；复用生产实际Open guard/短命worker，要求创建前真实2/3、创建后guard实际拒绝已有对象且关闭其探测handle、原HDESK关闭后再真实2/3。核验调用主线程desktop与station前后完全不变，报告真实API次数、原始错误、worker_joined、current_input_desktop_switch_calls=0、0clipboard/profile/UAC。它不建新station、不创建窗口、不切换用户输入desktop、不打开或处理剪贴板。普通当前station正反例不代替私有新station/LowMIC/AppContainer浏览器或六case验收。当前只编译，执行由集成负责人审阅后进行。

## 真实断管清理反例修复

后续noncea1357c87910c449aa9e8625c112aac3b已实际执行：A私有站无剪贴板preflight通过，B进入Main前0xC0000142退出；其AppContainer/Job0/原句柄退出事实及0seed保持原报告。随后旧控制端StreamWriter.Dispose在关闭pipe上flush抛IOException，跳出了旧finally，导致精确profile未进入正常清理；原failed/running末报告及异常事件不回写成通过。本次只修新源码，旧profile恢复由集成负责人另按精确原证据处理，不是此代码编译的运行结果。

新代码将StreamWriter设leaveOpen并独立持有stdin transport和原SafeFileHandle，stdout EOF transport也单独持有。writer dispose/flush、底层transport/SafeHandle、空Job、每个已signaled原进程句柄全部通过同一CleanupJournal逐项try记录；writer失败不跳过其余资源，错误类型/HRESULT/原生stage仍永久保留并使最终结果failed。主finally分阶段继续精确profile删除/双原生后查、desktop/station/peer句柄与两份SID释放，primary_failure保持原始用例错误，cleanup_journal独立记录；最后屏障捕获意外managed cleanup异常并保留资源重试，不能因finally异常退出丢下后续阶段。无退出/Job0/资源关闭证据时仍pending，不将清理异常改成成功。

独立回归入口 `--cleanup-self-test <绝对新report.json>` 使用同进程真实System.IO.Pipes匿名pipe：实际关闭唯一reader，缓冲写入的StreamWriter.Dispose必须产生真实IOException；调用生产CleanupJournal后底层transport及后续Job/profile/station的纯sentinel动作仍执行。该回归不是no-native协议测试，明确只新匿名pipe/托管IO，不启动进程、不调用GUI/clipboard/profile/UAC，不把sentinel当作真实Windows资源清理验收。当前仅源码/编译，执行成绩由集成负责人另记。

## 可选原生 DLL 分层诊断模式

manifest可显式设trial_mode=`native_loader_diagnostic`并给出native_child_directory/native_child_files/native_child_executable完整相对路径/字节数/SHA256集合。此模式只核验复制本地固定native child，不复制或启动WebView2/CLR child，不启动collector，不执行clipboard或窗口/COM操作。A在同一已认证新station/desktop普通权限完成全部DLL层和metadata绑定、原句柄退出与精确Job0后，才创建B精确profile并以原0x23/0x87对象权限运行同字节native child；不追加clipboard位或放宽已有对象。

每个native child仍以PROC_THREAD_ATTRIBUTE_JOB_LIST原子归属独立Job、显式两个stdio端点与原holder的Sync-only副本启动。父不为不读命令的固定native child创建StreamWriter，直接关闭其本端未使用stdin写端给出EOF；stdout保持严格nonce/原host PID/固定stage-result/大小限制。最小环境另有AUTUMN_PROBE_NONCE/SIDE/STATION/DESKTOP四个固定值。预期PE仅静态KERNEL32 imports，入口先发原生阶段消息，再SYSTEM32加载USER32/GDI32/SHELL32；USER32成功后才读取准确station/desktop metadata。native_loader_complete失败保留原failed_stage/native_error并failed清理，不变成安全证明；进入Main/entry以前失败也保留OS原exit code和Job计数。

成功状态明确为 `passed_native_loader_diagnostic_only`，其含义仅为本次原生层对照完成，browser_execution和clipboard_operations均not_run，isolation_acceptance仍not_claimed。B的全部已观察进程仍逐个实际OS token核验AC1/精确SID/IL4096/caps0，不能用child环境自报代替。该模式仍需要下次具体新UAC授权创建新station；已消耗的a135...授权不复用。诊断结果及cleanup与clipboard六case完全分账。
