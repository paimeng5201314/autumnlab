# Explicit synthetic LUA probe launcher

制作人：派蒙。2026-11-03。

这是独立的机制实验入口。它不会成为 `StandardUserLauncher` 的后备路径，也不会改变产品或其他探针的默认放行条件。它显式创建一份 LUA token 副本，把该副本降到 Medium，并仅修复副本中新建私有对象的DefaultDacl，用于本机没有真实 linked Medium token 时的自有机制诊断。

**该 token 不是实际 UAC Limited 用户，不是干净机器上的普通用户环境，不能用此工具验收产品 UI 或宣称普通用户产品测试通过。** 即使目标返回0，报告也只会写 `passed_synthetic_probe_launch_only`；真实普通用户和产品 UI 验收固定为 `not_run`。

## 依据与固定策略

本轮主代理已有真实的内存机制证据 `artifacts/isolation-20261103/lua-token-handles-high-01/report.json`：原调用者为未受限High；`CreateRestrictedToken(flags=5)` 独立副本再降Medium后，读回非提升、管理员组deny-only、Owner=user、唯一 privilege 为 SeChangeNotify、`IsTokenRestricted=false` 且 `TokenHasRestrictions=true`；原token事实未变。此报告只覆盖内存token，并不证明新child、浏览器或剪贴板边界。

本入口只接受未受限High管理员primary父token，不接受已有Medium或其他身份来自动选择行为。源token handle权限固定 `0x8b`。随后：

1. 用 `LUA_TOKEN | DISABLE_MAX_PRIVILEGE = 5`、零 restricting SID 创建独立副本。没有 SANDBOX_INERT、WRITE_RESTRICTED、账号/全局/ACL变更。
2. 在副本上调用现有 `LuaTokenProbe.Native.SetMedium`，再调用 `SetCopyPrivateObjectDefaultDacl`；原token从不传给 SetTokenInformation。后者只为该副本后续创建的新对象设置恰两条无继承Allow ACE：当前user GA、LocalSystem GA；严格二进制和事实读回，不改任何既有对象ACL。
3. 完整读回：同一用户、会话和authentication ID；primary、Default elevation type、非elevated、Medium、`HasRestrictions=true`、`IsTokenRestricted=false`、无restricting SID、Owner=user、管理员deny-only、仅SeChangeNotify且启用；没有UIAccess/SandboxInert/AppContainer，mandatory policy与原token相同。
4. `CreateProcessAsUser` 明确启动获授权的自有机制目标。创建失败直接记录原生错误，不改API、不提权、不放宽token条件。
5. 从真实child process handle读取同样完整事实，等待其自然退出；只继承两个新匿名pipe句柄。超时保留PID和失败状态，不强杀。
6. 关闭副本后读回原token，报告全部事实是否与创建前完全一致。变化不被隐藏，也不改写为测试通过。

## 允许目标

- `research/` 中名称为 `AutumnOS.*.exe` / `.dll`、且产品metadata为 `Lab Chronicles AutumnOS` 的自有探针。
- 唯一tools例外是 `tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe`（及同一输出目录中的同名DLL）。
- 固定项目dotnet只允许运行上述明确DLL。
- 项目固定pwsh或既有本机可信pwsh路径，仅接受 `-NoLogo -NoProfile -NonInteractive -File <绝对路径>`。脚本限research下脚本，以及 `scripts/Test-ExecutionContext.ps1`、`scripts/Test-IsolationMechanisms.ps1`、`scripts/Test.ps1` 三项非产品UI入口；禁止 `-Command` / `-EncodedCommand`。

没有src产品apphost、artifacts产品入口或其他tools二进制例外。路径/argv/文件读取lease、最终路径核验、SHA-256、报告路径核验和有界stdout/stderr收集沿用已审阅的StandardUserLauncher实现；产品metadata只是sanity check，不构成签名信任。待执行脚本的具体行为仍需由调用者审阅授权。

## 显式新desktop初始化试验

首次真实合成LUA调用在 `artifacts/isolation-20261103/lpac-synthetic-01/lpac-launcher.json` 保留：进程创建、父/child token核验和自然退出均实际完成，但目标在CLR进入前以 `0xC0000142` 失败，没有输出或probe报告。该结果不定位具体DLL、desktop或权限根因。

新增可选开关 `--private-desktop`（放在launcher参数的 `--` 分隔符之前）；不提供时保持原desktop路径。它只检验“显式新desktop是否改变初始化结果”这个诊断假设：

- 本次以随机名称创建一个新desktop，创建参数SDDL仅授当前user full access，以及 `ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2)` 的 `READ_CONTROL | 0xff`；后者不含SwitchDesktop、WriteDac、WriteOwner。
- 同一创建descriptor包含 `S:AI(ML;;NW;;;LW)`：新desktop的Low mandatory label，No-Write-Up，明确其SACL auto-inherited标志。不修改WinSta0、Default或任何既有对象ACL/label，不调用SetThreadDesktop/SetProcessWindowStation/SwitchDesktop。
- 创建后用 `GetSecurityInfo` 的OWNER/GROUP/DACL/LABEL读取native descriptor，并与创建descriptor的native SDDL精确比较；再独立读取mandatory label ACE，要求仅Low SID `S-1-16-4096`、NW mask1，核对实际名称。任何读回失败都拒绝启动child。
- child的 `STARTUPINFO.lpDesktop` 显式指定当前station下该新desktop；token限制保持原样。parent关闭自己创建的HDESK并读回父关联是否未变；关闭parent handle不等于证明对象销毁。若child仍存活或退出未确认，报告明确保留该状态。

报告增加 `child_exit_hex`、`child_failure_class`、`child_output_empty`。`0xC0000142` 标为 `native_init_failed`，不以空输出导致的JSON异常覆盖原生初始化失败。本补充构建尚未实际执行。

`lpac-private-synthetic-01`/`02` 失败报告均保留。private02的脱敏descriptor读回已定位严格比较差异：owner/group、ARAP `0x200ff` ACE及唯一Low/NW label完全一致；Windows将owner的通用 `GA (0x10000000)` 映射成 `DESKTOP_ALL_ACCESS (0x000F01FF)`，并使SACL auto-inherited为true，control由 `0x9014` 到 `0x9814`；DACL仍为protected。当前创建输入将owner mask直接写成 `0x000F01FF`，SACL明确写成 `S:AI`，与已经观察到的Windows语义一致。依然要求native SDDL严格相等、owner/group、两条且仅两条ACE、精确mask、DACL保护和唯一Low/NW label读回；没有新增grant、SetSecurityInfo或容忍任意ACL差异。此输入修正只完成编译，待主代理以新报告路径验证。

## 构建与调用

已完成Release编译，0 warnings / 0 errors；写入本说明时未运行此入口或LPAC探针。

```powershell
& ./.tools/dotnet/dotnet.exe build ./research/isolation-20261103/SyntheticLuaLauncher/SyntheticLuaLauncher.csproj -c Release -p:Platform=x64 --no-restore
```

示例（路径均需完整绝对值，两个报告必须使用新路径；LPAC参数以当前探针实际CLI为准）：

```powershell
$workspace = $PWD.Path
$env:DOTNET_ROOT = Join-Path $workspace '.tools/dotnet'
$launcher = Join-Path $workspace 'research/isolation-20261103/SyntheticLuaLauncher/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.SyntheticLuaLauncher.exe'
$probe = Join-Path $workspace 'tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe'
& $launcher --workspace $workspace --report (Join-Path $workspace 'artifacts/isolation-20261103/lua-lpac-01/launcher.json') --executable $probe --timeout-seconds 120 -- --report (Join-Path $workspace 'artifacts/isolation-20261103/lua-lpac-01/probe.json') --baseline synthetic-lua-medium
```

明确执行新desktop假设试验时，在 `--timeout-seconds 120` 后、`--` 前加入 `--private-desktop`，并使用全新的报告路径。

`--baseline synthetic-lua-medium` 是LPAC探针的明确报告标签，不会将合成token转换为真实UAC Limited用户，也不修改安全断言。本launcher不会自行添加或删减目标参数。

该工具不授权第三方应用执行，不代表剪贴板完整隔离已经成立，不改变正式WebAppHost在边界不可用时的拒绝行为。新desktop/LPAC/浏览器等目标机制的独立结果仍必须按其实际报告记录。

2026-11-04集成DefaultDacl修复：既有 `token-default-dacl-01/report.json` 记录flags5副本仍继承High默认DACL（Admins GA/System GA/logon GRGX），而Admins已deny-only，缺少当前user写权限；no-debug02在CLR进入后创建pipe返回5。主代理已实际运行 `lua-default-dacl-repair-01/report.json`，证明仅副本DefaultDacl修复后恰user+System两条GA，原token全量事实未变。本入口现明确调用该已验证方法，且selected/实际child的baseline验证要求非null、revision2、恰两条type0/flags0/mask0x10000000，第一条user hash、第二条S-1-5-18。报告将未修复的 `copy_after_medium_before_default_dacl_repair` 与修复后的 `selected_synthetic_token` 分开，并写 `copied_default_dacl_repaired`；父before/after全量比较保持。旧C142、pipe错误和失败报告不改写，本次集成对实际启动结果的影响由主代理新运行记录判定。
