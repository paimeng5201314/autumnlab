# 派蒙 · WinSta0 有界只读元数据检查器（待执行）

本目录是独立、未执行的诊断源码。它不改变既有三组 desktop-native 诊断及历史证据。

`Inspect-WinSta0Metadata.ps1 -ReportPath <全新绝对 JSON 路径>` 供 root 审阅后在既有 Windows x64 PowerShell 普通权限进程运行。建议用新的 `pwsh -NoProfile -File`；已有同名 Inspector 类型则拒绝，避免旧 Add-Type 字节被误关联新源码哈希。报告父目录须预先存在；报告 `FileMode.CreateNew`，不覆盖，文件名拒绝 alternate data streams。脚本仅在进程内编译旁边固定 C# 源码，持源文件只读句柄并记录源码及驱动哈希。编译/脚本基础设施失败不代表元数据已读，也不代表通过；预留报告可能为空，保留作失败证据。

调用范围：查询当前进程 token 的 elevation / AppContainer 整数并要求均为 0；借用 `GetProcessWindowStation` 句柄只查询当前名字，要求 `WinSta0`。随后固定 `OpenWindowStationW("WinSta0", false, 0x20000)`，仅请求 `READ_CONTROL`；`GetSecurityInfo(handle, SE_WINDOW_OBJECT=7, DACL_SECURITY_INFORMATION|LABEL_SECURITY_INFORMATION=0x14)`。不请求 owner/group 或完整 SACL，不调整 privilege，不提权。官方 `LABEL_SECURITY_INFORMATION` 查询要求为 `READ_CONTROL`，与完整 SACL 查询所需的 `ACCESS_SYSTEM_SECURITY` 不同。返回 SD 仅在内存解析，`LocalFree`，关闭自己新开的 station 读句柄；不关闭借来的当前 station 句柄。最后核对进程 station 句柄/名字未变。

报告只保存两种内置 trustee：`S-1-15-2-1`（ALL APPLICATION PACKAGES）及 `S-1-15-2-2`（ALL RESTRICTED APPLICATION PACKAGES）。保存匹配 ACE 的 ordinal/type/flags/qualifier/callback/object-scoped/raw mask、按交互 station 官方规则展开 generic bits 后的 mask/rights，以及其超出私有 station `0x23` 的位。保留 allow/deny 与顺序；不把多条 ACE 合并成 effective access。其它 SID 不 stringify/translate/记录；不记录 owner、group、完整 SDDL、SD 字节或全 SD 哈希。完整性标签仅保存 mandatory authority 的 RID 与 NW/NR/NX policy。未显式存在 label 时报告 count=0，不编造 Low。

没有剪贴板 API、输入/窗口 API、desktop 创建/打开 API、profile API、station 创建/切换 API、ACL 写 API、任意命令/进程启动、UAC 或特权修改。唯一写入为调用者指定的全新报告文件；源代码/驱动只读。需要区分该检查器的显式调用范围与 PowerShell/CLR 自身运行。

这些是两个匹配组的 descriptor 元数据，不是 AppContainer token 的完整双重访问检查、当前实际已授句柄权利或 USER32 初始化因果证明。WinSta0 与 noninteractive station 的交互性、其它 ACL 主体、token、integrity policy 也可能影响结果。与三组成功矩阵及私有 station USER32/1114 失败对比只能缩小待验证差异；不能由此宣称 `ACCESSCLIPBOARD` 为初始化必要条件。

官方依据（仅读取，无建议执行文档中的 ACL 修改命令）：

- [Window station security and access rights](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)：标准/专属/generic rights，`0x4` 为 clipboard、`0x20` 为 global atoms、`0x8` 为 createdesktop。
- [GetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo)：句柄查询、`READ_CONTROL`、返回 descriptor 以 `LocalFree` 释放。
- [SECURITY_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/secauthz/security-information)：DACL 与 mandatory label 的查询均只需 `READ_CONTROL`。
- [SE_OBJECT_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/accctrl/ne-accctrl-se_object_type)：window station / desktop 使用 `SE_WINDOW_OBJECT`。
- [OpenWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openwindowstationw)、[CloseWindowStation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-closewindowstation)：固定名字、不可继承、仅只读请求与自己句柄的关闭。
- [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)：两种 application-package built-in SID 的官方映射；其中 ACL 修改示例不在本检查器范围。

状态：源码准备；编译 `not_run`；元数据读取 `not_run`；隔离验收 `not_claimed`。
