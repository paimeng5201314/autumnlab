# RestrictedTokenAccessCheck：只读权限诊断

制作人：派蒙。2026-10-18。此草案不属于产品运行边界，也不启动受限应用。

集成负责人已复核源码并决定自行执行；本目录只记录可审阅源码/编译字节。实际运行结果单独保存在 `artifacts/isolation-20261018/restricted-token-accesscheck-01/report.json`。编译成功不等于该报告已完成，运行状态以报告为准。

## 精确操作

1. 要求普通 Medium、未受限的自身 primary token，且当前线程没有 impersonation token。
2. 仅在内存创建三份供 `AccessCheck` 查询的 token：A 为原 token 的 query-only impersonation copy；B 为 `DISABLE_MAX_PRIVILEGE (1)` + 唯一 RestrictedCode SID `S-1-5-12` 属性0；C 为同一条件再加 `WRITE_RESTRICTED (8)`，合计 flags9。三者都不会绑定到线程或进程，不启动 child。
3. 要求当前自身 window station/desktop 是 WinSta0/Default，读取现有对象的 OWNER、GROUP、DACL。只调用 GetProcessWindowStation/GetThreadDesktop/GetUserObjectInformation/GetUserObjectSecurity；不打开新 desktop、不设置关联、不写 ACL，不读取 SACL。
4. 读取固定失败 apphost（SHA256 `5a6ddd657fb62d8b0218ae52ccb81a449281ca77025d60c4d07e3cef9c196021`）及其托管 DLL、System32 的 ntdll/kernel32/kernelbase/user32/shell32/advapi32/ucrtbase、项目内 .NET 10.0.12 的 hostfxr/hostpolicy/coreclr/System.Private.CoreLib 的文件安全描述符。没有枚举用户文件或读取账号资料；此列表是有限候选，不宣称覆盖全部加载依赖。
5. 按每个对象类别的 GENERIC_MAPPING 先 MapGenericMask，然后 AccessCheck 各独立权限、generic rights 及 MAXIMUM_ALLOWED。真实 descriptor 和 token 仅作输入，没有尝试把计算所得的权限用于访问剪贴板或创建窗口。
6. 输出 ACE 类型、mask、固定 well-known SID；账号/登录 session 等私有 SID 只输出 SHA256。结束复核自身 token 内容、station/desktop 句柄以及无线程 impersonation 均保持。

没有 Clipboard API、SetThreadToken、Impersonate、CreateProcess、CreateWindowStation、CreateDesktop、SetProcessWindowStation、SetThreadDesktop、SetSecurityInfo、UAC、提权/日志修改或 GUI 初始化。

## 如何解释

顶层 `completed_readonly_diagnostics` / exit0 只表示工具完成全部诊断尝试；必须继续查看每个对象的 `status` 和每个结果的 `api_succeeded`。对象读取失败为 `read_or_query_failed`；API 失败 `api_succeeded=false` 的 access_status 为 null，不可解释为权限被拒绝。

当 API 本身成功，`access_status=false` 才代表对当前捕获 descriptor 的该项内存访问检查拒绝。它不能单独证明过去 PID24008 的真实加载失败就是该对象；也不能证明 GUI/浏览器/剪贴板可用或隔离成功。SACL/mandatory integrity、其他内核策略、运行时动态依赖、已打开或继承句柄、实际对象打开时机均不完整建模。

C 是诊断假说：官方说明 WRITE_RESTRICTED 只在写访问检查中使用限制 SID；WinSta0 的 GENERIC_WRITE 包括 WINSTA_ACCESSCLIPBOARD、CREATEDESKTOP、WRITEATTRIBUTES，GENERIC_READ 不包括 clipboard；Default desktop CREATEWINDOW 也属于 GENERIC_WRITE。实际 AccessCheck 结果用于区分文件加载读取、窗口创建和剪贴板权限，不把 flags9 当作已验证边界。今后将其应用于真实 child 属于新进程限制试验，不能由本次只读诊断默许。所有 clipboard copy/cut/paste、浏览器子进程与 GUI 实测仍 `not_run`。

若需要新窗口站、ACL、加载器跟踪/调试或实际受限 browser host，先形成具体方案并另获所需授权。已存在的系统自动命名 noninteractive station 不是本应用私有隔离空间，不能为了避免一次 UAC 擅自复用。

## 构建与运行

构建已完成，0 warnings / 0 errors，详见 build.log。仅当前进程设置 `DOTNET_ROOT=<WORKSPACE>\.tools\dotnet`，集成负责人普通权限执行已冻结 apphost：

```powershell
& '<WORKSPACE>\artifacts\isolation-20261018\drafts\RestrictedTokenAccessCheck\bin\x64\Release\net10.0-windows\win-x64\AutumnOS.RestrictedTokenAccessCheck.exe' --workspace '<WORKSPACE>' --report '<WORKSPACE>\artifacts\isolation-20261018\restricted-token-accesscheck-01\report.json'
```

报告已存在则拒绝开始。工具不重试进程、不启动 child、不使用管理员模式。

## 官方依据

- AccessCheck 要求 QUERY 权限的 impersonation token、有效 owner/group、预先映射的 desired rights；API return 与 AccessStatus 分开。[AccessCheck](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-accesscheck)
- DuplicateTokenEx 创建 token 对象与实际 impersonation 是不同操作；此处没有附加/模拟调用。[DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex)
- 限制 SID 双访问检查、WRITE_RESTRICTED 语义。[Restricted Tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens)、[CreateRestrictedToken](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)
- 查询只请求 OWNER/GROUP/DACL，不请求 SACL。[GetUserObjectSecurity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectsecurity)
- 各类 mapping 对照：[Window Station rights](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)、[Desktop rights](https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights)、[File rights](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights)。
