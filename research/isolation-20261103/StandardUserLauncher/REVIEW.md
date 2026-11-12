# Existing ordinary-user token launcher

制作人：派蒙。2026-11-03。

此工具只解决本轮测试环境的调用身份问题：默认工具执行 token 为受限 Medium，而完整本机执行为 High；两者都不能满足隔离机制探针原有的普通、未受限 Medium 前提。launcher 不修改探针条件，不创建账号、输入凭据、弹出 UAC、修改或新建 token，也不更改全局配置。

## 允许的身份与启动方式

1. 已是普通、未提升、无 restricting SID 的 Medium primary token：用自身身份 `CreateProcess`。
2. 调用者为未受限的 UAC High/Full primary token：读取现有 `TokenLinkedToken`，必须严格验证同一 user/session、Medium、非提升、未受限、primary、Limited。使用这一既有 token 调用 `CreateProcessAsUser`。
3. 其他身份或 native API 拒绝：失败，不启用 privilege、不换 API、不提权、不减弱条件、不做 fallback。

子进程实际 token 再由 native process handle 查询，验证同一用户/会话及普通 Medium primary 条件。即便元数据查询失败，也继续有界等待该自有 child，自然退出；不因查询失败直接遗弃子进程。超时保留 PID、失败状态和已捕获输出，关闭 parent 自有 handle，不强杀。超时不保证子进程已经退出。

只继承两个新匿名管道 handle；station/desktop 使用调用者当前关联的明确名称，不传 station/desktop handle。工作目录固定为工作根。继承当前进程环境，不记录环境变量值。目标与参数通过 Win32 argv 编码直接传给 native 创建 API，没有 ShellExecute、命令字符串解释或 shell 拼接。

## 目标限制

所有目标均要求绝对路径。打开文件后用 `GetFinalPathNameByHandle` 校验最终路径；在 child 生命周期内持有禁止写入/替换的读取 lease。

- `src/`、`tools/`、`tests/`、`research/`、`artifacts/` 中名称为 `AutumnOS.*.exe` 或 `AutumnOS.exe`，且产品 metadata 为 `Lab Chronicles AutumnOS` 的自有二进制。
- `<workspace>/.tools/dotnet/dotnet.exe` 仅能运行以上目录中同样名称与 metadata 条件的明确 `.dll`，不接受 dotnet build、tool、exec 或任意 CLI 子命令。
- `<workspace>/.tools/pwsh-ci/pwsh.exe`，或固定本机路径 `C:\Users\admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe`，仅接受严格前缀 `-NoLogo -NoProfile -NonInteractive -File <绝对脚本路径>`。脚本必须在工作根 `scripts/` 或 `research/` 中，禁止 `-Command`、`-EncodedCommand`。这允许执行已授权脚本；脚本本身的行为仍必须由调用者审阅授权。

产品 metadata 仅为路径/文件类型的 sanity check，不是代码签名或可信身份认证。此工具不能用来证明脚本无副作用，也不能代替调用工具对具体测试动作的授权。

## 报告与状态

报告必须是工作根 `artifacts/` 下的新文件，禁止目录 junction/reparse 跳出目标根。输出日志同样 `CreateNew`，stdout/stderr合并，最多保留8 MiB；继续排空超过上限的输出，但将此次执行记失败。默认等待180秒，可显式设置1至900秒；退出后再等最多5秒收尾输出。原始 user SID 不进入报告，只保留既有 helper 产生的 SHA-256。

报告保存随机 launch ID、native PID、实际 child token、选用 token、目标 EXE 与脚本/程序集的实际 SHA-256、参数向量摘要、退出码及自然退出状态。launch ID 是报告关联标识，不声称构成目标程序的挑战应答认证。child 完成且 token 验证通过时，launcher 原样转发 child 退出码；非零 child 状态为 `child_failed`。启动失败、token不符、超时或输出不完整为 `failed`。

后续诊断补充：报告保存 `parent_elevation_type` 和 `linked_elevation_type`，数值1/2/3分别对应Default/Full/Limited。未受限High但类型不是Full时明确返回 `CALLER_HIGH_WITHOUT_LINKED_MEDIUM`；不猜测或查询不存在的Medium后备。`target_execution = not_run` 与 `target_not_run_reason` 明确表示目标根本没有被启动，不把launcher身份检查失败记成目标机制测试失败。早期实际运行报告没有此字段，不能据此猜测其精确elevation type；本次补充构建尚未运行。

编译通过不代表任何启动已经执行。本文件写入时只进行了 Release 编译，没有运行 launcher 或目标探针。

## 调用示例

从工程根，使用本机授权的完整执行环境；不要在工具拒绝某项操作后，借此包装绕过拒绝。当前环境若要求审批，审批应覆盖 launcher 和下列完整目标参数。

```powershell
$workspace = $PWD.Path
$launcher = Join-Path $workspace 'research/isolation-20261103/StandardUserLauncher/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.StandardUserLauncher.exe'
$probe = Join-Path $workspace 'research/isolation-20261103/RestrictedDesktopLaunchProbe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.RestrictedDesktopLaunchProbe.exe'
$env:DOTNET_ROOT = Join-Path $workspace '.tools/dotnet'
& $launcher --workspace $workspace --report (Join-Path $workspace 'artifacts/isolation-20261103/standard-launch-01/report.json') --executable $probe --timeout-seconds 120 -- --report (Join-Path $workspace 'artifacts/isolation-20261103/restricted-desktop-medium-01/report.json')
```

需要执行已经审阅的工作根脚本时，把 `--executable` 指向上述固定 PowerShell路径，并在 `--` 后传：

```text
-NoLogo -NoProfile -NonInteractive -File <绝对workspace/scripts或research脚本.ps1> <脚本参数...>
```

没有文件删除、token变更、ACL变更、凭据处理、UAC或进程终止 API。真实待执行脚本的权限变化或机制试验仍由其自身文档与具体授权界定。

## 官方依据

- [TOKEN_LINKED_TOKEN](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_linked_token)、[TOKEN_ELEVATION_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_elevation_type)：查询现有 linked token 与 UAC token类型。
- [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)：primary token、启动要求和权限错误；本工具不会自动启用额外 privilege。
- [Handle inheritance](https://learn.microsoft.com/en-us/windows/win32/procthread/inheritance)：仅继承明确创建的管道。
