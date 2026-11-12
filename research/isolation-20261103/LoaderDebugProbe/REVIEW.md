# Owned LPAC native loader debug capture

制作人：派蒙。2026-11-03。只捕获固定自有LPAC console probe的原生debug事件；不改产品，不启动第三方，不读取或写入进程内存/寄存器/PEB，不启用loader snaps、gflags或注册表配置。

CLI只接受四个参数，且目标必须是工作根固定路径：

```text
--report <新绝对artifacts报告.json> --executable <workspace>/tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe
```

工作根从当前目录确定。目标最终handle路径、产品metadata和实际EXE摘要均核验；不接受任意argv、shell或其他EXE。目标参数固定为新独立LPAC报告和 `--baseline synthetic-lua-medium`。

仅接受未受限High管理员父token，用既有LuaTokenProbe helper创建flags5独立副本，将副本降Medium，再仅修复副本的私有新对象DefaultDacl；核验Owner=user、管理员禁用、唯一SeChangeNotify、HasRestrictions=true、IsTokenRestricted=false、相同用户/会话/authentication ID。DefaultDacl必须非null、revision2、恰两条无继承Allow ACE，当前user和LocalSystem的mask均0x10000000。真实child也读回完整token与同样DefaultDacl条件。它是明确的合成基线，不是实际UAC Limited用户。

链接已验证的新Low desktop创建器，其DACL和Low/NW label仍严格读回。parent不切换desktop，不改既有ACL。创建固定child采用 `DEBUG_ONLY_THIS_PROCESS | CREATE_UNICODE_ENVIRONMENT (0x402)`；创建成功建立debug连接后，立即在同一创建线程调用 `DebugSetProcessKillOnExit(false)`，首次Wait之前完成，不提前调用。所有Wait和Continue均在这个同步线程执行。

捕获CREATE_PROCESS、CREATE_THREAD、LOAD_DLL、UNLOAD_DLL、EXCEPTION、OUTPUT_DEBUG_STRING、EXIT_THREAD、EXIT_PROCESS及RIP元数据，只记录该自有PID。DLL/EXE路径从debug事件hFile查询，转换成workspace/windows相对路径；外部路径只记文件名和完整路径SHA。OUTPUT_DEBUG_STRING只记长度和Unicode标志，绝不为取内容读取目标内存。

第一个自有first-chance `0x80000003` 为初始breakpoint并Continue；其余真实exceptions均 `DBG_EXCEPTION_NOT_HANDLED`，不吞掉程序故障。每个已接收事件均在finally中Continue。捕获期限60秒、最多4096事件；达到上限先继续未决事件，再detach并记incomplete。若系统拒绝detach，会留在同一child的继续/脱离恢复循环中，直到detach或自然exit，不以退出debugger遗弃暂停进程，不强杀，不启动后续进程。Kill-on-debugger-exit提前关闭。

按微软debug事件生命周期，EXE/DLL文件handle由本工具处理事件时关闭；CREATE_PROCESS/CREATE_THREAD事件hProcess/hThread由系统在continued exit/detach释放，不能重复关闭。CreateProcess返回的调用者PROCESS_INFORMATION句柄独立关闭；若句柄值与系统拥有的事件句柄重合则避免重复关闭。源token、副本和自有HDESK均关闭；不声称关闭parent desktop handle就证明对象已销毁。

x64布局在执行前严格断言并写入报告：DEBUG_EVENT=176、EXCEPTION_DEBUG_INFO=160、union偏移16、exception address32、first-chance168、LOAD_DLL hFile16/base24、CREATE_PROCESS file16/process24/thread32/base40。防止以错误union布局解析并关闭句柄。

`native_capture_completed` 仅表示收到并继续了EXIT_PROCESS且确认自然退出；它不要求child成功，也不代表机制隔离通过。报告单独保留原退出码与 `0xC0000142` 分类。hostfxr/hostpolicy/coreclr的LOAD_DLL事件只证明模块映射被观察到，不能证明DLL初始化成功或managed入口已执行；debugger本身也改变执行环境。

本工具已Release编译0 warnings / 0 errors，未实际运行。先由集成负责人审阅，再使用全新报告路径执行。

2026-11-04新增显式 `--no-debug`（仅可放在上述四参数末尾），默认debug路径不变。该诊断保留同一flags5/Medium副本、新Low desktop、固定LPAC目标和普通STARTUPINFO，仅移除DEBUG_ONLY_THIS_PROCESS，创建flags为 `CREATE_UNICODE_ENVIRONMENT (0x400)`。handle继承false，无stdout pipe，无CREATE_NO_WINDOW，无desktop切换；不调用任何debug连接API，凭native PID/实际token、新独立child报告路径和自然退出记录结果。它用来区分debugger影响与旧STARTUPINFOEX/stdio/creationflags差异；不泛化为产品启动器，不作为产品UI、真实普通用户或剪贴板验收。

2026-11-04DefaultDacl集成依据：原flags5副本继承的High默认DACL缺少当前user写权限，而管理员组已禁用，no-debug02的CreatePipe返回5；主代理已有独立内存实跑证明 `SetCopyPrivateObjectDefaultDacl` 仅修改该副本，原token全量事实不变。本工具SetMedium后立即调用该方法，报告 `copied_default_dacl_repaired`，将修复前Medium快照与修复后selected/实际child事实分开，父tokenbefore/after全量比较继续。没有改变任何既有对象ACL，也没有删改旧初始化/pipe失败报告；新启动结果以主代理后续实际运行记录为准。

最小创建flag对照可在 `--no-debug` 后再加 `--no-window`，只与no-debug搭配；它仅增加 `CREATE_NO_WINDOW`，最终flag `0x08000400`。普通STARTUPINFO、无pipe、inherit=false、同一新Low desktop及修复后的token保持一致。默认debug/no-debug原路径不变，不自动执行此对照。

官方依据：[Debugging events](https://learn.microsoft.com/en-us/windows/win32/debug/debugging-events)、[CREATE_PROCESS_DEBUG_INFO](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-create_process_debug_info)、[DebugActiveProcessStop](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-debugactiveprocessstop)。
