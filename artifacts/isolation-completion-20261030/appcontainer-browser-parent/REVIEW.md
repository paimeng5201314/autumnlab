# 普通权限 AppContainer 浏览器父审计器

制作人：派蒙。此目录是独立机制诊断工具，未接入产品，也不授予任何应用运行资格。

## 执行入口和输入

只编译不创建 profile。执行入口严格为：

`AutumnOS.OwnedAppContainerBrowserParent.exe --execute <32位小写hex新nonce> <绝对manifest路径>`

manifest schema_version=1，包含 `case_root`、`child_directory`、`runtime_directory`、`child_executable`、`runtime_version`、`child_files`、`runtime_files`。每项文件绑定为 `{path,sha256,bytes}`；文件集合必须完整相等，摘要必须对应实际源字节。运行时固定154.0.4258.53，child入口固定AutumnOS.OwnedAppContainerBrowserHost.exe。拒绝重解析点、网络盘、绝对包内路径及越界路径。

case_root必须是预先存在的D盘本地目录。每次只创建新 `browser-case-<nonce>`，已有同名目录直接失败，不复用、不删除。报告为该目录内 `report.json`。冻结清单和构建脚本由集成负责人统一准备。

## 具体影响和权限

1. 普通权限父进程首先核验自身非提升、非AppContainer。将冻结child与固定引擎逐文件复制到全新的A目录，逐字节核验新副本；创建A自有UDF/TEMP，不触碰真实产品数据。
2. A必须完成真实网页WASM42/60帧、匹配Normal事件、父持有的原browser/child及所有已观察子进程句柄signaled、最终SDK环境集合空，才允许创建B的新profile。
3. B使用Windows新建的唯一AppContainer profile，SECURITY_CAPABILITIES请求零capability。普通父通过显式STARTUPINFOEX句柄列表仅传递两个新pipe句柄；不继承其它文件、进程或凭据句柄。环境仅包含标准LOCALAPPDATA、SystemRoot、新自有TEMP/TMP和关闭.NET诊断端口的两个开关，不继承宿主WebView2覆盖或代理变量。
4. B的child和引擎复制到新D盘目录，新目录只给当前owner和精确新package SID对应权限。UDF和TEMP位于Windows本次新profile下的新子目录，继承该位置的OS标签；本工具未独立读取其mandatory label，不把继承预期写成标签实测。只对本次创建目录设置DACL；既有祖先、WinSta0、Default、Windows profile本身的ACL/SACL均不修改。
5. 父不调用剪贴板、网络探测或UAC，不改变系统安全策略，不关闭Chromium沙箱，不终止进程。

## IPC和证据

child参数为 `--execute nonce runtime UDF`。stdout使用UTF8 JSONL，每行至多64KiB、总计至多4MiB；信封必须匹配schema、nonce、原host PID及固定标记。stdin为至多1024字节的JSONL命令 `load_owned_page` 和 `close`。

controller_ready时父先核验实际固定版本、策略、InPrivate和精确UDF路径hash，然后OpenProcess取得原browser句柄并查询OS token、映像和创建时间。B在父已观察的原host/browser等进程均为精确新package SID、AppContainer=1、IL<=4096、capabilities0且无观察缺口时才发送加载自有页面命令。任何缺项均失败，不靠child自报受限位推导。

父另用Toolhelp32快照独立发现原host后代，逐个OpenProcess持有原句柄，查询OS token的AppContainer、SID hash、IL、capability SID hashes和映像路径。SDK inventory仅提供角色提示；角色字段明确为 `SDK_hint_plus_OS_image_identity_not_independent_command_line_role`。快照可能遗漏短命进程，报告固定 `sampled_not_exhaustive`，不宣称全生命周期或完整隔离已证明。原native句柄、token和退出事实公开序列化进报告，句柄值不序列化。错误保留固定stage、异常类型、HRESULT及Win32错误码；不保存任意异常文本、用户名路径或消息内容。

首轮A对照在应用页面加载前因复制树外的OS后代映像失败。后续诊断修订保留该映像的basename、规范化完整路径SHA256、是否精确Windows/System32/conhost.exe以及是否位于本次复制树；不保存完整路径。识别conhost的布尔值只供定位，同样记录树外失败与原句柄，绝不作为认可名单或放行例外。child仍保持同CUI字节进行下一组对照，是否更改子程序输出类型须依据真实结果另行处理。

收到自有页面完成后只请求正常关闭。45秒观察预算耗尽、取消或协议错误只增加失败并请求正常关闭；不会把后来退出改为测试通过。父持续保留并观察原句柄，未signaled不释放，报告显式保持failed_pending_observed_original_handles_or_stdio；外层驱动不得以超时Kill父或子进程。45秒是观察预算，不是强制结束时限。failure_stage和failure_native_stage保留原失败阶段，cleanup_stage单独记录，清理不覆盖原失败。

测试通过和清理资格分别记录。即使功能/token测试失败，只要本次browser匹配Normal、全部已观察原句柄signaled、SDK最终空且无进程身份观察缺口，仍可精确DeleteAppContainerProfile清理这个新profile。token事实查询失败与进程身份缺口分别记录，token失败不单独取消已经证实的清理资格。任何CreateProcess调用前失败，或CreateProcess失败且所有返回句柄和ID全0的分支，记“未创建进程组”，也可清理精确新profile。部分返回身份、未证明退出或进程身份观察缺口均保留profile，不递归删除、不开PID替代原句柄、不修改现有对象ACL。所有D盘诊断副本和报告保留。

删除后对删除前保存的精确profile路径调用GetFileAttributesW，只有原生错误2/3才记路径不存在；错误5等保持unknown/failed。另以同一新SID调用GetAppContainerFolderPath并保存原始HRESULT及是否返回路径，只接受0x80070002/0x80070003作为SID路径不存在。删除HRESULT、两份原始后查证据分别保存；不用Directory.Exists(false)替代独立文件系统检查。

## 当前验证范围

本目录父源码已于本轮用项目SDK执行Windows Release编译，零警告、零错误；之后新增的最终修订由集成负责人统一重编译绑定。未执行此父程序，未创建profile，未运行浏览器或剪贴板实验。完整产品、剪贴板、中文IME、独立完整生命周期角色识别均为not_run。生产门禁不解除，源码编译不代表机制或产品验收通过。
