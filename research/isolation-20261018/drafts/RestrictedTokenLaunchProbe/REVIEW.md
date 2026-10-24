# RestrictedTokenLaunchProbe：待授权的最小机制试验

制作人：派蒙。日期：2026-10-18。此目录是本地试验草案，不属于产品发布内容。

**当前仅源码检查与编译；候选 EXE 从未执行。A/B、受限 token 创建、受限 child 启动、窗口站访问检查均为 `not_run`。运行需要本轮另行明确授权，之前的私有窗口站 UAC 许可不复用。**

## 为什么要试

已完成的 `clipboard-station-uac-02/report.json` 证明，Job UI restriction 6 仍允许浏览器 copy/cut 清空合成剪贴板。该失败不删除、不降低断言，不集成 Job6 为完整剪贴板边界。另一个只读句柄试验证明，普通 token 可同时重新打开 WinSta0 的 ACCESSCLIPBOARD 权限；单独继承减权句柄不足以证明限制不可绕过。

本候选只回答：当前普通账号能否用自身的 restricted primary token 创建一个固定的受控 child，并由 child 实际读回限制与窗口站访问元数据。它不加载第三方应用，不启动 WebView，不验收剪贴板隔离。

## 精确范围与影响

- 普通 Medium、非提升 parent。原 token 只申请 QUERY、DUPLICATE、ASSIGN_PRIMARY 句柄权限，不修改原 token，不 impersonate。
- A：`CreateProcessW` 启动同目录同一 apphost，要求 CLR 执行、自读 token、回传正确 nonce/PID、自然退出；A 失败则不开始 B。
- B：`CreateRestrictedToken`，仅 `DISABLE_MAX_PRIVILEGE = 0x1`；仅 restricting SID `RestrictedCode (S-1-5-12)`，属性 `0`。没有 deny-only SID 列表；没有额外 privilege 列表；不使用 SANDBOX_INERT、WRITE_RESTRICTED、Everyone 后备或静默减弱。
- 创建 token 后检查它仍为相同账号/会话的 Medium primary，非提升，确有唯一该限制 SID；启用的 privilege 仅允许 SeChangeNotifyPrivilege。原 token 已有约束导致 SID 交集不符合预期时停止。
- `CreateProcessAsUserW` 创建同一固定 child。child 再自读以上事实，parent 结合匿名管道 nonce、实际 native PID、退出码验证执行完成。创建 API 返回成功本身不算试验通过。
- child 仅查询当前 station 名称，并分别以 READATTRIBUTES `2`、ACCESSCLIPBOARD `4` 调用 `OpenWindowStation("WinSta0", false, mask)`，通过 `NtQueryObject` 查询实际 granted mask 后关闭自己打开的句柄。打开权限句柄不读取、写入或清空剪贴板。
- 全部继承句柄限制到两个新匿名 pipe 端点；不传 station/desktop 句柄。`lpDesktop = NULL`，不指定或更换当前 station/desktop。只设置自身的无错误对话框模式与 child `CREATE_NO_WINDOW`。
- 不调用任何 Clipboard API、SetProcessWindowStation、SetThreadDesktop、CreateWindowStation、DACL 写入、AdjustTokenPrivileges、UAC、凭据登录、进程/Job 强制结束；不初始化 GUI/COM/浏览器。不读用户文件、账号令牌或环境变量值到报告。
- child 会加载此工具及项目内 .NET 运行时；限制 SID 可能令 DLL/运行时/desktop 访问失败。这是需保留的机制结果，不会通过修改文件 ACL 或窗口站 ACL 修复。
- 该试验在现有执行环境内运行，继承已有 Job 等限制；不会 breakaway。结果不能直接泛化到 Explorer、不同 Windows 版本或产品 WebView 进程。

## 失败处理与结果解释

`CreateProcessAsUser` 的 `5`、`1314` 等实际错误保留为 native launch failure，不改 API、不提权、不放松限制重试。若 native 创建成功但 child 未进入 CLR/未完成 metadata，则分别记录 `api_started=true` 与失败 stage/退出码；不能归为“受限 child 已可运行”。每个 child 最多等待 45 秒自然退出，输出最多额外等待 5 秒；超时停止后续 case、保留 PID，不强杀。parent 只关闭自身句柄，不保证异常 child 已结束。

只有 A/B 均真实完成且 token 符合预期，顶层机制状态才为 `passed`。其中 WinSta0 ACCESSCLIPBOARD 打开**允许或拒绝都只属于观测数据**；若仍允许，不能称剪贴板边界成立。所有 WebView、legacy copy/cut/paste、异步 clipboard、窗口站脱离与跨进程能力测试明确 `not_run`。即使访问句柄被拒绝，也没有证明已继承句柄、代理或浏览器其它进程不存在旁路。

缺失 child case 为 `not_run`；已执行失败保留 `failed`。不以超时或缺少回传作为限制成功。失败报告不覆盖；`--report` 指向已存在文件时拒绝开始。

## 构建与唯一待授权运行入口

编译（已执行；详见 `build.log` 与 `manifest.json`）：

```powershell
& '<WORKSPACE>\.tools\dotnet\dotnet.exe' build '<WORKSPACE>\artifacts\isolation-20261018\drafts\RestrictedTokenLaunchProbe\RestrictedTokenLaunchProbe.csproj' -c Release -p:Platform=x64 --no-restore
```

以下命令**未执行**。集成负责人获本轮授权后，在普通权限 PowerShell 中设置仅当前进程的 DOTNET_ROOT，并用一个全新报告路径执行一次；不设置系统 PATH，不使用 RunAs：

```powershell
$oldDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', '<WORKSPACE>\.tools\dotnet', 'Process')
    & '<WORKSPACE>\artifacts\isolation-20261018\drafts\RestrictedTokenLaunchProbe\bin\x64\Release\net10.0-windows\win-x64\AutumnOS.RestrictedTokenLaunchProbe.exe' --report '<WORKSPACE>\artifacts\isolation-20261018\restricted-token-launch-01\report.json'
    $probeExitCode = $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $oldDotnetRoot, 'Process')
}
```

进程影响限于该工具与至多两个顺序执行的自有 console child。无 UAC、无新窗口站、无全局配置。若后续要把受限 token 用于实际 browser host，仍需独立设计和授权：SDK 启动/IPC、渲染与输入、用户 profile 权限、浏览器子进程实际 token、完整剪贴板 A/B，以及 AppContainer/Chromium 沙箱兼容性均未在本候选覆盖。

## 官方依据

- `CreateProcessAsUser` 要求 token handle 含 QUERY、DUPLICATE、ASSIGN_PRIMARY；自身 restricted primary 的 assign-primary 特权要求有例外，但其它启动要求与权限错误仍须实测。[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)
- `CreateRestrictedToken` 返回相同类型及 handle access，限制 SID 参数属性应为零；既有限制 SID 使用交集，限制 privilege 不等于取消所有访问。[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)
- 限制 token 的访问检查需要普通 SID 与限制 SID 两次均允许；它不自动保证完整应用隔离。官方同时要求真正受限应用使用非默认 desktop，防止窗口消息通道；本候选只运行固定自有 child，不发送窗口消息、不加载不可信内容，不能直接推广成实际应用宿主。[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens)
- HANDLE_LIST 指定继承句柄，要求这些句柄可继承且 bInheritHandles 为 TRUE。[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- OpenWindowStation 的 access mask 与继承标志属于句柄打开操作；本候选不把句柄打开误写成剪贴板内容访问。[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openwindowstationw)
