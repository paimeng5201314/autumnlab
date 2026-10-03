# 单文件便携交付

派蒙的新要求替代原先允许必要 DLL 与 EXE 并列的交付布局。只交付一个 `AutumnOS.exe`，首次打开只在同级创建 `AutumnOS_Data`。没有安装向导、全局组件安装或额外快捷方式。

启动准备器使用目标 Windows 自带的 .NET Framework 4.x；实际产品仍为自包含 .NET 10 / WinUI 3。所有产品运行依赖、固定 WebView2、内置游戏、开发者功能与许可证均在 EXE 内。运行时按内容哈希将资源展开到 `AutumnOS_Data/System/Product-<20位摘要>`；.NET 单文件原生提取路径限定在同一 Data 下的 System/CLR，仅设置子进程环境，不修改系统 PATH/注册表。无需另下程序组件。

`PortableLayout` 用固定目录结构与固定标识识别布局，拒绝重解析点，不读取任意环境变量或当前工作目录。用户数据、账号、存档和设置仍共享 EXE 旁原 `AutumnOS_Data`；`ProgramDirectory` 专供程序资源和更新事务使用。不同单文件种子使用不同运行目录，不覆盖用户数据；重复启动仍由已有业务单实例协议唤回原窗。

包内只剔除 PDB 调试符号，既有产物不删除。所有普通运行目录、PRI/XBF/deps、SDK/模板/文档、许可证保持。按 SHA-256 合并 EXE 内重复的字节内容，展开后保留必需的运行路径；不使用硬链接，以避免更新覆盖共享内容。没有进行未经验证的 .NET/WinRT 裁剪。

启动器验证嵌入负载的编译固定 SHA-256、清单、逐文件字节、路径和稳定内层启动器/更新器。这个摘要用于本地包完整性，不能替代发布签名。网络更新仍由原独立更新器重新检查生产信任根、签名、负载、目标根和维护交接。检测到未完成事务时交给受保护内层启动器恢复，不能把半更新目录直接当业务实例启动。已更新版本不重新覆盖成 EXE 内的旧种子。生产根缺失继续拒绝自动安装。

准备和校验受同用户/目录专用互斥协调；此互斥只用于资源展开，不冒充业务维护锁。成功准备采用完整新目录原子改名；取消/失败保留 Data 内的阶段目录，不删除未知数据。只读、重解析点、损坏资源失败时保留存档，不请求管理员权限或关闭安全策略。

本地命令（PowerShell 7，工程根）：

```powershell
./scripts/Build.ps1 -BuildId T06-20261003-single001-01 -BuildVersion 0.5.1-local.4 -ReleaseLabel meta0.0.1-20261001
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Test.ps1
./scripts/Package-SingleFile.ps1 -BuildId T06-20261003-single001-01
./scripts/Test-SingleFileSmoke.ps1 -BuildId T06-20261003-single001-01 -ReportDirectory ./artifacts/sf01
```

单文件实际原生验证包含：只有 EXE 的新中文目录、不同工作目录、首次欢迎/准备/真实桌面、依赖与逐文件摘要、准确版本、真实内部游戏、权限覆盖层、SDK 保存、后台维护阻塞、二次启动原窗、未保存输入继续、真正结束释放资源、重启原数据。报告保存实际进程所有权、截图与清理，不将原普通包成绩套到单文件。

Win10/干净 OS/离线首次运行矩阵与新布局完整 A/B 更新回退仍须各自真实执行，不能因打包成功自动通过。原 T06/T03 未完成项、生产签名/许可证门禁及制作人审阅保留。旧包和源码保留，本轮无公共发布授权。

官方部署参考：[.NET 单文件提取规则](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[WinUI 非打包自包含应用](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[Windows 的 .NET Framework 交付方式](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)。实际可用结论以本机最终 EXE 的报告为准。
