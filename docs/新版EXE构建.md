# 从当前源码生成新版单文件 EXE

此入口默认构建 `0.5.1-local.5`，显示版本 `meta0.0.2-20261007`，每次自动生成唯一 T06 构建 ID。本地默认执行锁定恢复、构建、测试、打包和 Windows 原生烟测；无交互桌面的 Windows CI 必须显式跳过原生烟测，并将其记录为未执行。本说明不表示新版 EXE 已在 Linux 云环境生成。更改源码后，旧 EXE 不会自动包含修复。

## GitHub Actions

`.github/workflows/windows-exe.yml` 使用 `windows-2025`，固定 Node.js 24.19.0、PowerShell 7.6.6 与项目 SDK。`Invoke-CiBuild.ps1` 在临时普通用户账户下执行构建、全部非交互测试和打包，加载该用户配置文件；仅向该账户授予工作目录权限，随机密码只保留在内存中，结束后删除临时账户。推送 `codex/windows-exe-*` 构建分支时自动运行；工作流进入默认分支后也可从 Actions 页使用 Run workflow 手动触发并指定显示版本。无需添加发布令牌；工作流权限只有读取源码，上传产物使用 Actions 提供的运行凭据。

成功运行后，在该次运行的 **Artifacts** 下载 `AutumnOS-meta0.0.2-20261007-win-x64`（自定义标签时名称随之改变），其中含单文件 EXE、SHA-256 和源快照/打包记录。`Windows-build-evidence-<run>-<attempt>` 保存构建和测试报告，失败时也尝试保留日志。产物保留 14 天，未创建 Release。

云端显式使用 `-SkipNativeSmoke`；新版原生 UI 尚需从下载的 EXE 在普通用户桌面运行验收。Actions 构建 ID 包含 run ID 与重试次数，失败重跑不会复用本地证据目录。此工作流不向仓库写入构建文件，也不修改生产更新签名配置。

## 准备 Windows

使用完整源码目录，在 **Windows x64、普通用户的交互桌面** 打开 **PowerShell 7.6 或更新版本（.NET 10 或更新、x64）**。提供 **Node.js ≥22.9**，命令 `node --version` 应可用。PowerShell 与 Node 是本机前提，脚本不会安装全局组件。不要用 Windows PowerShell 5.1、管理员窗口或服务会话。

系统需要已有 .NET Framework 4.x 的 `Framework64/v4.0.30319/csc.exe`、`expand.exe`、`tar.exe`，用于编译单文件准备器与解包官方工具。WinUI/Windows SDK BuildTools 来自固定 NuGet 依赖，不要求把 Visual Studio 或 Windows SDK 全局安装。磁盘需容纳 SDK、依赖缓存、完整目录产物、单文件负载及测试展开副本；宜预留至少 15 GiB，可用空间不足时先停止构建。

先保存并关闭已有 AutumnOS，保持桌面解锁，原生烟测期间不要操作测试窗口。脚本会拒绝现有产品实例，并只测试新报告目录中的自有副本；不会清理真实 `AutumnOS_Data`。

## 执行

在源码根运行预检查；此模式不下载依赖、不构建、不启动产品：

```powershell
./scripts/Build-SingleFile.ps1 -CheckPrerequisitesOnly
```

满足前提后执行：

```powershell
./scripts/Build-SingleFile.ps1
```

无交互桌面的 Windows CI 使用下列显式模式。它仍要求普通用户身份，并执行当前构建全部非交互测试；无需打开交互桌面，只跳过原生 UI 烟测。GitHub runner 的管理员进程通过 `Invoke-CiBuild.ps1` 创建和启动专用普通用户进程：

```powershell
./scripts/Build-SingleFile.ps1 -SkipNativeSmoke
```

此模式的报告状态是 `packaged_native_not_run`，`native_tests` 和 `native_smoke` 均为 `not_run_ci_no_interactive_desktop`；没有 `native-smoke/single-file-smoke.json` 成功证据。下载后的 EXE 仍须在普通用户真实桌面验证，不能将 CI 打包成功当作 UI 验收通过。预检查也可组合 `-CheckPrerequisitesOnly -SkipNativeSmoke`。

实际顺序是：验证静态前提 → 固定 .NET SDK 10.0.401（SHA-512 校验）→ 固定 npm 11.6.1 与锁定合同依赖 → 固定 WebView2 154.0.4258.53（微软签名/版本验证）→ 构建（含锁定 NuGet 恢复、内置包与测试 fixture）→ 当前构建全部测试 → 完整目录准备 → 单文件打包 → 真实原生首启、游戏/保存、后台、重复启动与重启保留烟测。

首次联网访问 .NET 官方下载、nuget.org、registry.npmjs.org 与微软固定 WebView2 下载地址。源码包不含完整依赖缓存，因此不是离线构建包；证书、TLS、包摘要或签名失败时停止并保留证据，不能关闭验证。本地原生测试模式另检查 SDK 附带的 WindowsDesktop 10.0.12 UI 自动化程序集；缺少时报告阻塞，不随意代换另一个运行时。首次运行会创建 NuGet.Config 所需的空 `.tools/nuget-feed`，不改写依赖锁；历史 Project 版本范围本身不需要刷新锁。

成功后控制台给出 EXE 路径与 SHA-256：

```text
artifacts/single-file/<BuildId>/AutumnOS.exe
artifacts/build-pipelines/<BuildId>/pipeline.json
artifacts/build-pipelines/<BuildId>/pipeline.log
artifacts/build-pipelines/<BuildId>/native-smoke/single-file-smoke.json
```

构建、测试与逐文件打包证据另存于 `artifacts/builds/<BuildId>/`。失败时保留当前输出和日志，修正具体原因后以新 BuildId 重试；不要复用旧 ID、删除失败报告、跳过失败的非交互测试或手工修改锁文件。构建期间保持源码不变，依赖和报告仅写入 `.tools`、`artifacts` 与常规 bin/obj 输出；前后快照必须相同。

本地烟测通过仅证明该 Windows 机器上的指定流程。完整新布局更新回退、真实双账号/独立后端、全面社区隔离、干净系统/Windows 10/输入设备矩阵仍需各自验证。产物仍未做正式 Authenticode 签名，生产更新根未配置；日志中不会将这些缺项记成已完成。
