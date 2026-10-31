# 私有station读权限七组对照草案

制作人：派蒙。新文件 `StationReadLoader.c` / `AutumnOS.NativeStationReadLoader.exe` 独立保存。原 `NativeUser32Loader.c` 与既有559ed5…EXE不改字节；本变体唯一代码语义差异是成功loaded行的native_error固定0，失败仍保留原始GetLastError。当前仅源码、编译和静态PE检查，尚未请求或获得这次具体新包的执行授权；新station/profile/ACL/UAC、child、browser和clipboard均未执行。

固定七组、不得额外重试：A23、B23、B20023、B20123、B20323、B23-restored、A23-restored。A是普通权限正控，B是真实零capability AppContainer。全部共用同一新命名非交互station、同一新AppContainer profile SID、同一0x87新desktop以及完全相同的原生子程序字节；每次只更改该新station的精确package SID那一条ACE。ENV SIDE仍只能是A/B，七个case标签仅由普通父程序记账。恢复两项正负对照用于核对临时更改后的复现性，不把一次成功当持续边界证明。

| 新station package mask | 相比前项增加的权限 | 含义 |
| --- | --- | --- |
| 0x23 | 基线 | ENUMDESKTOPS、READATTRIBUTES、ACCESSGLOBALATOMS |
| 0x20023 | READ_CONTROL=0x20000 | 读取此新站安全描述符的允许部分 |
| 0x20123 | WINSTA_ENUMERATE=0x100 | 允许此新站被枚举 |
| 0x20323 | WINSTA_READSCREEN=0x200 | 允许访问此新私有空站的屏幕内容 |

READSCREEN是明确新增权限，不能省略其影响或笼统称作无影响的generic read。每个mask都不含WINSTA_ACCESSCLIPBOARD=0x4，不包含新建desktop、修改属性、退出Windows、WRITE_DAC/WRITE_OWNER、DELETE等新增权限。不授AllApplicationPackages、World或GENERIC_ALL，不增加token能力，不修改WinSta0/Default或任何既有对象的ACL/完整性标签。

每组全部原始进程句柄均已真实退出、精确原子Job已ActiveProcesses0且PID表空、stdout已EOF并排空，才允许更改下一mask。更新必须只作用于已由本次认证holder创建的新对象：保留owner/SYSTEM ACE与protected标记、保持原Low/NoWriteUp SACL不变，修改前后从内核回读并核对精确SID和完整mask。父程序自己的全权station/desktop句柄绝不继承给B；子端仅继承必要pipe和经审阅的最小对象句柄。一个组未证实退出/清理时不更改权限、不启动下一组、不强杀，也不把超时当退出。

原生child保持仅11个KERNEL32静态导入、无CRT/CLR/USER32/SHELL32静态导入。它只从AUTUMN_PROBE_NONCE/SIDE/STATION/DESKTOP取得受限绑定，严格接受AutumnOS_ClipboardProbe_<32hex>与AutumnOS_ClipboardDesktop_<32hex>，忽略所有命令行参数。先输出native entry，再从SYSTEM32依次动态加载USER32/GDI32/SHELL32；USER32成功后才通过动态metadata函数核对精确当前station/desktop，不能声称更早就有子端真实绑定证明。没有clipboard、窗口创建、输入/显示切换、COM、browser、profile/ACL修改或进程创建API。

每层输出固定NDJSON阶段、返回状态和失败原生错误；最多16行、每行少于1024字节，无任意路径/环境值。成功行native_error0，失败行保留1114等实际错误。失败普通ExitProcess20，成功ExitProcess0；若系统库内部直接终止，缺终态必须如实标未完成。Microsoft DLL自身初始化是本次真实观察对象，不能从本代码无显式调用推导DLL没有内部活动。

需要新的具体授权时，其理由是只创建一个全新命名station：Microsoft CreateWindowStationW只允许管理员指定名称。普通broker将只请求一次受限用途holder提权；所有native子程序和普通控制器保持各自预定普通/AppContainer权限。holder只创建、认证、持有并精确释放该新站，不运行应用、不收任意命令、不改全局策略。旧单次UAC授权已消耗，不可沿用。完整源快照、buildID、EXE摘要、固定矩阵、影响和清理方案应先由root冻结供审核，批准之后才由root运行。

七组完成只表示捕获了可比较的DLL初始化结果；加载失败可以是有效的诊断结果，必须保留精确阶段/错误/退出和清理证据。它不代表clipboard、WebView2、内部呈现、键鼠/中文IME、SDK或完整隔离通过，产品第三方门禁继续保持。

依据：[station权限](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)、[创建station及名称权限](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)、[按lpDesktop和MAXIMUM_ALLOWED连接规则](https://learn.microsoft.com/en-us/windows/win32/winstation/process-connection-to-a-window-station)。这些文档提供权限语义，不承诺其中哪一位能够修复本机USER32初始化，必须以对应新实测判断。
