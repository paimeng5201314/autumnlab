# 普通权限私有窗口站 · 原生内部画面原型

2026-10-31当前增量：0b70包的真实原生A已通过（start1/update6/end1、trusted end0、最终值/end.data均“你好”摘要、原进程exit0及Normal/SDK0/持原句柄退出），私有B在14帧时首次report_write80070005停止，尚无B键盘IME，旧运行不追写通过。新修复只统一Host/Core/UI报告发布：同目录pending写入/Flush(true)/关闭后File.Replace到已存在的目标，不先删目标、不放宽ACL；仅replace的Win32 5/32/33给2秒和101次双上限，每次间隔20ms。捕获固定temp_open/serialize/flush/close/replace阶段和原HRESULT/attempts，不记录路径或任意异常消息；预算耗尽沿用永久report_error、停止新输入与原清理门禁。此前实际Move持读反例及Replace共享矩阵在独立artifact保留；新真实文件IO回归和编译以root新build实际报告为准，未运行新A/B。

实时状态只用`scripts/Read-SessionBrowserLiveReport.ps1`短暂Read+ReadWrite/Delete共享读取byte[]，先关句柄再解析；这不是proof。原生A冻结证明和manifest的严格FileShare.Read pin、退出后的最终SHA/原句柄绑定保持原判据。旧程序缺保存子阶段，不能把本次具体拒绝来源宣称已定位某个读者。微软[File.Replace说明](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)与[dotnet/runtime原始复现](https://github.com/dotnet/runtime/issues/114230)作为参考，本机实际回归才决定新代码状态。

新文件IO检查首次build e11d53a99f914b039e283ded76cba3f8真实36/37；仅补诊断的d3acbcac9b6f476699996d5f41302217也36/37。第8项要求并发替换期间路径始终可读，这一新假设无API保证；实际100代及100次顺序新打开均完整、原held旧generation0字节不变，但并发读者220个成功快照后遇FileNotFound2。两个失败完整保留。纠正的新观察合同是：接受的样本必须完整JSON和校验正确，路径缺失/共享冲突2/3/32/33只能明确计为unavailable而不作证据，其他异常仍失败。观察器不自动重试、不返回空成功；最终proof仍须原进程退出后的完整文件和实际hash绑定。本项不减少任何原29项纯回归或原生输入/清理验收，也不宣称File.Replace保证全过程路径可见；特殊1175/1176/1177及其他错误不进入重试，仍失败并保留当前文件/清理待证事实。[ReplaceFile原始语义及失败状态](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)。

最新源修（未构建/运行）：case-4ac 同一文档/导航1内，首段目标非active却收到所有API ACK，DOM仍0；第二段目标active后有真实trusted start/update，commit的total end1但trusted end0。页面key/初始化/转抄未见错误，本候选不改变trusted验收。新增 CompositionEffectGate 供真实Host与UI调用：动作前核本次文档和 owned textarea/document焦点；首非空preedit要求本次start+本步update，其后preedit要求新的update，事件data长度/hash匹配。commit/cancel核新的totalend与非composing；commit还核末次实际end.data长度/hash。OwnedPage只快照真实事件data的长度/摘要，原文不进入IPC/报告。固定只读轮询到真实效果才回operation_succeeded，预算仅失败；不执行任意脚本、不合成DOM事件、不强行focus。空start只建立基线，空update明确failclosed。UI Idle从最新已呈现frame准入，Active/PendingEnd不因后续帧禁用控件。旧24纯例保留，新增4个实际helper/codec回归共28例，待root统一构建/执行，不能据源码宣称IME已通过。

制作人：派蒙。仅本地 G1 机制原型；不是 AutumnOS 产品放行依据。

## 实际执行范围

原生 WinUI viewer 在 WinSta0 显示来自受限浏览器的 800×600 PNG，创建一个普通 CoreParent；CoreParent 最多调用一次 `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` 创建 StationHost。参数使用随机合成值，不读取真实密码，不代表 Windows 或 Logto 身份认证。该 API 会产生真实 LSA 网络凭据上下文、审计及可能的 provider 通知；原进程退出不等于 LSA 状态已清理。

StationHost 在这个新的 AuthenticationId 下创建一个 NULL 名称、CWF_CREATE_ONLY 的私有窗口站和一个新 desktop。只修改本次新对象：窗口站 protected owner/SYSTEM/package 三 ACE、package 0x20023、Low NW；desktop package 0x87。没有对既有 WinSta0、Default、用户文件或全局策略扩权，没有 UAC。

新建唯一 AppContainer profile，以零 capability 在原子 Job 内启动同一冻结 core 的 browser-host 模式。固定 Runtime154.0.4258.53、SDK1.0.4258.31，实际使用与生产逐字校验的 FrozenPolicy，不关闭 Chromium 沙箱。运行页仅本包合成 canvas/textarea/WASM42/RAF；不启动 ChemLeap 或第三方应用，不接入生产 SDK。

## 数据与输入边界

双向 pipe 使用 CurrentUserOnly、原始进程句柄/实际 token、kernel peer PID、nonce 和 epoch 绑定。PNG 最多2MiB，严格尺寸、chunk CRC、受限元数据和解压后栅格长度校验；输入 header 最多16KiB，固定命令、有限坐标/文本/组合范围，单请求在途和单调 frame/request。没有任意 CDP、脚本、URL 或路径命令。

帧需实际 CapturePreview、800×600/DPR1/scale1读回、真实固定像素与不同画面。WinUI 对同一帧完成解码后呈现；先保存两张无输入、空文本的初始不同帧供复查，不保存用户组合文本日志。文字输入只通过原生输入代理：开始/更新/结束/取消与选区使用实际 composition 事件；最终文字注入不能单独算中文 IME 通过。原生 clipboard 默认、上下文菜单和拖放通道被阻断；高级系统 IME/provider 通道未计为隔离已通过。

物理鼠标、键盘和中文输入法必须由实际用户操作验证。没有真实事件、DOM可信组合或最终“你好”结果时保持 partial/failed。源码事件处理、纯协议检查和编译不能代替这些验收。

2026-10-31第二包case-a37a的实际普通启动/WinUI帧/鼠标/键盘/start+update已发生，989帧、pointer10/key27+27/IMEstart1+change5，但Ended(start0,length0,caret7)被旧活动span校验误拒绝，完整输入验收失败。原8个B进程/16原句柄、Normal/SDK0/EOF、Job0与精确profile/desktop/station清理都核实；失败证据和已消耗授权保留，不重跑旧包。

新的源修只纠正输入事务：活动preedit仍用真实范围/相对caret；结束范围只作为真实通知事实，不代表最终文字长度。PendingEnd必须观察同原生TextBox结束后的实际TextChanging代次，结算时buffer长度/哈希要与该代次相符，单个低优先级回调或延时不作最终文本证据。期间TextChanged不另行insert；只发一条commit或空buffer cancel，确认成功入有界队列后清理自身当前组合buffer。关闭须等该事务结算/入队再排空；未证边界保持失败。报告仅通知范围、版本、长度/哈希、命令ACK与最终DOM对应，不保存正文。新helper纯回归与后续物理运行各自记录，不以逻辑测试冒充原生通过。

提交修复已在新授权的case-c9ce实测：真实End→TextChanging revision1→len2/SHA一致的settled/实际ACK/最终DOM“你好”成立，全部资源清理通过；但最终DOM composition start/update/end均0，12个RPC返回不等于阶段事件生效，严格G1仍partial。下一源候选仅增加动作前后只读焦点/选区/文字len/hash/DOM计数、随机文档实例摘要和导航计数，及最多256条无原文IME ACK证据；不更改焦点行为、CDP动作或既有验收，不用观测影响调度当成根因修复。新事实及codec纯回归不代表浏览器或IME已复验。

## 退出证据

启动 profile/browser 前，CoreParent 与 WinUI 分别复制同一个新 Job 的 QUERY-only 句柄并正向确认；WinUI 另持有原 StationHost 的 QUERY+SYNCHRONIZE-only 句柄，核 PID/创建时刻/镜像/实际 NETONLY token 关系。CoreParent 退出不能证明 Host 不会稍后创建浏览器，必须先观察原 Host 退出，再证明 Job Active0/PID[]。

正常收尾要求浏览器 Normal 事件、SDK进程0、原持有进程退出、pipe真实EOF与句柄退休；再删除精确新 profile、核原路径/同SID不存在、关闭原 desktop/station并作精确名称不存在后查。失败资源各自保留证据，超时不作成功、不强杀。缺少已创建对象的清理证据时保持 pending/failed，不能凭 PID 消失猜测退出。

ui/core/station-host 报告按 nonce、manifest/source 和实际最终文件哈希关联。报告仅包括元数据、哈希、计数与阶段，不含合成密码、真实凭据、原始 token 或用户组合正文。

## 构建与批准

`scripts/Build-SessionBrowserPrototype.ps1` 恢复固定官方依赖和锁，发布两个 Release 程序、运行无参数 compiled-only 入口及真实协议纯检查，记录实际字节 source_snapshot_id、完整产物哈希和逐项回读的源 ZIP。它不运行 NETONLY、profile/browser 或交互窗口。

历史：首次交互启动在case前C000027B失败，详见已消耗的prepared-46389...；旧源ZIP/产物/manifest保留。随后补标准App.xaml/InitializeComponent并验证真实WinUI启动，经历的两步链已另有实际报告和已消耗授权。当前候选改为ADR0010的三步有界链：纯WinUI启动门禁→普通WinSta0/Default直接原生WebView硬件IME对照A→一次私有B桥。A入口 `--native-ime-control <manifest> <prepared/native-ime-control.json>` 只用Runtime.evaluate读证据，无CDP输入/NETONLY/自定义profile/station；保留新artifact UDF，不冒称全OS隔离。原A process退出0及真实DOM/Normal/SDK0/已持原句柄全部通过后，父端写owner/SYSTEM-only protected目录中的control-binding。B完整入口为 `--execute <manifest> <controlReport> <reportSHA> <bindingSHA>`，只读pin并验证source/runtime/原进程退出证明；其raw end trust模式须匹配真实A测量，缺失/零end/mixed一律拒绝。不是把false改true或删除真实end要求，详见纳入snapshot的ADR0010。任何步失败不重试，不复用旧授权；原生A与新B当前尚未运行。

`scripts/Run-SessionBrowserPrototype.ps1 -Stage Prepare` 仅生成冻结 manifest 和具体影响。Execute 需要新包明确授权、精确 hash 绑定及 CreateNew 一次 guard，不自动重试。原 metadata 或 UAC 单次授权不能沿用至本原型。

最终普通启动、真实画面、中文候选/更新/提交/取消/选区、性能、完整键盘、生产 SDK/撤权/账号/缓存/网络矩阵，以及普通第三方应用和最终打包均需各自真实验收。生产门禁仍 blocked_pending_G1_and_product_integration，G2–G5不因本原型生成而提升。
