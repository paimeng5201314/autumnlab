# Lab Chronicles AutumnOS

**制作人：派蒙**

2026-10-24：派蒙已授权将本地工程与预览交付上传到本仓库。当前预览为 `meta-v0.5.2-local.9`（显示版本 `0.0.5-20261024-update`），详情见[本次交付说明](release/meta-v0.5.2-local.9/RELEASE_NOTES.md)与[下载页](https://github.com/paimeng5201314/autumnlab/releases/tag/meta-v0.5.2-local.9)。本版仍未获Windows发布者签名，真实GUI升级与第三方完整隔离尚未验收，ChemLeap普通模式仍受限制。


2026-10-24 工作方式更新：先交付代码修复、自动核验和独立测试包，真实窗口/新进程隔离检测由派蒙执行。入口、范围和步骤见[实测说明](docs/isolation-manual-verification.md)。代码核验包由 `Package-CodeHandoff.ps1` 独立生成，原生验收保持 `not_run`，不绕过正式打包门禁或覆盖根入口。

2026-10-18 隔离修复进行中：根入口和真实数据仍保留原样。当前源码已补网络策略、文档失效及SDK响应检查，但真实剪贴板copy/cut隔离失败；所有外部安装包及开发预览在创建浏览器前拒绝，提示 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`。开启开发模式、修改包声明或报告不能解除。安装和存档保留，ChemLeap普通模式运行尚未达标。`artifacts/isolation-20261018/`中的中间EXE用于自有探针诊断，不能当作第三方放行版本；实际结果见[隔离模型](docs/runtime-isolation.md)和[进度](AUTUMNOS_PROGRESS.md)。

这是本地 C# / .NET 10 / WinUI 3 原生 Windows 工程，目前处于 T06 本地联调。2026-10-17 本轮合入主程序更新与回滚逻辑，改造原生准备界面、多语言欢迎、手写品牌、独立账号页和游戏快捷键；没有上传或发布。旧阶段缺项及完整发行验收继续保留。实现与复现方法见 [本轮说明](docs/local-refresh-20261017.md)，实际构建 ID、哈希和测试结果见 [AUTUMNOS_PROGRESS.md](AUTUMNOS_PROGRESS.md)。

## 本地构建与运行

在工程根用 PowerShell 7 执行。项目内 SDK 已获授权安装，位于 `.tools/dotnet`，不修改系统 PATH。首次恢复需要连接微软/NuGet 官方源；后续锁定依赖。

```powershell
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Test-Source.ps1
$buildId = 'T06-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
./scripts/Build.ps1 -BuildId $buildId -BuildVersion 0.5.2-local.9 -ReleaseLabel 0.0.5-20261024-update
./scripts/Test.ps1 -ProbeLogto
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Package-SingleFile.ps1 -BuildId $buildId
./scripts/Test-SingleFileSmoke.ps1 -BuildId $buildId -ReportDirectory "artifacts/reports/$buildId-single-file"
```

单文件交付位于 `artifacts/single-file/<build-id>/AutumnOS.exe`，可单独复制；首次运行在旁边展开唯一的 `AutumnOS_Data`。搬迁已有存档时同时保留该数据目录。`artifacts/packages/` 内的目录版仍需保留 DLL 和资源，不能只复制其小型入口 EXE。`artifacts/latest-build.txt` 指向最近通过的构建 ID；构建/测试日志、源文件清单和 SHA-256 位于 `artifacts/builds/<build-id>/`。原生验证脚本只清理自己启动的进程，测试前正常关闭其他 AutumnOS 窗口。

若 NuGet 传输反复 EOF，可运行 `./scripts/Cache-OfficialPackages.ps1` 从同一官方源按锁文件下载并验证签名与 NuGet 内容哈希，再重试恢复；不关闭 TLS 校验。缺少项目内 SDK 时，先取得安装授权，再执行 `./scripts/Install-LocalSdk.ps1`。现有锁文件不可为绕过失败而删除。

早期 `Build-ProtocolPeer.ps1` / `Package.ps1` 跨版本协议回归仍保留，不能替代上面的当前单文件验证。构建必须来自真实源码，失败保留报告，不修改元数据冒充版本。安装包按用户既有指示暂缓；正式签名、系统兼容矩阵和完整 T06 验收仍以进度中未完成项为准。本地脚本不上传或公开发布。

`Test-DeliveredLauncher.ps1` 还会直接在最终交付路径执行 EXE、从另一工作目录二次启动并正常退出自启主进程，核对原 PID/HWND 及构建身份。此步骤在 ZIP 前完成；实际启动生成的 `AutumnOS_Data` 留在本地目录，打 ZIP 明确排除它，不删除数据。

## 当前行为

- 默认普通 WinUI 窗口，可最大化，显示集中品牌与关于信息。
- 同一 Windows 用户、同一登录会话内，参与 v1 启动协议的发行副本共用一个主实例。再次启动只召回原窗口；最小化恢复，已有页面、最大化状态和游戏实例保持不变，后台游戏不会自动切回前台。先启动副本继续使用自己的版本与数据目录；转发进程不初始化另一套数据。关闭按钮仍正常退出，没有新增托盘或开机自启。
- 协调超时或权限失败时本次启动退出，不能因此另开主窗口；Windows 拒绝前台激活时请求任务栏提醒。历史包未实现协议，不在自动协调范围内；普通用户与提权启动混用可能被拒绝。详情、退出码和更新维护边界见 [单实例 ADR](docs/adr-0003-launcher-single-instance.md)。
- 每次检查实际入口旁 `AutumnOS_Data`，只创建缺失目录；中文/空格路径可用，工作目录不影响数据位置。只读或路径冲突返回可恢复错误。
- 首启为原生资源准备 → 多语言 hello → 手写 autumnOS → 个性化 → 可选账号 → 桌面。账号可取消、重试或跳过；完成后重启直达桌面。已有记录、损坏记录和未来版本记录不会被无声重置。
- 游戏内容铺满视口，Esc 返回桌面并保留同一实例和未保存输入；Ctrl+Shift+Q 请求结束并保留确认，F6 在桌面打开多任务。原生输入桥只对当前窗口及当前游戏的实际 WebView 进程生效，不记录键盘文本。
- 主程序每次实际启动检查更新。关闭预览以 GitHub Latest 稳定版为自动候选，开启后同时考虑预览；更新前展示对应发布说明，明确确认后下载，再主动重启安装。设置可指定版本或兼容回滚、固定版本；正式可信公钥和签名资产未配置时拒绝安装。
- 桌面包括实际时钟、应用图标网格、Dock 和图标上的“运行中”标记。元素配对图标只在真实安装记录及资源验证成功后出现；未运行时单击打开，返回桌面保持同一会话，双击后台图标打开“继续游戏 / 结束游戏”操作面板。继续恢复同一实例；确认结束后清除状态，下次单击新建实例。左键按住 600 ms 或右键菜单提供相同操作，超过 8 DIP 移动取消长按；Shift+F10 等价，Escape 关闭。
- 设置继续使用双栏；新增账号、应用权限与数据页，通知页面接入真实授权通知、角标、去重与静音。主题 light/dark/system 与三套原创壁纸保留；控制中心未接通项目仍如实说明。Windows 减少动态效果与当前主题/DPI 通过 SDK 同步。
- 公开 Logto 配置直接复制自 `autumnos-spec/config/logto.public.json`；账号使用系统浏览器 PKCE、独占回环与受保护凭据。Native 类型及登录回调已由派蒙确认，退出回调尚待确认。真实认证验收以进度和报告为准，公开发现校验始终不代表已登录。
- 构建脚本从 `samples/element-pairs` 与 SDK JS 确定性生成包，单独 `dotnet build` 前可运行 `./scripts/New-SamplePackage.ps1`。新版样例 0.3.0 保留同 appId 的 0.1.0/0.2.0 文件和游客存档，加入默认折叠的资料/权限、私有文件和桌面扩展真实示例。
- SDK/宿主没有累计 4096 次封顶；16 并发、32 KiB、滚动每分钟 120 次，以及宿主完成后 10 分钟、2048 容量的去重保留。超过窗口的手动重放可能再次执行，详见 SDK 合同。
- 普通第三方包及开发预览均因缺少完整剪贴板边界而拒绝执行；有限校验、打包和已实现的脱敏调试接口保留，开发模式不能绕过。拒绝原生 EXE/DLL、越界路径、未知权限和坏包；没有认可名单。
- 数据按账号与应用来源隔离；权限持久保存并可撤销。诊断中可检查真实存档/配置备份并明确恢复，保留原记录；SDK 文件只通过系统选择器与有期限的能力句柄访问。

## 数据、规范与协作

`AutumnOS_Data` 含 Apps、Packages、Downloads、Cache、AppData、Saves、Screenshots、Config、Logs、Runtime、Temp、Updates。日志采用固定事件与错误码，不接收任意异常、令牌或用户文本。数据不会进入源快照和开发包。

工作规范位于 `autumnos-spec/`；原始解压包与 ZIP 完整保留。未建立 Git，没有远端仓库或云端 CI 依赖。SDK、NuGet 与产品的只读联网能力不代表源码上传授权。

完整范围保留：内部 `.autumn` 应用、iPad 风格桌面、GitHub 发现 `topic:autumnos-app`、自标分类 `autumn-app-pm` / `autumn-app-sq`、版本筛选、代理、`paimeng5201314/autumnlab` 的稳定/预览安全更新、完整 SDK 与开发文档。没有认可名单；分类不授予安全信任。

文档入口：[架构](docs/architecture.md)、[数据基础](docs/data-foundation.md)、[Logto 配置](docs/logto-configuration.md)、[依赖与许可](docs/dependencies.md)。没有从历史远端取回或伪造项目 LICENSE；现有开工包文件原样保留，公开分发许可仍待项目所有者确定。
