# Lab Chronicles AutumnOS

**制作人：派蒙**

这是本地 C# / .NET 10 / WinUI 3 原生 Windows 工程。T00 基础阶段已有实测；保留 T02 已交付的桌面、双栏设置、后台继续/结束和单实例，本轮按用户授权推进 T03 账号、权限、数据和开发者能力。T01/T02 缺项及完整发行验收继续保留。实际状态、构建 ID、哈希和测试路径见 [AUTUMNOS_PROGRESS.md](AUTUMNOS_PROGRESS.md)，历史证据核对见 [T00/T01 审计](docs/t00-t01-evidence-audit.md)。

## 本地构建与运行

在工程根用 PowerShell 7 执行。项目内 SDK 已获授权安装，位于 `.tools/dotnet`，不修改系统 PATH。首次恢复需要连接微软/NuGet 官方源；后续锁定依赖。

```powershell
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Test-Source.ps1
./scripts/Build.ps1
./scripts/Test.ps1 -ProbeLogto
./scripts/Test-DesktopInteractionSmoke.ps1
./scripts/Test-SingleInstanceSmoke.ps1 -ExecutablePath ./src/AutumnOS.Shell/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AutumnOS.exe
./scripts/Run.ps1
```

双击实际构建目录内 `AutumnOS.exe` 也能启动。不要只复制 EXE；自包含目录的 DLL 和资源需要一起保留。`artifacts/latest-build.txt` 指向最近通过的构建 ID，其 `build-result.json` 给出准确输出路径。构建/测试日志、源文件清单和产物 SHA-256 位于 `artifacts/builds/<build-id>/`。

若 NuGet 传输反复 EOF，可运行 `./scripts/Cache-OfficialPackages.ps1` 从同一官方源按锁文件下载并验证签名与 NuGet 内容哈希，再重试恢复；不关闭 TLS 校验。缺少项目内 SDK 时，先取得安装授权，再执行 `./scripts/Install-LocalSdk.ps1`。现有锁文件不可为绕过失败而删除。

本轮打包先执行 `./scripts/Build-ProtocolPeer.ps1 -BuildId <唯一辅助ID>`，再执行主版本 `Build.ps1` 和 `Test.ps1`，最后用 `./scripts/Package.ps1 -PeerExecutablePath <辅助构建输出的AutumnOS.exe>` 生成带明显标记的 **本地开发包**。辅助版本由 MSBuild 真正编译，不能修改元数据冒充另一个版本；源码须与主版本相同。打包核对 WinUI `.pri`/`.xbf`，在 ZIP 前自动从最终交付目录复制到中文空格隔离路径，执行原交互回归和跨版本单实例实测；失败保留报告并停止。测试前需正常关闭既有用户窗口，脚本只清理自己启动的进程。安装 EXE、完整便携离线环境、安全更新及全部门禁仍由 T05/T06 完成；不是批准发布的候选，脚本不上传。

`Test-DeliveredLauncher.ps1` 还会直接在最终交付路径执行 EXE、从另一工作目录二次启动并正常退出自启主进程，核对原 PID/HWND 及构建身份。此步骤在 ZIP 前完成；实际启动生成的 `AutumnOS_Data` 留在本地目录，打 ZIP 明确排除它，不删除数据。

## 当前行为

- 默认普通 WinUI 窗口，可最大化，显示集中品牌与关于信息。
- 同一 Windows 用户、同一登录会话内，参与 v1 启动协议的发行副本共用一个主实例。再次启动只召回原窗口；最小化恢复，已有页面、最大化状态和游戏实例保持不变，后台游戏不会自动切回前台。先启动副本继续使用自己的版本与数据目录；转发进程不初始化另一套数据。关闭按钮仍正常退出，没有新增托盘或开机自启。
- 协调超时或权限失败时本次启动退出，不能因此另开主窗口；Windows 拒绝前台激活时请求任务栏提醒。历史包未实现协议，不在自动协调范围内；普通用户与提权启动混用可能被拒绝。详情、退出码和更新维护边界见 [单实例 ADR](docs/adr-0003-launcher-single-instance.md)。
- 每次检查实际入口旁 `AutumnOS_Data`，只创建缺失目录；中文/空格路径可用，工作目录不影响数据位置。只读或路径冲突返回可恢复错误。
- 首启全窗口 hello → 品牌 → 初始化引导 → 壁纸桌面；每步保存检查点，完成后重启直达桌面。已有记录、损坏记录和未来版本记录不会被无声重置。
- 桌面包括实际时钟、应用图标网格、Dock 和图标上的“运行中”标记。元素配对图标只在真实安装记录及资源验证成功后出现；未运行时单击打开，返回桌面保持同一会话，双击后台图标打开“继续游戏 / 结束游戏”操作面板。继续恢复同一实例；确认结束后清除状态，下次单击新建实例。左键按住 600 ms 或右键菜单提供相同操作，超过 8 DIP 移动取消长按；Shift+F10 等价，Escape 关闭。
- 设置继续使用双栏；新增账号、应用权限与数据页，通知页面接入真实授权通知、角标、去重与静音。主题 light/dark/system 与三套原创壁纸保留；控制中心未接通项目仍如实说明。Windows 减少动态效果与当前主题/DPI 通过 SDK 同步。
- 公开 Logto 配置直接复制自 `autumnos-spec/config/logto.public.json`；账号使用系统浏览器 PKCE、独占回环与受保护凭据。Native 类型及登录回调已由派蒙确认，退出回调尚待确认。真实认证验收以进度和报告为准，公开发现校验始终不代表已登录。
- 构建脚本从 `samples/element-pairs` 与 SDK JS 确定性生成包，单独 `dotnet build` 前可运行 `./scripts/New-SamplePackage.ps1`。新版样例 0.3.0 保留同 appId 的 0.1.0/0.2.0 文件和游客存档，加入默认折叠的资料/权限、私有文件和桌面扩展真实示例。
- SDK/宿主没有累计 4096 次封顶；16 并发、32 KiB、滚动每分钟 120 次，以及宿主完成后 10 分钟、2048 容量的去重保留。超过窗口的手动重放可能再次执行，详见 SDK 合同。
- 普通第三方包仍待网络全通道隔离验收；开发模式另提供受控项目的独立来源预览、有限打包与脱敏调试。关闭模式撤销预览/调试能力，普通游戏继续。拒绝原生 EXE/DLL、越界路径、未知权限和坏包；没有认可名单。
- 数据按账号与应用来源隔离；权限持久保存并可撤销。诊断中可检查真实存档/配置备份并明确恢复，保留原记录；SDK 文件只通过系统选择器与有期限的能力句柄访问。

## 数据、规范与协作

`AutumnOS_Data` 含 Apps、Packages、Downloads、Cache、AppData、Saves、Screenshots、Config、Logs、Runtime、Temp、Updates。日志采用固定事件与错误码，不接收任意异常、令牌或用户文本。数据不会进入源快照和开发包。

工作规范位于 `autumnos-spec/`；原始解压包与 ZIP 完整保留。未建立 Git，没有远端仓库或云端 CI 依赖。SDK、NuGet 与产品的只读联网能力不代表源码上传授权。

完整范围保留：内部 `.autumn` 应用、iPad 风格桌面、GitHub 发现 `topic:autumnos-app`、自标分类 `autumn-app-pm` / `autumn-app-sq`、版本筛选、代理、`paimeng5201314/autumnlab` 的 plus/meta 安全更新、完整 SDK 与开发文档。没有认可名单；分类不授予安全信任。

文档入口：[架构](docs/architecture.md)、[数据基础](docs/data-foundation.md)、[Logto 配置](docs/logto-configuration.md)、[依赖与许可](docs/dependencies.md)。没有从历史远端取回或伪造项目 LICENSE；现有开工包文件原样保留，公开分发许可仍待项目所有者确定。
