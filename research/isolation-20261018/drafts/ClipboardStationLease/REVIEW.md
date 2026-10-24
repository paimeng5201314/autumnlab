# 单次私有 Window Station 验证辅助器（草案）

制作人：派蒙。此目录是本地验证辅助器，不是 AutumnOS 产品功能。2026-10-18 用户已授权本次 UAC 验证；由集成负责人统一启动一次，子代理没有启动它，也不自动重试 UAC。

## 为什么需要这一步

普通权限调用 `CreateWindowStation(随机名称, CWF_CREATE_ONLY)` 返回 5；NULL 自动名称加 CREATE_ONLY 返回 183，且实际返回句柄为 NULL。没有访问任何剪贴板。API 文档明确只有管理员可指定名称，NULL 名称由登录会话生成；不能去掉 CREATE_ONLY 后复用未知共享 station。新的 desktop 仍共享所属 station 的剪贴板，不能用它保护真实剪贴板。

`browser-job-02/report.json` 已真实证明当前 Windows/WebView2 的新浏览器进程能够加入 UI flags=6 的自建 Job、回读限制、渲染新 origin/WASM42 并正常退出；这是兼容性通过，不是剪贴板 A/B 通过。原先根据既有 Job 一律预拒绝的判断不足，不能继续引用它证明当前环境不可行。

## 请求及影响

正常权限 broker 仅请求一次 Windows UAC，启动本目录同一 EXE 的 `--hold` 分支。高权限分支仅创建一个 `AutumnOS_ClipboardProbe_<随机128位nonce>` 全新非交互 window station，校验后持有句柄。它不读取或写入剪贴板，不接收文件路径，不启动子进程，不写报告，不切换用户桌面，不设置全局安全策略，不创建账号/登录会话。

新 station 使用显式安全描述符：owner=发起人的 Windows SID；受保护 DACL 只有该 SID 和 SYSTEM 两条无继承 allow ACE，掩码 `0x000F037F`；mandatory label 为 Medium / NoWriteUp。这些是新对象自己的属性，不修改现存对象 ACL。helper 从内核回读名称、nonvisible、owner、DACL 与 label，任何不符则拒绝。普通 probe 再独立核验并创建随机 private desktop，验证浏览器确实关联该 desktop 后，才允许对该 station 的合成标记做 A/B。不访问 WinSta0 剪贴板。

只有辅助器提权；broker、探针、WebView2 和产品均保持普通 Medium 权限。若 UAC 要求使用另一个管理员账号，helper 会因 SID 不同拒绝；不要提供其他账号或绕过检查。

普通 broker 创建一次性随机命名 pipe，明确只授权同 SID/SYSTEM。两端核验 pipe 的真实 peer PID、同 SID/session、期望完整性和同 EXE 路径。进程身份仅以 QUERY_LIMITED_INFORMATION 句柄固定，没有终止或进程写权限；报文有 nonce 与长度上限。helper 不接收运行命令，也没有通用 RPC。`CurrentUserOnly` 不能用于此处，因为 .NET 在 Windows 也比较 elevation level，会拒绝目标中的高权限同用户连接。

完成后普通 probe 正常退出，broker 请求 release，helper 关闭 station 句柄并退出。连接断开或 10 分钟租约到期时 helper 同样退出；不强杀任何进程。若探针尚未正常结束，其已有对象句柄会存活到它自行退出；不得把 helper 退出说成自动清除了仍存活的探针。不会创建系统服务、计划任务、持久登录、全局策略或持久 station。

## 构建与当前验证状态

本地 .NET 10 self-contained publish 已完成（未打单文件，避免高权限单文件原生库解压到临时路径）；程序来源、完整 publish 文件哈希见同目录 `source-and-publish-manifest.json`。`--broker` / `--hold` 尚未由此子代理执行；创建 station、跨完整性 pipe、Medium label 回读及 A/B 全部等待集成负责人的本次运行，不能把编译通过写成通过。

辅助器源文件仅 `Program.cs`，无 NuGet package references。EXE 所在完整 publish 目录应一起使用，避免依赖提升后的环境能找到项目内 .NET。执行前集成负责人应按 manifest 复核实际字节并保留 stdout/stderr；失败不自动再次请求 UAC。

普通 broker 参数：

```text
AutumnOS.ClipboardStationLease.DRAFT.exe --broker --probe <ClipboardStationProbe.exe绝对路径> --runtime <固定WebView2目录绝对路径> --report <全新报告绝对路径>
```

broker 传给 probe 的租约参数固定为 `--leased-station <name> --lease-nonce <nonce> --lease-helper-pid <已认证helperPID>`。JSON 报告不是授权凭据；probe 按内核对象和活进程验证。

## 官方依据

- [CreateWindowStationW 名称权限、CREATE_ONLY、返回值](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)
- [Window stations 及剪贴板所属对象](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)
- [Window station security and access rights](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)
- [.NET PipeOptions.CurrentUserOnly 同时比较用户与提升级别](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)
- [NamedPipeServerStreamAcl.Create](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0)
- [JobObject UI clipboard restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_ui_restrictions)

官方 Job 文档描述的约束需与当前实际 API 返回和限制回读一起报告。这里不再仅凭“已属于某 Job”认定可行或不可行，也不声称抵御浏览器零日或已控制同用户/管理员的攻击者。
