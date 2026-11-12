# Restricted desktop console launch diagnostic

制作人：派蒙。2026-11-03。

此探针用于接续仓库缺失的 `RestrictedDesktopLaunchProbe` 草案，不能代替当时未公开源码，也不能复用其构建或测试成绩。本版只测试自有 console child 的初始化、实际 token、启动 desktop 和对象句柄权限；不启动 WebView2，不触碰剪贴板内容，不加载第三方代码，不改变产品放行条件。

## 假设和范围

历史 `RestrictedTokenLaunchProbe` 的 A 正控完成，但 B 在 `CreateProcessAsUser` 返回成功后以 `0xC0000142` 退出，没有探针 JSON。这只能证明初始化失败，不能定位具体 DLL、运行时或 desktop 的访问失败。

本探针将 startup desktop 显式指定到一个新随机命名的 desktop，区分默认 desktop 的权限约束。parent 必须为普通、未提升、未受限的 Medium primary token；不满足则记录失败，不修改或替换调用者身份。

- 通过 `CreateDesktop` 的创建参数为新对象设置受保护 DACL。只给当前用户 full access、给随机合成 restricting SID `READ_CONTROL | 0xff`；后者不含 `DESKTOP_SWITCHDESKTOP`。不写 WinSta0、Default 或其他既有对象的 ACL。
- A 以原 token 执行同一 apphost，B 用 `DISABLE_MAX_PRIVILEGE | WRITE_RESTRICTED`（9）和唯一合成 restricting SID。B 的实际 token 必须保持相同用户、会话、Medium primary，且只有预期限制 SID、无额外启用 privilege。
- 两个 child 都从 `STARTUPINFO.lpDesktop = WinSta0\\<new desktop>` 启动，只继承两个新匿名管道句柄；没有继承 station/desktop handle，不使用 breakaway、UAC、impersonation 或强杀。
- child 必须回传正确 nonce、case、native PID、parent PID、station、desktop、token 和句柄元数据，并自然退出。A 还必须能打开 WinSta0 `ACCESSCLIPBOARD` 和 Default `CREATEWINDOW` 句柄，形成正控。
- B 必须实际完成 CLR，再验证 WinSta0 `ACCESSCLIPBOARD` 与 Default `CREATEWINDOW` 的独立打开均以 `ERROR_ACCESS_DENIED` 失败，同时新 desktop 和 station readattributes 句柄可打开。打开句柄不调用 Open/Get/Set/EmptyClipboard。
- parent 与每个 child 的等待上限为30秒；退出后读输出再等最多5秒。超时保留 PID 与失败状态，不强杀、不启动后续 case。parent 关闭自己创建/打开的 handle；child 未退出时对象仍可能被其引用，不能将关闭 parent handle 说成全对象已销毁。

`WRITE_RESTRICTED` 对读取不实施 restricting SID 的第二次检查，**本探针不是完整文件系统、账号数据或应用沙箱**。即便 console 机制通过，仍不能推导 WebView2 renderer sandbox 兼容性、内部呈现、可信输入、浏览器子进程限制继承或 legacy copy/cut/paste 边界成立。真实浏览器可能在该 token 下初始化失败，这个结果必须单独记录，不能放宽限制来放行应用。

## 文件和执行

- `Program.cs`：本版 desktop/A/B逻辑。
- `RestrictedDesktopLaunchProbe.csproj`：链接历史 token/pipe helper 源码，指定本版唯一启动入口；没有项目/产品引用，没有更改历史探针。
- `Run-MechanismProbe.ps1`：只运行已经编译的探针，显式要求一个全新的报告目录。

编译已经执行，.NET SDK `10.0.401`，Release 0 warnings / 0 errors。仅编译不等于机制通过；本文件写入时未运行 A/B。

从工程根编译：

```powershell
& ./.tools/dotnet/dotnet.exe build ./research/isolation-20261103/RestrictedDesktopLaunchProbe/RestrictedDesktopLaunchProbe.csproj -c Release -p:Platform=x64 --no-restore
```

由集成负责人决定实际执行时，使用普通用户权限；此命令会创建新 desktop 和至多两个顺序执行的自有 console child：

```powershell
& ./research/isolation-20261103/RestrictedDesktopLaunchProbe/Run-MechanismProbe.ps1 -ReportDirectory ./artifacts/isolation-20261103/restricted-desktop-01
```

报告 `passed_mechanism_only` 仅表示上述 console 元数据和句柄断言均成立。任意进程创建错误、初始化退出码、缺少 JSON、nonce/token不匹配或正控失败均为 failed。`clipboard_isolation_acceptance`、`browser`、`user_interface` 始终保持 `not_run`。

2026-11-03 诊断修正：child输出在读取时最多保留64 KiB并继续排空，超限即失败；不先无界 `ReadToEndAsync` 再检查长度。记录实际 wait result；`WAIT_FAILED` 与其Win32错误、`WAIT_TIMEOUT`、意外结果分别分类。自然退出 `0xC0000142` 直接记录 `CHILD_NATIVE_INITIALIZATION_FAILED` 和原退出码，不尝试把空stdout解析成JSON；其他非零退出也保留原生失败为主诊断。可选错误JSON不能覆盖该主诊断。退出0但无输出单独记 `CHILD_EXITED_ZERO_WITHOUT_METADATA`。本补充仅改善诊断，不更改token/desktop策略或机制验收条件。

## 官方依据

- [Restricted tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens)：限制 SID 的第二次访问检查，以及受限应用的独立 desktop 要求。
- [CreateRestrictedToken](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)：`WRITE_RESTRICTED` 的范围。
- [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)：primary token 与 startup desktop。
- [Desktop security](https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights)、[Window station security](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)：两类对象的独立权限。
