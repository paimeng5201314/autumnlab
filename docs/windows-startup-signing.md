# Windows 启动拦截与签名

制作人：派蒙。此说明针对本地 Windows 原生客户端，不是第三方应用隔离验收。

2026-10-24 的真实故障：Windows CodeIntegrity 事件 3077/3033 记录旧根入口 `AutumnOS.exe` 及代码核验包内层 `AutumnOS.Client.exe` 被策略阻止。用户主动运行诊断脚本得到退出码 31。原 Bootstrap 将启动进程失败统一显示为 `UPDATE_BOOTSTRAP_FAILED`，还指向从未写入的 `.autumnos-update` 报告。这不能归因于 CLR 目录为空或用户存档损坏。

新启动处理保留失败阶段、固定目标文件名、异常类型、HResult、Win32 NativeErrorCode、构建 ID 与源快照；报告只追加到当前便携数据根的 `AutumnOS_Data/Logs/Startup/startup-<GUID>.json`。不会输出任意异常正文、环境变量、命令行或登录资料。只有文件实际写成才显示路径；写入失败时明确提示。Windows 在最外层入口运行前就阻止进程时，应用没有机会生成报告，应读取 Windows CodeIntegrity 事件。

577、1260、4551 映射 `STARTUP_WINDOWS_POLICY_BLOCKED`；5（权限不足）、2/3（文件缺失）、126（依赖缺失）和193（无效映像）分别报告。错误码本身不能证明具体由 SAC、组策略或其他应用控制发出；本次 SAC 结论同时依据用户截图和系统事件。代码不修改策略、自动提权、添加排除或重试绕过。

## 当前签名缺口

本机 CurrentUser/My 与 LocalMachine/My 未发现代码签名证书。已检查旧入口、新外层、内层 Bootstrap、Client、Updater，均为 NotSigned。新的代码核验包仍是未签名开发包，不保证在开启 SAC 的机器上运行，不能称为问题已经解除。

Windows Authenticode 发布者身份、AutumnOS 更新清单签名和 `.autumn` 包哈希是不同验证层，不能相互替代。需要受信提供商颁发的代码签名证书或已批准的签名服务；自签测试证书及本地更新测试公钥不满足这个交付结论。证书显示真实发布者，不把“制作人：派蒙”伪装成已验证证书身份。云端 Artifact Signing 涉及外部服务，不在当前全部本地、不上传的授权内。

## 打包顺序与格式

1. 构建内层 EXE/DLL；使用另行落实的受信签名身份签署自有程序二进制，核对第三方组件已有签名，按实际加载文件检查缺口。
2. 验证最终内层字节，再生成文件清单、安装 receipt、ZIP payload 与编译期 payload SHA256。签名改变字节，不能继续沿用签名前哈希。
3. 编译并组合外层入口，再签外层 EXE。单文件读取器解析 PE Security Directory，仅支持位于文件尾、结构有效的证书表；允许证书表前0–7字节零填充，并保留原 footer、offset/length、编译期及实际 payload SHA256 检查。无证书仍从 EOF 读取。不会全文件扫描寻找可用 footer，损坏证书目录拒绝加载。
4. 重新生成最终 EXE/ZIP 哈希，再验证签名、解包、本机实际启动和解压副本。结构解析不验证 PKCS#7 信任；Windows 策略接受与真实启动必须另行实测。

当前脚本只产出明确标注 `authenticode=not_signed` 的本地代码核验包，尚未执行以上正式签名流程。不能直接签现有旧包后声称可运行：旧解析器不兼容证书表，内层字节也缺少可信签名。未来签名管线必须基于修正后的源码并重新生成清单与产物；不得原地修改已交付哈希对应的包。

## 验证范围

错误处理测试使用明确的合成 Win32 异常与独立临时数据，检查正反分类、报告可读、脱敏、写入失败、已有合成存档保留。PE 测试使用合成证书表，覆盖 signed-layout/unsigned-layout、填充、截断、伪目录与 payload 边界；这不是受信签名或 SAC 验收。实际签名、SAC 下 GUI 启动仍 `not_run`。原 `codecheck-01` 启动为用户实测失败，不能被新代码测试覆盖。

这次启动修复不解除第三方运行门禁。剪贴板边界及完整网络验证缺口仍在，ChemLeap 普通模式仍不可运行，不改变其包、来源或已有存档。

依据：[Microsoft SAC 签名要求](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)、[SAC FAQ](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions)、[PE 格式](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)、[Win32 错误定义](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/shared/winerror.h)。
