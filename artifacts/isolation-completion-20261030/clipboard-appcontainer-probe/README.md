# AutumnOS AppContainer 真实剪贴板子程序

制作人：派蒙。本目录是仅使用新建非交互窗口站与合成数据的独立机制代码，不接入生产、不解除第三方门禁。旧 `clipboard-station-uac-02` 的 Job copy/cut 清空反例保留原字节；本次将受测浏览器交给真实 AppContainer 父启动器，而不是再使用 Job UI 限制。

固定 `.NET SDK 10.0.401`、自包含 `.NET 10.0.12`、win-x64、WebView2 SDK `1.0.4258.31` 与固定浏览器 `154.0.4258.53`。`OutputType=WinExe` 避免额外控制台。Chromium 默认沙箱保留，`FrozenPolicy.cs` 的生产常量由集成构建脚本逐项核对；从首次合成 HTTPS 资源响应开始使用完整 CSP、Connection-Allowlist 与 Permissions-Policy，不使用 NavigateToString、CSP 放宽或 JS eval。

本程序的两种模式由普通控制器在 `CreateProcess` 前设置精确新 `lpDesktop`。程序在任何 GUI/OLE/浏览器/剪贴板调用前确认当前窗口站/desktop名称及非交互标记；每次命令和原生剪贴板动作前重新核对。程序不调用 SetProcessWindowStation、SetThreadDesktop、SwitchDesktop 或 SendInput，不访问 WinSta0，不创建profile或修改ACL。只有 collector 模式调用原生 clipboard API；browser 模式只运行同字节自有页面和相同真实编辑刺激。

## 协议

控制器提供启动参数，须由集成负责人冻结后一起构建：

```text
--browser <caseNonce32> <runtimeAbsolute> <udfAbsolute> <stationName> <desktopName> <copy|cut|paste> <holderPid> <creationFileTime> <inheritedWaitHandle>
--collector <caseNonce32> <stationName> <desktopName> <copy|cut|paste> <A|B> <holderPid> <creationFileTime> <inheritedWaitHandle>
```

最后三个参数的数值均为十进制。父程序从已验证的原holder进程句柄只复制 `SYNCHRONIZE` 权限，作为显式第三继承句柄；子程序立即清除其继承标记，防止传给Chromium后代。子程序在每次私有站绑定检查时对这个原内核对象执行零超时等待，必须仍为live；不重新按PID打开进程，不查询helper的token、映像或创建时刻。PID/creation仅是父绑定元数据，子程序不声称独立验证；完整身份仍由父程序自己的原始句柄与OS证据证明。没有任何完整station/desktop句柄传给B。

普通A和AppContainerB共用完全相同EXE/浏览器/页面/输入/策略。A/B仅由父启动器的token和新对象权限区别。每个case有独立nonce、UDF、进程组和collector；浏览器写入marker固定为 `owned-page-<caseNonce>-<copy|cut>`。

stdin严格UTF-8 JSONL，每行最多1024字节，最多64条命令，字段不重复：`schema_version:1`、`nonce`、`command`。仅 `sample` 与 `sample_paste` 还需要数值 `sample_index:0..10`。stdout无BOM UTF-8 JSONL，每行最多64KiB，信封为 `{schema_version,nonce,host_pid,elapsed_ms,kind,detail,isolation_acceptance:"not_claimed_host_output_only"}`。路径和剪贴板文本不输出，只输出SHA256与明确状态。父程序必须持续读取管道。

browser命令：`load_owned_page`、`perform`、`sample_paste`、`close`。collector命令：`seed`、`sample`、`final`、`close`。无效命令、绑定错误、EOF、重放/超限或超时均失败。collector关闭不会改写/清理clipboard；该clipboard随精确新窗口站自然释放，不保存或恢复用户桌面内容。

## 实测顺序与通过条件

先进行普通A和AppContainerB的新station兼容检查：真实初始化、加载同一自有页面到 `page_ready`，不发送 `perform`，正常关闭并由父程序取得精确Normal事件、SDK最终0、所有原始句柄退出。这一步不创建collector、不调用任何clipboard API，`host_complete.operation_attempted=false`、`operation_succeeded=false`。两项均通过后才进入六case；失败不进入clipboard阶段。

六case按 copy/cut/paste，各自 A 后 B。普通collector在确认私有站后 `seed` 一次，产生只存在native clipboard中的随机baseline，原生读回正控和非零序号必须通过。只把baseline的SHA256交给控制器，绝不把原值送给浏览器、JS、ExecuteScript或CDP。然后新建真实浏览器并独立审计其原生身份，加载页面，再令collector执行 `sample_index=0` 确认初始化/导航未改变baseline，并从实际私有desktop枚举中看到精确browser PID。

`perform` 使用旧六case中真实可执行的 CDP 受信鼠标点击和Ctrl+V按键，仅作用于已核验的新非交互desktop。copy/cut页面调用真实 `document.execCommand`；paste页面必须收到受信键盘事件，且实际原生paste事件和DOM内容由页面消息回传。CDP只提供输入刺激和读取真实结果，不注入clipboard marker、不改写断言、不作为权限边界。原生AppContainer与新station ACL才是受测机制。

动作后执行10次原生采样；paste另执行10次DOM/真实paste事件采样，结果均转为SHA256。A copy/cut必须实际写出固定marker且序号改变（cut还需删去选区）；A paste必须DOM和受信paste事件读到native-only baseline，clipboard序号保持。B copy/cut必须序号完全不变、原baseline保持，不能只证明“没有新文本”；B paste必须DOM与所有paste事件均为空，native baseline与序号保持。

paste读取证据不局限于10次采样。每个真实DOM paste事件都立即发送 `paste_event_data{event_index,trusted,text_sha256,text_empty,source}`，序号从1连续，累计至控制器释放前，最多64个事件/每个4096字符；超限或序号缺失失败。关闭开始后继续收集，不清空JS/native累计列表。A/B的实时内容是否符合baseline由普通控制器判断，子程序不猜测side。任何B非空或不可信事件都应永久使该case失败，即使随后的DOM采样恢复为空也不能通过。

close阶段必须通过原生ExecuteScript回执明确读取只读的 `collectPasteEvidence()`，核对同文档nonce/operation与全部事件；尚在native消息队列的真实事件可从这个实际DOM快照补齐，之后的相同回调去重，冲突失败。此读取不修改结果、监听器或clipboard。未读到尾部、超时、计数不一致或释放期间新事件均不能默认“空”：最终 `paste_capture_complete` 必须明确为true，`paste_final_snapshot_observed` 必须为true。终态摘要为 `paste_event_count`、`paste_nonempty_event_count`、`paste_untrusted_event_count`、`paste_event_digest_sha256`；摘要算法为按事件序号将 `index|trustedBit|emptyBit|textHash\n` 连接后对UTF-8字节做SHA256，含最后LF，无事件时为SHA256空字节。父程序须以全部实时事件重新计算并与终态核对。

浏览器正常关闭后，父程序须确认精确Normal事件、SDK0和全部原始进程句柄退出，再向collector发 `final`；collector要求sample1–10齐全、逐项期望及最终native状态全通过，随后自然退出。父程序还须持有collector原始句柄确认退出。缺正控、超时、崩溃、读取不稳定、未执行探针或清理不明都不是隔离通过。

事件字段固定：`controller_ready` 继承上一浏览器机制协议；`page_ready{operation,loaded,browser_pid}`；`operation_complete{operation,activation_verified,exec_command_result?,cut_selection_empty?,keyboard_verified?,prepare_verified?}`；`paste_sample{sample_index,value_sha256,value_empty,complete,paste_events:[{trusted,text_sha256,text_empty}]}`；`seed_complete{baseline_hash,sequence_before,seed_readback_matches}`；`collector_sample{sample_index,sequence,value_sha256,value_empty,baseline_matches,expected,read_stable,desktop_window_pids}`；`collector_final{passed,side,operation,sequence_before,sequence_after,baseline_hash,final_value_sha256,unchanged,write_marker_matches,sample_count,all_samples_expected,browser_exit_proof:"parent_required"}`。

`host_complete.page_succeeded` 表示真实页面已就绪；新增 `operation_succeeded` 表示真实输入刺激和本地完成已验证。它不代表clipboard隔离通过，六case还需独立collector与普通控制器全部断言。

同一次close阶段快照还显式读取最终DOM值，终态字段为 `paste_final_value_sha256` 与 `paste_final_value_empty`；未成功观测时均为null，不得当空通过。A须与native-only baseline摘要一致，B须明确为空且摘要等于空UTF-8的SHA256。最终DOM状态不能覆盖先前累计的非空事件事实。

当前仅编译，GUI/clipboard/AppContainer运行均为 `not_run`。首轮WFO1000编译失败保留；修复private setter后，锁定恢复及Release预编译0警告0错误，见 `compile-preflight-02.log`；holder增量见03，实时paste证据增量的后续真实编译另记。最终构建/源码/EXE摘要和真实运行另记，不能从编译结果推导安全成绩。
# 2026-10-30 最小READ_CONTROL对照模式补充

新 `clipboard_readcontrol_sixcase` 模式由普通controller在任何测试子进程创建前，只将本次新私有station的精确package ACE从0x23改为0x20023；holder仍仅以初始0x23创建并持有新站，高权限代码不变。新增的只有READ_CONTROL，desktop仍0x87/Low，未授clipboard、ENUMERATE或READSCREEN。每次创建、加载、输入刺激与native采样前均回读原对象边界，全部实际browser/collector原句柄退出、Job0、stdout EOF和资源退休后恢复初始DACL哈希，再清理profile/lease。

依据是已执行原生七组case-d908454c7e6f4600aca98998eb18da70：B23前后都USER321114，B20023即三DLL/绑定/exit0成功。该原生结论不能外推本probe已通过；新模式的真实browser前置及六case尚not_run，新UAC必须另行授权。下文0x23表示旧模式及holder初始权限，不能把旧初始化失败报告改成成功。

