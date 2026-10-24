# Readonly02：随机合成限制 SID 的 AccessCheck

制作人：派蒙。2026-10-18。仅内存权限计算，未附加 token 到线程/进程。由集成负责人复核后自行执行；本目录的构建记录不代表实际运行。

复制自已经冻结的 `../RestrictedTokenAccessCheck/`；没有修改 Readonly01 源码、二进制、报告。操作范围和官方依据见 [Readonly01 REVIEW](../RestrictedTokenAccessCheck/REVIEW.md)。

此次差异：

- 保留 A 原 token、B RestrictedCode+flags1、C RestrictedCode+flags9 对照。
- 用 CSPRNG 生成96随机位，组成 `S-1-5-21-<a>-<b>-<c>-1001` 形式的合成 SID。仅语法校验，没有账号/组查询、创建、注册或删除；不声称以查询方式证明该 SID 在整个 Windows/域内不存在。合成值仅存内存，报告输出 SHA256。
- D 用该 SID + `DISABLE_MAX_PRIVILEGE (1)`；E 用同一 SID + `DISABLE_MAX_PRIVILEGE | WRITE_RESTRICTED (9)`。仍只调用 AccessCheck；没有 CreateProcess、token附加、Impersonate、ACL/station/desktop改变、GUI、Clipboard API、UAC。
- 逐个 query-only impersonation token 读回类型、身份hash、会话、Medium IL、限制SID和privilege。D/E 读回的限制 SID hash 必须等于同一合成值。
- 继续读取相同15个对象的 owner/group/DACL，测试相同权限位。真实 ACL 输出只含固定 well-known SID 或私有SID hash。
- 改进诊断状态：任一对象读取失败或任一 AccessCheck API 失败，顶层为 `partial_readonly_diagnostics`、exit1；API成功且访问拒绝仍是正常诊断数据。不能只靠 exit0 判断某条隔离策略成立。

关注的对照是 WinSta0 的 `WINSTA_ACCESSCLIPBOARD=4`、GENERIC_READ、ACCESSGLOBALATOMS，Default 的 CREATEWINDOW/READOBJECTS/WRITEOBJECTS，以及固定二进制的 FILE_GENERIC_READ_EXECUTE。即便 E 出现“文件读允许、clipboard拒绝”，仍未证明真实浏览器创建、用户输入、已继承句柄、Clipboard API 或其它进程无法旁路。若 Default CREATEWINDOW 拒绝，只能说明当前捕获 DACL 的该项内存检查拒绝，不能自动授权创建新 desktop、修改 ACL 或启动新受限 child。

Readonly01 已证明该机 WinSta0/Default DACL 对 RestrictedCode 的访问范围过宽，B/C 都允许 clipboard=4；将 C 直接用于产品不满足需要。Readonly02 是区分是否换合成 SID 有诊断价值，不能绕过这一失败直接放行第三方应用。

编译已完成，0 warnings / 0 errors。候选 EXE/DLL/源码哈希见 manifest.json；无共享项目引用。普通权限、仅当前进程 DOTNET_ROOT 指向项目内 `.tools/dotnet` 后，集成负责人唯一候选命令：

```powershell
& '<WORKSPACE>\artifacts\isolation-20261018\drafts\RestrictedTokenAccessCheck02\bin\x64\Release\net10.0-windows\win-x64\AutumnOS.RestrictedTokenAccessCheck02.exe' --workspace '<WORKSPACE>' --report '<WORKSPACE>\artifacts\isolation-20261018\restricted-token-accesscheck-02\report.json'
```

这是只读诊断入口，不是受限进程启动入口。真实 child/浏览器/clipboard/新station/desktop/ACL/UAC 试验均 `not_run`，需要其对应的后续授权与实际正反例。
