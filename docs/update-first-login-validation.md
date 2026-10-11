# 主程序更新与首次登录改造：2026-10-10 本地阶段验收

制作人：派蒙。记录日期：2026-10-10（Asia/Shanghai）；环境日志使用 UTC。该阶段只修改本地 `/workspace/autumnlab`，当时没有提交、推送、创建 Release、触发远程构建或修改身份服务配置。

本页保留后来收录于提交 `c42c708` 的改造验证证据，比较基线为 `062773e4`。后续将该改造整合到发行基线 `f83e005` 时，保留了数据桥、日志轮转、Windows CI 等既有修复；这里的用例数量和通过结果不代表整合后的重新验收。整合后的构建、测试和发布状态必须以对应提交的实际 CI 与产物记录为准。

## 实现范围

- 官方 Latest 稳定候选与可选预览候选；SemVer 比较、签名 tag 映射、分页历史和精确禁用原因。
- 每次真正启动检查、并发合并、限流退避、条件请求、上次成功时间；登录和首次配置不阻塞元数据检查。
- 查看 Release 说明并明确确认以后才下载；精确版本身份绑定；下载和应用前重新核验；稍后重启绝不自动安装。
- 指定升级／回滚、成功以后固定版本／解除固定、关闭预览撤销未应用预览任务。
- 原独立更新器、单实例和维护协议；受支持同数据格式版本对的回滚；限定配置快照；单文件原入口持久绑定。
- 可跳过首次账号登录，老状态迁移，账号会话独立恢复，取消／重复点击／迟到回调保护。
- CLI 冻结负载后绑定真实 Release 与资产 ID、签名、验证；原生测试脚本同步明确确认步骤。

没有从文档第八节 A 的示例版本号推导发行号，没有修改产品版本号。只将更新协议版本提升为 `1.1.0`。读取到的实际 GitHub 标签为稳定 `meta`、预览 `meta0.0.2-20261007`；历史不透明标签只能由可信清单映射到可比较版本。

## 实际验证

| 项目 | 结果 | 证据 |
|---|---|---|
| .NET SDK | 官方 10.0.401 Linux SDK；官方 SHA-512 验证通过 | `work/validation/` 构建日志；SDK 仅装入工作目录 |
| 依赖 | 原 `packages.lock.json` 的 locked restore 成功，无依赖版本改动 | `restore-locked.log` |
| 测试工程、Update、UpdateProtocol、Identity 等 | Release 构建成功，0 错误；72 条 CA1416 提示为测试直接访问标为 Windows 的纯文件事务 API | `tests-build.log` |
| 本轮更新／回滚／引导与会话 | **67/67 通过**：40 更新、7 回滚、20 引导和会话 | `tests-final.json` 中对应前缀；`focused-results.json` 为相同运行结果摘录 |
| 完整 Linux 可执行 C# 用例集合 | **475/483 通过，8 项未通过**，详情见下节 | `tests-final.json` / `tests-final.log` |
| SDK / 合成示例交互 | **22/22、6/6 通过** | `sdk-direct-tests.log` / `sample-direct-tests.log` |
| 真实发布 CLI + 临时隔离密钥与合成负载 | **9/9 通过**，含 prepare、bind-release、RSA-PSS 签名、负载验证和篡改拒绝 | `update-cli-tests.json`；临时私钥已删除 |
| 修改过的 PowerShell 脚本 | **17/17 AST 语法解析通过**，未执行原生 Windows 流程 | `all-script-syntax.json` |
| 全部 Shell C# | 30 个真实源文件＋XAML 字段声明，221 个实际 Microsoft／工程引用；语义编译 0 错误，9 个引用兼容／生成字段警告 | `shell-csharp-result.json` / `shell-csharp-inputs.json` / `shell-csharp-compiler.log` |
| 完整 WinUI 构建 | **未通过环境门槛**：Linux 执行 Windows `XamlCompiler.exe` 报 `Exec format error` | `shell-build.log` |
| Windows 原入口、进程交接、健康启动、DPI／键盘、真实 DPAPI 与 Logto 登录 | **未运行**，当前只有 Linux 环境 | 不以逻辑夹具或 C# 语义编译替代 |

Shell 语义验证只补 XAML 生成的字段声明和空 `InitializeComponent`，所有 WinUI／Windows／WebView2 API 来自真实引用。生成 DLL 仅验证 C#，不能运行或打包为产品。已生成测试工程及 CLI 的 DLL，但**没有生成可交付的新版 `AutumnOS.exe`、Windows 截图或安装成功报告**。

本轮直接构建的测试报告保持真实 `build_id=local-untracked`、`source_snapshot_id=not_recorded`。交付的源码摘要用于审阅本次最终工作区，不冒充已执行完整 Windows `Build.ps1` 的正式构建身份。

## 完整用例未通过项

以下 8 项断言依赖 Windows 的共享占用错误码与只读目标替换语义，在 Linux 上失败，未删除或改成通过：

- `storage.busy_checkpoint_is_retryable`
- `storage.readonly_checkpoint_preserves_bytes`
- `desktop_preferences.concurrent_writer_is_retryable`
- `desktop_preferences.readonly_destination_preserves_commit`
- `desktop_layout.concurrent_writer_is_retryable`
- `desktop_layout.readonly_failure_preserves_commit`
- `runtime.busy_save_is_retryable`
- `runtime.readonly_save_preserves_commit`

在独立临时目录完整解包未修改的 HEAD `062773e4`，使用相同 SDK／依赖锁和本地网络权限构建并运行，得到 **430/439**。上述 8 项全部在基线复现；第 9 项是下述已修复的旧测试根标记问题。`baseline-tests.json`、`baseline-build.log`、`baseline-comparison.json` 保留原始结果，当前与基线共有用例没有新增失败。

另修正了一个测试根目录定位问题：原测试要求已被仓库历史删除的根 `AGENTS.md`，改用真实解决方案和构建属性定位，仍读取原始示例源码，没有替换成合成示例。最终该测试通过。第一次未开放测试进程的本地网络时，两个回环 HTTP 用例受到沙箱拒绝；最终以允许网络的执行权限运行真实本地夹具，两个用例均通过。没有访问真实游戏或账号数据。

## 命令与证据位置

执行环境中原始证据在 `/workspace/work/validation`；Shell C# 验证在 `/workspace/work/shell-csharp-check`。对外证据包只含源码改动、文档和脱敏验证结果，不含 SDK、NuGet 缓存、用户数据或测试私钥。

实际执行的主要命令（`dotnet` 为工作目录内已验证的 10.0.401；CLI home 和包缓存均在 `work`）：

```sh
pwsh -NoProfile -File scripts/New-StoreFixturePackages.ps1
pwsh -NoProfile -File scripts/New-SamplePackage.ps1
dotnet restore tests/AutumnOS.Tests/AutumnOS.Tests.csproj --locked-mode -p:NuGetAudit=false
dotnet build tests/AutumnOS.Tests/AutumnOS.Tests.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll --report /workspace/work/validation/tests-final.json
node tests/sdk-tests.cjs
node tests/t03-sample-tests.cjs
dotnet restore src/AutumnOS.Shell/AutumnOS.Shell.csproj --locked-mode -p:EnableWindowsTargeting=true -p:NuGetAudit=false
dotnet build src/AutumnOS.Shell/AutumnOS.Shell.csproj --no-restore -c Release -m:1 -nr:false -p:EnableWindowsTargeting=true -p:BuildProjectReferences=false -p:UseSharedCompilation=false
```

最后一条 WinUI 命令因 Windows XAML 编译器无法在 Linux 运行而失败；不能省略这个边界。独立 C# 检查使用 SDK 自带 Roslyn，响应文件和输入摘要在证据包中。

## Windows 本地复验与外部配置

1. 使用 Windows、PowerShell 7、锁定的 .NET SDK，执行 `scripts/Build.ps1` 和 `scripts/Test.ps1`；构建 ID 必须唯一。`BuildVersion` 与 `ReleaseLabel` 必须按实际 GitHub 发行身份和可信清单填写，不复制本文件测试夹具版本。需要只跑此次逻辑时，测试 DLL 支持 `--filter update.,rollback.,onboarding.,identity.local_restore,identity.revoked --report <路径>`。
2. 按 `docs/t05-release-engineering.md` 建立隔离 A/B 签名演练包，运行更新冒烟与故障脚本。检查确认前没有包下载、稍后与普通退出不安装、预览关闭撤销计划、同时运行游戏时只提示保存退出、精确目标撤回／换包中止。对同格式受支持版本完成升级、使用、回滚，并故意令健康启动失败检查程序与对应桌面快照恢复。
3. 对新外层单文件运行 `scripts/Test-SingleFileSmoke.ps1`：从原 EXE 正常启动更新后的代码，再模拟更新运行目录缺失，确认不会悄悄重新展开旧种子；真实入口和完整回滚仍需 Windows 实测。
4. 首次配置分别登录、取消、失败后跳过、直接跳过；真正完成桌面准备后重启不重复引导。旧 schema 1 完成状态直接进入桌面。用真实 Logto 检查系统浏览器回调、保持登录、离线、刷新、撤销及退出；当前没有修改线上配置或使用真实凭据验证。
5. 生产更新须由维护者提供有效生产公钥、签名产物和明确数据／协议兼容元数据；官方现有两个 Release 未提供这些资产，不能真实安装。测试密钥不可成为生产可信根。详情及首个完整 EXE 过渡步骤见发行说明。此处仅记录上述本地验证阶段，没有将后续发布视为已经完成。

## 2026-10-11 发行整合预检

制作人随后授权 GitHub 提交和发布，并要求 EXE 由 GitHub Actions 生成。发行源码以已发布预览的 `f83e005` 为基线合入 `c42c708`，保留数据桥、日志轮转、Windows 普通用户构建等既有修复。展示标签顺延为 `meta0.0.3-20261011`，内部版本由上一预览的 `0.5.1-local.5` 顺延为 `0.5.1-local.6`。

整合后使用同一已验证 Linux SDK、locked restore 和真实项目源完成 Release 测试工程编译，0 错误、72 条 CA1416 提示。完整 Linux 用例为 **532/540**，失败仍仅为上文列出的 8 项 Windows 文件语义断言；更新、回滚、引导和会话为 **68/68**，含新增带 `+build` 标签的安装源复验。SDK 与示例仍为 **22/22、6/6**。原始日志在 `/workspace/work/release-validation`，此阶段仍为 `local-untracked`，不替代 Windows CI。

Windows Actions 必须对精确提交重新编译、运行完整测试、打包 EXE；发布作业在验证同一次运行的产物、源摘要和 SHA-256 后创建预览 Release。实际 Windows 成绩与最终下载哈希以该 Actions 运行及 Release 附件为准，交互式 UI、真实 Logto 与兼容矩阵不由非交互 CI 代替。

## 2026-10-11 Windows Actions 实际结果

[运行 38126950961](https://github.com/paimeng5201314/autumnlab/actions/runs/38126950961) 已成功：发行提交 `65755778ebdf03aa61203ce72354d3cfceec7667` 的真实 WinUI Release 构建通过，完整 Windows C# **694/694**，全部要求的非交互测试组通过。Windows 用例包含凭据持久化、身份协议、独立更新器和安装事务；上文 8 项 Linux 文件语义差异没有成为 Windows 失败。

同次运行已生成完整 EXE 并发布 [meta0.0.3-20261011](https://github.com/paimeng5201314/autumnlab/releases/tag/meta0.0.3-20261011)。公开 `AutumnOS.exe` SHA-256 为 `4bf30b9405329aeedc4a8755476919f348d2e974029849ce32fe9b2702453cd8`，与 Actions、GitHub 资产摘要和 `SHA256SUMS.txt` 一致；公开 `release-build.json` 的摘要和源码／构建／测试身份也已核对。发布器在草稿阶段下载回读三个附件后才公开。

此次仍未运行 Windows 交互式 UI、真实 Logto、真实原入口更新重启或系统矩阵。EXE 未 Authenticode 签名，生产自更新可信根未配置，本次未提供签名更新清单及 ZIP；这些边界已写入 Release 说明。main 的发行记录与构建文档后补提交不改变已冻结的发行源码身份。
