# Synthetic LUA token: handle-only diagnostic

制作人：派蒙。2026-11-03。

该独立诊断仅打开自身 primary token，调用 `CreateRestrictedToken` 的 flags 5（`LUA_TOKEN | DISABLE_MAX_PRIVILEGE`，零 restricting SID），回读返回的新副本，**只对该新副本**设置 Medium integrity，再完整回读。关闭副本后再次读取原 token 并比较原始事实。没有进程启动、token 附加、impersonation、剪贴板、ACL、账户或全局策略操作。

报告路径必须是当前工程 `artifacts/` 中的新文件，拒绝覆盖及路径中的 reparse 目录。报告含脱敏 user/owner/group SID、全部 privilege 名称与属性、类型、session、elevation、IL、`TokenHasRestrictions`、`IsTokenRestricted`、UIAccess、SandboxInert、AppContainer 及原 token 不变核验。`IsTokenRestricted` 仅检测 restricting SID 列表，返回 false 不代表 token 没被过滤；官方仍标 reserved 的 `TokenIsRestricted` 不使用。

`memory_diagnostic_completed` 只表示上述句柄操作完成；另列 `synthetic_experiment_baseline_matches`。无论该值如何，这个合成 token 都不被称作真实 UAC Limited/普通用户桌面，不形成任何子进程、LPAC、浏览器、剪贴板或产品验收成绩。后续进程试验必须另行审查，工具不会启动它。

从工程根构建，执行时仅给新报告路径：

```powershell
. ./scripts/Common.ps1
& $Dotnet build ./research/isolation-20261103/LuaTokenProbe/LuaTokenProbe.csproj -c Release -p:Platform=x64
& ./research/isolation-20261103/LuaTokenProbe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.LuaTokenProbe.exe --report ./artifacts/isolation-20261103/lua-token-handles-01/report.json
```

官方依据：[CreateRestrictedToken](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)、[IsTokenRestricted](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-istokenrestricted)、[TOKEN_INFORMATION_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class)、[TOKEN_ELEVATION_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_elevation_type)。

## 现有启动对象的纯 AccessCheck

可追加 `--check-launch-access <绝对自有apphost路径>`。只接受当前工作根 `tools/` 或 `research/` 中产品 metadata 匹配的 `AutumnOS.*.exe`，用读取 lease 和最终路径核对拒绝 reparse 跳转；固定关联同名 DLL、项目 SDK 10.0.12 的 hostfxr/coreclr/System.Private.CoreLib，不接受额外对象列表。

从刚建立的合成 Medium token 复制一个仅 TOKEN_QUERY 权限的 impersonation **类型对象**，不附加线程。捕获当前 WinSta0/Default 及固定文件 owner/group/DACL，在内存通过正确的 window-station/desktop/file GenericMapping 和 MapGenericMask 执行 AccessCheck。报告 `launch_access_checks.objects` 列出逐权限 API 成功/拒绝/错误、映射前后 mask、descriptor摘要与脱敏 owner/group。没有打开或使用 clipboard；ACCESSCLIPBOARD 只是 desired-access 数值4。

该检查不请求 SACL，因此不完整模拟强制完整性、实际 Win32k/loader 状态和其它内核策略。DACL 允许不等于真实启动成功，DACL 拒绝只是定位线索。不同对象可独立记录失败；`partial_readonly_accesscheck` 不改变既有失败报告。

```powershell
$apphost = Join-Path $PWD 'tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe'
& ./research/isolation-20261103/LuaTokenProbe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.LuaTokenProbe.exe --report ./artifacts/isolation-20261103/lua-accesscheck-01/report.json --check-launch-access $apphost
```

本子任务只构建未执行 AccessCheck 模式，主代理使用实际完整本机身份单独运行。先前 `lua-token-handles-01` 的 BOOLEAN 长度失败和 `lua-token-handles-02` 中已受限 sandbox token 的flags5错误87报告原样保留。

## 显式修正独立副本的 TokenDefaultDacl

所有token事实现在包含 `TokenDefaultDacl(6)` 的脱敏ACE：精确type/flags/mask、SID摘要与ACL/ACE字节摘要。默认模式仍只读默认DACL。

仅追加 `--repair-copy-default-dacl` 时，才在降Medium之后调用 `Native.SetCopyPrivateObjectDefaultDacl`。该方法要求独立、同用户/会话、primary、Medium、非提升、管理员禁用、已filtered、Owner为当前user、无restricting SID/危险启用privilege；还核对TokenId不等于当前进程token。原High token不能接受。新模板仅两条无继承标记的AccessAllowed GA：当前user、LocalSystem；使用SetTokenInformation6改变该副本的默认新对象DACL并严格回读相同字节。没有改任何既有文件、管道、桌面或原token的ACL。后续创建对象才会使用该模板；本诊断不创建这些对象。

```powershell
& ./research/isolation-20261103/LuaTokenProbe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.LuaTokenProbe.exe --report ./artifacts/isolation-20261103/lua-private-default-dacl-01/report.json --repair-copy-default-dacl
```

新增模式只编译、未由子任务运行。仍明确为合成内存token实验，不是普通用户或隔离验收。
