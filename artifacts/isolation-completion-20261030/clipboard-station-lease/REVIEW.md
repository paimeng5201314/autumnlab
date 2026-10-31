# 新私有剪贴板窗口站租约 · 仅源码与编译

制作人：派蒙。日期：2026-10-30。当前没有执行`--broker`、`--hold`、UAC、窗口站、AppContainer profile或剪贴板实验。执行本次新UAC前，集成负责人必须展示本包实际字节与影响，并取得派蒙对本次具体试验的明确确认。旧单次UAC授权不沿用。编译通过不代表新描述符、跨完整性管道或浏览器剪贴板隔离通过。

本实现从旧`artifacts/isolation-20261018/drafts/ClipboardStationLease/Program.cs`的窄租约协议扩展；旧源码、旧六项A/B与copy/cut清空反例保持原字节。当前helper和broker均为自包含WinExe，避免为测试新建console/conhost；调试结果仅写继承的stdio（无句柄时不会创建console）。root负责统一构建脚本与最终发布文件清单。

## 固定接口

普通broker唯一入口：

```text
AutumnOS.OwnedClipboardStationLease.DRAFT.exe --broker --probe <controller EXE绝对路径> --manifest <root冻结的controller manifest绝对路径>
```

普通broker产生一个128位随机nonce，唯一一次`ShellExecuteExW(runas, SW_HIDE, SEE_MASK_NOCLOSEPROCESS)`启动同字节self holder。取消或失败不重试。holder只接受以下固定字段，没有用户SID、SDDL、命令、EXE、runtime、manifest或报告路径参数：

```text
--hold --pipe AutumnOS_ClipboardLease_<nonce> --nonce <nonce> --broker-pid <原broker PID>
```

holder经真实pipe server PID、原持有process句柄、same-user/session、精确self image、Medium/High非AppContainer身份双向认证后，才接受一个`create`消息。普通broker对holder进行对称检查，原始holder句柄来自本次ShellExecuteEx。pipe只授权当前用户和SYSTEM，`FirstPipeInstance`；不使用会拒绝高/中完整性跨级连接的`CurrentUserOnly`。协议是4字节little-endian长度与最多4096字节JSON，固定10字段，拒绝重复、未知、缺失字段；stage仅create/ready/release/released。

holder收到固定nonce后，仅派生`AutumnOS.Paimon.ClipboardProbe.<nonce>`的package SID，不创建或删除profile，不接受调用方指定任意SID。收到请求和真正派生的SID摘要不一致即停止。普通controller自行创建该新profile并独立核验实际SID。

ready之后，broker以自己原普通权限启动controller，固定argv：

```text
--execute <nonce32> <manifest绝对路径> <holderPID> <brokerPID>
```

controller按manifest的case_root生成报告、创建新desktop并运行自有collector和浏览器；这些职责不在holder中。controller必须独立验证nonce、真实同用户/session的holder/broker句柄、各自精确image及其hash、新station的真实owner/DACL/Low label/nonvisible及派生package SID。JSON消息或报告不作为系统权限证明。

## 唯一新对象与固定访问权

holder仅调用一次`CreateWindowStationW("AutumnOS_ClipboardProbe_<nonce>", CWF_CREATE_ONLY, ...)`。新对象必须noninteractive、不可继承；已存在或任何API错误均失败，不复用旧station。owner/group为当前同用户SID，受保护DACL恰好三条无继承allow ACE：

| 身份 | 固定mask | 用途 |
| --- | --- | --- |
| 当前用户 | `0x000F037F` | 普通collector与controller管理本次新对象 |
| SYSTEM | `0x000F037F` | 保留系统访问 |
| 精确本次package SID | `0x00000023` | `WINSTA_ENUMDESKTOPS(0x1) | WINSTA_READATTRIBUTES(0x2) | WINSTA_ACCESSGLOBALATOMS(0x20)` |

package没有`WINSTA_ACCESSCLIPBOARD(0x4)`、`WINSTA_CREATEDESKTOP(0x8)`、`WINSTA_WRITEATTRIBUTES`、`WINSTA_EXITWINDOWS`、`WINSTA_READSCREEN`、`WRITE_DAC`或`WRITE_OWNER`。`0x23`是用于此新私有station的候选访问集合，其数值和含义按官方文档核验；尚未实测它足以初始化WebView2。失败不自动扩权或追加SID。

新对象mandatory label仅Low、NoWriteUp：`S:(ML;;NW;;;LW)`。helper回读精确name、nonvisible、不可继承、owner/group、protected DACL三条ACE/每个mask和唯一Low label；不使用任意可编辑报告代替回读。Low label允许低完整性参与本次新对象，不授予缺失的DACL权限。

普通collector的完整station句柄不能继承到AppContainerB。实际B所关联的station handle及后续重开权利仍须由独立controller/浏览器探针核验；这里不因token带AppContainer位或DACL缺0x4就宣称真实copy/cut/paste已受限。剪贴板接收端必须始终是普通进程，不能把B返回的零序号当成“未改变”的证据。

## 高权限分支的边界

holder的业务代码没有File/Directory操作、manifest读取、profile注册、clipboard API、CreateDesktop、SetProcessWindowStation、SetThreadDesktop、进程创建/终止或安全策略修改；它只持有自己新建的station句柄及认证管道。派生SID使用官方`DeriveAppContainerSidFromAppContainerName`并`FreeSid`释放。native imports只从System32加载。

普通broker在UAC前验证manifest/self/controller路径和摘要，拒绝网络盘/UNC/reparse路径，核对broker完整发布文件集合并只读持有所有文件（FileShare.Read，禁止写入或删除共享），直至原holder真实退出。manifest须位于lease发布目录之外，并包含：

```json
{
  "broker_executable": "<lease发布EXE绝对路径>",
  "broker_sha256": "<64小写hex>",
  "controller_executable": "<controller EXE绝对路径>",
  "controller_sha256": "<64小写hex>",
  "broker_files": [{"path":"<lease发布目录相对路径，正斜杠>","bytes":0,"sha256":"<64小写hex>"}]
}
```

上例仅说明字段，不是可运行清单。其余controller/child/runtime字段由root提供并由controller验证。broker、holder拒绝启动hook/profiler等注入配置，只记录“存在”的失败，不输出环境变量值。此检查不改变系统或用户环境。

## 退出、失败与仍未验证项

controller原进程句柄未signaled时，broker不因超时主动发release；controller应在任何浏览器/子进程退出、profile清理未证实时保持pending。controller实际退出后，broker执行release握手并观察原holder句柄退出。握手异常时断pipe让holder自行结束，broker仍保留全部文件pin及原进程句柄直至退出。失败观察不能当作退出；不重开PID、不Kill、不Kill-on-close。

连接与ready握手同时观察本次ShellExecuteEx返回的原holder进程句柄。若Windows启动、CLR或策略失败使holder先退出，broker立即记录固定阶段及真实exit code并失败，取消并收束本次pipe等待，不空等10分钟、不重开PID、不再请求UAC。pipe先完成时，只取消辅助轮询观察；Broker.finally仍以同一原句柄无取消预算地确认退出。ready读写同样监控peer提前退出，外层10分钟预算只限制握手，不作为进程退出证明。

holder自身租约上限10分钟，断连/过期时只关闭它自己的station句柄并自然退出。其它仍活着的进程可能还持有station对象，所以helper退出、CloseWindowStation成功都不等于整个对象已销毁。controller/collector必须在helper失效时停止新的clipboard操作并走合作清理；不得利用过期自动提升验收。这里没有直接clipboard测试，也不证明生产内部画面、键鼠/IME或IPC安全。

待执行项全部`not_run`：新UAC、真实pipe跨完整性握手、CreateWindowStation/实际descriptor回读、AppContainerB关联与浏览器启动、新六项copy/cut/paste A/B、任意collector/native clipboard调用及真实生命周期矩阵。只能由root按具体批准后的固定字节启动。

## 官方依据

- [CreateWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)：自定义名称管理员要求、CREATE_ONLY、继承与安全描述符。
- [Window station访问权](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)：上述WINSTA位含义；ACCESSCLIPBOARD=0x4。
- [DeriveAppContainerSidFromAppContainerName](https://learn.microsoft.com/en-us/windows/win32/api/userenv/nf-userenv-deriveappcontainersidfromappcontainername)：只派生SID及FreeSid释放。
- [Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)：完整性标签和DACL是不同检查。
- [窗口站连接规则](https://learn.microsoft.com/en-us/windows/win32/winstation/process-connection-to-a-window-station)：继承句柄选择、显式lpDesktop与MAXIMUM_ALLOWED。
- [.NET PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)：CurrentUserOnly同时比较提升等级。
- [ShellExecuteExW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw)：本次启动的原始process handle。
