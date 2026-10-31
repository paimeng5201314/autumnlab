# 原子 JobList 的普通权限浏览器 A/B 预检

制作人：派蒙。日期：2026-10-30。当前目录是新的独立机制预检，不修改已经通过的 appcontainer-browser-parent 或原有产品。只编译；没有执行本程序、profile/ACL/GUI实验或任何剪贴板调用。root负责实际A/B和构建脚本。

本项目复制原父审计器的源码、项目、锁和说明，不复制bin/obj/缓存。它通过明确的Compile Link引用 `../clipboard-appcontainer-controller/JobEvidence.cs`，该真实源字节必须一起进入root构建快照。当前共享源码SHA256为 `f4a4c0cd1e37616fcc63670ed2e9d9f6b78f9b59b55fe5e382e7344db3559286`。child继续使用manifest冻结的已验证WinExe host，不修改该child源码，也不把旧通过成绩计作新Job变体通过。

## 执行入口及实际影响

固定入口：

```text
AutumnOS.OwnedAppContainerBrowserParent.exe --execute <32小写hex新nonce> <绝对冻结manifest路径>
```

manifest仍为schema_version=1，包含case_root、child_directory、runtime_directory、child_executable、runtime_version、child_files与runtime_files。case_root必须预先存在且位于本地D盘；每次只创建全新 `job-bound-browser-case-<nonce>`，拒绝已有目录，不复用不删除。报告位于新目录report.json。文件集合、长度、SHA256逐项相等，拒绝网络盘/reparse/越界路径。固定浏览器154.0.4258.53；child固定AutumnOS.OwnedAppContainerBrowserHost.exe。

普通父先验证自己非提升、非AppContainer。先运行普通A，A真实完成WASM42与60帧并满足全部退出条件后，才创建一个全新唯一AppContainer profile用于B。B请求零capability，仅给本次新建目录及精确package SID所需访问，不改已有祖先、WinSta0/Default、Windows profile本身ACL/SACL。B的UDF/TEMP在本次OS profile下新子目录。环境只包含标准LOCALAPPDATA、SystemRoot、新TEMP/TMP和两个.NET诊断禁用开关，不继承宿主凭据、代理或WebView2覆盖。

每个A/B host各自拥有一个全新unnamed Job，默认LimitFlags必须原生回读为0，且观察中重复核验。没有SetInformationJobObject、post-start AssignProcessToJobObject、kill-on-close、breakaway或UI restrictions。它用于原子进程归属和退出计数，不提供剪贴板边界。

## 原子绑定与页面执行门禁

STARTUPINFOEX显式属性包括：两个仅自有stdin/stdout pipe的HANDLE_LIST、一个非继承Job handle数组的JOB_LIST，以及B单独的零capability SECURITY_CAPABILITIES。A的attribute_count=2，B=3。JOB_LIST在CreateProcess之前设置，核对真实返回值，不在child启动后补Assign。新Job句柄不在继承列表内，不通过Job名称复用对象。

成功CreateProcess后，父持原host句柄调用 `IsProcessInJob(originalHost, thisExactJob)`，必须为true。controller_ready时，再对已持有原browser句柄调用同一API，必须为true；传NULL测试“属于任意Job”不能代替。父同时核验固定版本、policy、InPrivate、精确UDF hash、原browser映像/创建时间与OS token。B的原host/browser及已观察进程必须匹配精确package SID、AppContainer=1、IL<=4096、capabilities0。全部满足且没有历史Job查询/成员失败，才发送load_owned_page，执行真实WASM42/60帧。

独立发现包括Toolhelp后代快照、SDK inventory角色提示与新Job的JOBOBJECT_BASIC_PROCESS_ID_LIST；每个新Job PID均取得OS原句柄并查询映像、创建时间、token，不能凭SDK自报或父AppContainer位推导浏览器身份。Job PID列表要求完整返回，不截断；上界4096个。它补充可达进程集合与退出证据，但短命成员的完整身份历史仍可能缺失，报告不宣称全生命周期无遗漏身份或完整隔离。任何发现/身份缺口保留失败。

## 收尾与失败

收到真实自有页面完成后只请求正常关闭。45秒观察预算、用户取消、协议/Job查询/成员失败都只记失败并请求close，不强杀，不因预算放行。

清理必须同时满足：匹配原browser PID的Normal事件、持有原host/browser及全部观察到的进程句柄signaled、SDK最后环境集合0、没有进程身份缺口、以及最近原生Job accounting ActiveProcesses=0且完整Job PID列表为空。Job查询失败历史不清除；后来恢复取得真实空证据可以用于清理，不能恢复测试通过。原始Job handle仅由CloseAfterEmpty关闭，没有kill-on-close。

任何句柄/stdio/Job成员未完成时，父保持pending和原句柄/Job，不重开PID替代，不据超时删除profile。若CreateProcess失败且所有返回句柄/ID均0，仍需观察新Job真实空；调用前失败则无child组。部分返回身份一律保留失败并继续观察原句柄。A失败不创建B profile。

B只有完成上述收尾才可DeleteAppContainerProfile精确清理本次新profile，不递归删除。删除后用保存的原profile路径GetFileAttributesW和同SID GetAppContainerFolderPath分别后查，只有原生错误2/3或对应HRESULT才证明不存在。D盘副本与报告原位保留，功能失败和清理证据分开。

## 当前结果及边界

本目录已完成Windows Release编译，零警告、零错误；root最终构建另绑定共享Job文件和全部输入实际字节。真实普通A/AppContainerB、JobList兼容性、Job PID发现/退出矩阵均not_run，等待root执行。本预检没有UAC、window station/desktop创建、剪贴板、网络探测、输入/IME或产品第三方运行，不改变生产拒绝门禁。

## 官方依据

- [UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)：JOB_LIST、HANDLE_LIST与SECURITY_CAPABILITIES属性。
- [IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)：非NULL精确Job handle与原进程handle查询。
- [JOBOBJECT_BASIC_PROCESS_ID_LIST](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_process_id_list)：包含子Job成员，完整缓冲区要求。
- [JOBOBJECT_BASIC_ACCOUNTING_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information)：ActiveProcesses原生计数。