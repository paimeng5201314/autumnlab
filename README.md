# Lab Chronicles AutumnOS · plus0.0.4-20261017

**制作人：派蒙 · plus0.0.4-20261017 · Windows x64 稳定通道**

AutumnOS 是使用 C#、.NET 10 和 WinUI 3 构建的原生 Windows 客户端，提供桌面、个性化、账号、存档、GitHub 应用发现，以及在程序内部运行的 `.autumn` 应用。用户入口为 **AutumnOS.exe**。

本次稳定版从已验证的 ChemLeap03 产品源码基线重新构建，用户界面与发行名称统一为完整标识 `plus0.0.4-20261017`。后续尚未完成集成验收的第三方运行时隔离改动不包含在本版中。稳定通道表示本次发行分类，以下已知限制仍然适用。

## 下载与使用

从 [plus0.0.4-20261017 发布页](https://github.com/paimeng5201314/autumnlab/releases/tag/plus0.0.4-20261017) 下载 `AutumnOS.exe`，放在用户可写的普通目录中双击运行。对应源码包为 `AutumnOS-plus0.0.4-20261017-source.zip`，发行记录目录为 `release/plus0.0.4-20261017/`。目标平台为 Windows 10 22H2 / Windows 11、x64；具体实测范围见下文。

单文件包含运行所需的 .NET、Windows App SDK、WebView2 和资源。首次运行会在旁边创建 `AutumnOS_Data` 并展开运行资源；后续启动复用已有数据。移动程序时同时保留这个目录，已有用户请先备份数据，不要删除其中的存档或运行文件。为避开已知的超长展开路径问题，建议使用较短目录。

- 首次启动依次进入资源准备、多语言 hello、手写 autumnOS、个性化与独立账号页；登录可以跳过。
- 游戏内按 **Esc** 返回桌面并保留当前游戏；**Ctrl+Shift+Q** 请求结束游戏并显示确认；桌面按 **F6** 打开多任务。
- 再次打开程序会激活同一 Windows 用户会话中的已有主实例。

## 本版更新

- 资源准备窗口采用与程序一致的原生界面；欢迎页增加多语言反向滚动和品牌手写动画，并支持减少动态效果。
- 个性化与登录拆分为独立步骤，修复主题切换及布局问题；登录提供取消、重试和跳过。
- 游戏内容使用完整视口，通过快捷键返回桌面或请求结束游戏，保留原会话和确认流程。
- 主程序每次实际启动检查更新。关闭“接受预览版邀请”时使用 GitHub Latest 稳定版；开启后同时考虑稳定版和预览版，以语义版本比较选择更新。
- 安装前展示目标版本的发布说明，经用户确认后下载。设置提供指定版本、兼容回滚与版本固定；关闭预览开关不会自动降级。
- 保留内部应用、隔离存档、权限管理、GitHub 商店发现及开发者 SDK。仓库分类为作者自标，不代表安全认证，也不授予额外权限。

## 构建与验证记录

本版构建 ID：`T06-20261017-stable-03`。所用产品源码基线为 `T06-20261017-chemleap-03`，基线快照：

```text
sha256:50cf737f7011fa97572c0c31fbd3e95b6ed39d68752b8c244aed382b7c6274b2
```

版本参数和公开文档随本次发行更新；新构建身份及产物以以下实测结果为准，基线快照不是新 EXE 的哈希。

| 本次稳定版项目 | 结果 |
| --- | --- |
| 发行版本／标签 | `plus0.0.4-20261017` |
| 发行记录目录 | `release/plus0.0.4-20261017/` |
| 新源快照 | `sha256:18dab870b97cc5785213a4f666205348fb33d4c2253263d988e8170650461e45` |
| Windows Release 构建 | `passed；0 警告、0 错误` |
| C# 测试 | `768/768 passed` |
| SDK／机器契约／示例契约 | `SDK 22/22；机器契约 358/358；示例 6/6；ChemLeap 模型 50/50 passed` |
| 独立身份客户端／HTTP 负例／开发 CLI | `21/21；7/7；12/12 passed（合成身份环境）` |
| 本次 EXE 原生验证 | `49/49 passed；正常退出及零测试进程残留` |
| EXE 大小（字节） | `488734276` |
| EXE SHA-256 | `94ce904433866e4d54d9c5ac3f2b0afb9de1008003391500cb8417850d914b37` |
| 源码包 | `AutumnOS-plus0.0.4-20261017-source.zip` |
| 源码资产校验 | `见同一 Release 的 SHA256SUMS.txt` |

历史补充证据：ChemLeap03 的 C# 测试 768/768、游戏浏览器测试 14/14、游戏原生测试 16/16、仓库原生测试 29/29、单文件原生测试 47/47 通过。临时测试信任根下的本地目录更新演练通过 133 项（更新流程 80 项、手动回滚及版本固定 53 项）。这些计数均属于历史构建，不作为 `stable-03` 重测结果；目录更新演练也不代表生产更新或单文件外层入口升级通过。

## 已知限制

- **账号服务：** 客户端已实现系统浏览器 OIDC/PKCE 流程和首设账号页。2026-10-17 较早探测曾返回 HTTP 502；本次稳定构建在当日 18:38（UTC+8）通过公开发现文档校验，真实账号登录往返仍未验证。离线使用和跳过登录不受此验收缺项影响。Native 客户端类型及登录回调已由用户确认，退出回调登记仍待确认；发现文档及合成身份测试不验证登记状态，也不等于真实登录成功。
- **生产更新：** 正式更新信任公钥及签名资产尚未配置，客户端保持拒绝不可信安装。生产 GitHub 更新及单文件外层入口完整升级／回退尚未通过验收。发布一个稳定版 Release 不会解除这些保护。当前 EXE 未使用 Authenticode 签名。
- **普通第三方应用：** 完整网络通道隔离仍待验收，普通社区包执行保留安全门禁；商店发现、下载或安装成功不代表运行权限已经授予。受控开发预览不替代普通第三方验收，也没有仓库认可名单。
- **发行范围：** 本次提供单文件 EXE 和源码，安装器暂缓。干净系统、Windows 10、跨硬件、DPI、触摸及完整人工辅助功能矩阵尚未全部验证；完整 T06 验收仍未完成。已知超长路径可能导致首次资源展开失败。
- **许可：** 第三方许可随程序和源码保留；仓库原有 LICENSE 原样保留。

## 从源码构建

需要 Windows x64、PowerShell 7、.NET SDK **10.0.401**、相应 Windows SDK / WinUI 构建工具和 Node.js。先运行环境检查确认缺项；项目脚本优先使用 `.tools/dotnet`，也可使用符合 `global.json` 的系统 SDK。首次恢复依赖需要访问官方源，保留所有依赖锁文件。

构建命令中的 .NET／兼容内部版本字段用于已有应用的兼容检查，发行显示名称由完整的 `ReleaseLabel` 决定。

在源码根目录执行：

```powershell
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Test-Source.ps1
$buildId = 'T06-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-stable'
./scripts/Build.ps1 -BuildId $buildId -BuildVersion 0.5.1 -ReleaseLabel plus0.0.4-20261017
./scripts/Test.ps1
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Package-SingleFile.ps1 -BuildId $buildId
./scripts/Test-SingleFileSmoke.ps1 -BuildId $buildId -ReportDirectory "artifacts/reports/$buildId-single-file"
```

生成的单文件位于 `artifacts/single-file/<build-id>/AutumnOS.exe`。构建身份、源文件哈希和测试记录位于 `artifacts/builds/<build-id>/`；重新构建会产生自己的构建 ID 和产物哈希。运行完整测试及原生验证前正常关闭其他 AutumnOS 实例，各组测试串行执行。源码包不包含本机 SDK、用户数据、凭据、测试私钥或浏览器缓存。

开发文档：[架构](docs/architecture.md)、[SDK](docs/versions/0.5.1/sdk-reference.md)、[Logto 配置](docs/logto-configuration.md)、[依赖与许可](docs/dependencies.md)、[本轮界面与更新改造](docs/local-refresh-20261017.md)。
