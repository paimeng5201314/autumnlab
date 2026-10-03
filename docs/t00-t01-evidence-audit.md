# T00 / T01 本地证据核查

制作人：派蒙。核查日期：2026-10-01（Asia/Shanghai）。工作根：`D:\特殊\chem`。

**结论：T00 的阶段完成条件有真实 Windows 构建、标准用户窗口和数据验证证据。T01 已有真实内部游戏、SDK、存档及修复后的开发包启动证据，但完整阶段验收仍为 `in_progress`。** 本轮用户明确授权继续制作 T02 中间检查点；这是本轮工作范围授权，不等于 T01 已通过，不取消 T01 剩余测试，也不构成正式发布授权。

本次仅读取本地规则、规范、报告、日志、快照和产物，并编写本报告。没有重新构建、启动产品、运行产品测试、联网或执行 Git 操作。下述历史命令来自已有进度和对应落盘证据；本次新增核查是报告交叉比对与只读哈希计算。所有路径均相对于上述工作根。

## 证据层级与来源

已读取根 `AGENTS.md`、`LOCAL_WORKFLOW.md`、`CODEX_START.md`、`autumnos-spec/AGENTS.md`、`AUTUMNOS_PROGRESS.md`、T00/T01 阶段文件，以及涉及 R001–R006、R032、R035、R036、R053、R057 的需求和验收定义。未发现 `docs/` 下额外 AGENTS.md。

| 证据 | 可以说明 | 不能据此宣称 |
|---|---|---|
| `Test-Source.ps1`、计划/公开配置校验 | 文件可解析、脚本语法、规范数量和静态字段约束 | Windows 产品运行、网络隔离、真实登录、完整功能验收 |
| `build-result.json`、`build.log` | 指定源快照的 Windows Release 编译结果 | 交付目录资源完整、ZIP 解压后可启动 |
| C# `tests.json`、Node `sdk-tests.log` | 实际执行的逻辑、文件系统、协议测试 | 物理输入设备、中文 IME、浏览器所有原生网络通道 |
| 构建输出 `windows-smoke.json` | 复制自构建输出的原生窗口与样例操作 | 另一套打包目录或 ZIP 的启动能力 |
| `package-verification.json` | ZIP 文件与交付目录一致、资源/用户数据检查 | 单凭哈希无法证明那个目录是可启动的 |
| `package-smoke/windows-smoke.json` | 复制自实际交付目录的隔离副本通过窗口/游戏检查 | Windows 10、干净机器、安装 EXE 已通过 |
| `delivered-launch.json` | 指定交付目录 EXE 在记录时直接启动并观察到窗口 | 此后一直存活、长期稳定性或全部产品功能 |

## 构建与源文件关联

| 构建 ID | source_snapshot_id | 清单文件数 | Release / C# / 窗口证据 |
|---|---|---:|---|
| `T00-20261001-foundation-03` | `sha256:6b1b000d184163234bb1f14c816d9ef3a6ab7b5003870649a88c571d6cc06485` | 55 | Release passed，0 警告/0 错误；C# 81/81；构建输出窗口 22/22 |
| `T01-20261001-internal-01` | `sha256:74d4ece59f3a5e20b453f3b4987096e2a63dc7100aeb69ae2f95c4a800ee9ade` | 78 | Release passed，0 警告/0 错误；C# 132/132；构建输出窗口 31/31；旧交付包随后启动失败 |
| `T01-20261001-packagefix-01` | `sha256:c1274fbe3ec48f83195c5bb318500db2f6429b506c72b4091b9336cc0c7c717e` | 78 | Release passed，0 警告/0 错误；C# 132/132；交付目录窗口 29/29；直接启动 passed |

每行证据根为 `artifacts/builds/<构建ID>/`，均记录 `commit=not_applicable`、`configuration=Release`、`rid=win-x64`、SDK `10.0.401`、系统 `Microsoft Windows NT 10.0.26200.0`。原生 smoke 的 `isElevated=false`。

已比对各构建的 `source-snapshot.json`、`after-build/`、`before-test/`、`before-package/` 中源 ID；两次 T01 的 `after-delivery/` ID 也一致。本次按清单声明的规范格式重新计算三份来源清单摘要，均匹配其 ID。它验证保存的清单内部一致性；没有把正在继续开发的当前源目录冒充这些历史快照，也没有重新确认每个历史源文件仍与当前源码相同。

环境报告 `artifacts/reports/environment.json` 记录 Windows 11 专业版 10.0.26200、PowerShell 7.6.5、非提权、项目内 SDK、WebView2 Runtime 154.0.4258.48；没有系统 Windows SDK，采用固定 NuGet BuildTools。该机器装有开发工具/运行组件，不能充当无开发环境的干净机器。

## 已记录的真实命令与结果

以下为历史执行入口，完整报告和日志位于对应构建证据根。命令中的 `<...>` 是说明性路径占位，不声称保存了逐字 shell transcript。

| 历史命令 / 操作 | 实际结果及边界 |
|---|---|
| `./scripts/Get-Environment.ps1` | 生成上述环境记录；只报告环境 |
| `./scripts/Build.ps1 -BuildId T00-20261001-foundation-03` | `build-result.json` passed，`build.log` 0 警告/0 错误；`tests.json` 实际 81 条，失败 0 |
| `./scripts/Test-WindowsSmoke.ps1 -ReportDirectory artifacts/builds/T00-20261001-foundation-03/windows-smoke` | 22 项 passed、3 张截图；实际 EXE 来源是 `src/AutumnOS.Shell/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/` |
| `./scripts/Build.ps1 -BuildId T01-20261001-internal-01` | locked restore 与 Release passed，0 警告/0 错误 |
| `./scripts/Test.ps1 -ProbeLogto`（internal-01） | C# 132/132；Node v24.19.0 SDK 12/12；公开 OIDC discovery passed，`login=not_run`、`registration=unverified` |
| `./scripts/Test-WindowsSmoke.ps1 -ExerciseRuntime -ReportDirectory artifacts/builds/T01-20261001-internal-01/windows-smoke` | 构建输出副本 31 项 passed，8 张截图；不是交付目录启动验证 |
| `./scripts/Package.ps1`（internal-01）及本地 ZipArchive 逐项 SHA-256 比较 | 旧 ZIP 551 个文件与旧交付目录一致；没有包含用户数据。后续真实启动失败，不能继续称该包可用 |
| `./scripts/Build.ps1 -BuildId T01-20261001-packagefix-01` | Release passed，0 警告/0 错误；构建前后来源 ID 一致 |
| `./scripts/Test.ps1`（packagefix-01） | C# 132/132，失败 0；SDK 12/12，失败/取消/跳过 0。本轮没有重跑 Logto discovery |
| `./scripts/Package.ps1`（packagefix-01） | 自动执行 `Test-WindowsSmoke.ps1 -ExecutablePath <新交付EXE> -ExerciseRuntime -ReportDirectory artifacts/builds/T01-20261001-packagefix-01/package-smoke`；29 项 passed，8 张截图 |
| 本地 ZipArchive 逐项 SHA-256 比较（packagefix-01） | 554 文件与交付目录匹配；必需 WinUI 资源存在；ZIP 无 `AutumnOS_Data` |
| `Start-Process -FilePath <新交付EXE> -WorkingDirectory <EXE目录> -WindowStyle Normal -PassThru` | `delivered-launch.json`：2026-10-01 13:43:19 +08:00 启动，进程 2316，观察 8 秒仍存活，窗口句柄 328954，标题 `Lab Chronicles AutumnOS`，数据目录存在 |
| `./scripts/Test-Source.ps1` | internal-01 的 `final-source-check.json` 保存 29 项 passed，明确 `product_tests_executed=false`；packagefix 进度另记录再次执行通过 |
| 已有 Python 执行 `autumnos-spec/tools/check_plan.py` / `check_public_config.py` | internal-01 的 `plan-validation.json` / `public-config-validation.json` 均 passed；计划为 57 需求、65 验收、7 任务；脚本没有运行 Windows 构建、产品测试或登录 |
| `./scripts/New-SourceSnapshot.ps1 -OutputDirectory artifacts/builds/T01-20261001-packagefix-01/after-delivery` | 保存的源 ID 与该次构建一致 |

SDK 数量来自对应 `test-runs/<运行ID>/sdk-tests.log`，因为 `sdk-tests.json` 仅记录状态、退出码、Node 版本，不含数量。packagefix 的运行目录是 `test-runs/0fc8116dff094b90849eea0d16a5bc9c/`，internal-01 是 `test-runs/a04b49ea41b64c829076e1b85e49a5bd/`。脚本实际调用 `node --test tests/sdk-tests.cjs`，C# 运行 `AutumnOS.Tests.dll --report <tests.json>`。

C# 132 项中含 Runtime 26 项、Packages 23 项、Storage 21 项，其余为身份配置/发现和公共合同测试。测试中有真实 Windows ACL、junction、文件锁、只读文件和原子存档行为；这不能代替真实 WebView2 网络隔离或产品账号切换。

29 项交付 smoke 与 31 项构建 smoke 的差异是交付路径没有执行两项默认构建路径身份检查（`binary_matches_tracked_build_identity`、`executable_matches_tracked_hash`），不是游戏操作测试被取消。交付脚本另有产物字节校验，报告须与 `package-result.json`、`artifact-hashes.json` 一起解读。

## 交付包真实性与旧包失败

`artifacts/builds/T01-20261001-packagefix-01/original-package-launch-failure.json` 明确保存旧包失败：进程 6492、退出码 `-1073741189`、异常码 `0xc000027b`、模块 `Microsoft.UI.Xaml.dll`；缺 `App.xbf`、`MainWindow.xbf`、`AutumnOS.pri`。原因记录为独立 `dotnet publish --no-build` 遗漏生成的 XAML 资源。报告同时明确 `previous_native_smoke_scope=build_output_only`、`previous_package_launch=not_run`。

因此 internal-01 的 Release、逻辑测试、构建窗口检查仍是有效的各自范围证据；其 ZIP 哈希一致性仅证明忠实复制了一个不完整目录，不能支持“旧交付 EXE 可运行”。进度文件已撤回旧入口可用声明。旧包和失败记录保留。

修复后本地开发入口为：

`artifacts/packages/T01-20261001-packagefix-01/AutumnOS-0.1.0-t01-win-x64-development/AutumnOS.exe`

ZIP 为：

`artifacts/packages/T01-20261001-packagefix-01/AutumnOS-0.1.0-t01-win-x64-development.zip`

`package-result.json` 记录大小 **90,973,419 字节**，SHA-256 **`07f25fdb337ecdb8e366c18620391ecb44782d510d008a7611df550737c7bdb4`**，`kind=local_development_bundle`、`release_candidate=false`、`public_release=false`。本次只读 `Get-FileHash -Algorithm SHA256 -LiteralPath <该ZIP>` 再算结果相同，并确认交付目录的 EXE、AutumnOS.dll、App.xbf、MainWindow.xbf、AutumnOS.pri 五项哈希均匹配该构建 `artifact-hashes.json`。实际 ZIP 解压后重新启动测试本次 `not_run`；已有 554 文件比较和交付目录启动证据不能被描述成另一项已执行测试。

根 `AutumnOS（本地开发版）.lnk` 的创建与目标记录在 `delivered-launch.json`；该文件不是独立可执行发行物。直接启动后的用户数据可存在于开发目录，历史 ZIP 检查仍记录不含用户数据。

## 其他失败与辅助证据

| 记录 | 核查结果 |
|---|---|
| `artifacts/builds/T00-20261001-foundation-01/build-result.json` | failed：解决方案实际输出 `bin/Release`，报告指向早先 `bin/x64/Release`；该构建被废弃，明确不作验证/交付声明 |
| `artifacts/reports/first-shell-build.log` | 曾有 PRI249 `Invalid qualifier: REGISTRATION-STATUS` 警告；后续三次跟踪构建的日志为零警告/零错误 |
| `artifacts/reports/t01-native-integration-01/windows-smoke.json` | failed：30 秒找不到通过无障碍暴露的 HTML 输入框；已记录 20 项通过，内部应用流程未完成 |
| `artifacts/reports/t01-native-integration-02/windows-smoke.json` | failed：30 秒未观察到实际游戏关闭；已记录 25 项通过 |
| `artifacts/reports/t01-native-integration-03/windows-smoke.json` | failed：`GetCurrentPattern` 返回 `Unsupported Pattern`；已记录 20 项通过 |
| `artifacts/reports/t01-native-integration-04/windows-smoke.json` | passed：29 项；`build_id=local-untracked`、`source_snapshot_id=not_recorded`，只作调试辅助证据 |
| 早期样例缺失 MSB3030 | `AUTUMNOS_PROGRESS.md` 记录此辅助构建失败；本次检索现有 `.log` 未找到对应 MSB3030 日志，不把进度叙述伪装成已读独立错误日志 |

T00 两次早期窗口 smoke 也是 `local-untracked`。审计使用 foundation-03 的最终跟踪证据，不把未跟踪调试结果补写进其他快照。

窗口报告的截图字段是 `captured_not_visually_reviewed`。internal-01 另有 `visual-review.json`，只记录审看 `06-restored-application.png` 和 `07-permission-overlay.png`，并明确设计人验收和完整 UI 矩阵未跑。packagefix 的进度写明已查看读档恢复页，但不把该叙述提升为全部截图视觉验收；本次审计未重新查看截图。

## 仍待执行的验收

| 范围 | 状态 / 需要的后续证据 |
|---|---|
| T01 浏览器原生网络隔离 | `not_run`：WebRTC/STUN、WebSocket、worker、框架、导航、下载、外部协议等真实通道负面测试；网关拒绝伪造消息不等于完整沙箱 |
| T01 实际挂起/恢复、崩溃与诊断恢复 | `not_run`：Runtime 状态与竞态单元测试不代替实际浏览器进程挂起或故障注入 |
| 物理键鼠/触控、中文 IME、DPI/缩放矩阵 | `not_run`：无障碍填入中文并读回，只证明 Unicode 数据流；最大化/恢复不覆盖多 DPI 或输入法组合文本 |
| 完整 TypeScript 与 JSON Schema 引擎验证 | `not_run`：SDK Node 用例包含 schema 文件可解析/样例约束检查，未执行 TypeScript 编译或完整 schema 验证器 |
| Windows 10 22H2、无开发环境/无预装 .NET 的干净系统、离线 WebView2 | `not_run`；当前 Windows 11 开发机结果不可外推 |
| 安装 EXE、正式签名、更新失败回退、物理断电 | `not_run`；开发 ZIP 不是这些发行门禁的替代品 |
| Logto Native/public 类型与两个回调登记 | `unverified`；公开配置/发现通过不验证控制台登记 |
| 真实浏览器登录、刷新、退出、账号切换与账号存档隔离 | `not_run`；游客存档和本地拒绝旧会话请求不构成真实登录 |
| 全流程 X01/X02/X03/X05/X06/X07 | `not_run`：GitHub 安装至安全更新、账号切换异步响应、安装崩溃、启动/更新竞争、干净系统双包、独立开发者接入 |

读取时 `acceptance-tests.json` 中 A002/A004/A005/A006 为范围限定的 passed；A001/A003/A032/A035/A036/A053/A057 仍为 not_run，虽附部分实现证据也没有完整通过。T00 阶段 verified 不能替代这些跨阶段完整断言。尤其部分 evidence 仍引用 internal-01 的旧包 551 文件描述，阅读时必须结合本报告的旧包失败及 packagefix-01 修复证据；本审计没有修改规范状态或历史记录。

T02 中间检查点的开发可依据本轮用户明确授权推进；T01 保持 in_progress。iPad 风格完整桌面、内部 `.autumn`、GitHub 应用发现、pm/sq 分类、版本筛选、代理、Logto 权限、存档、plus/meta 安全更新和完整 SDK/对接文档仍在总范围内，没有认可名单，也没有取消尚未验收的功能。
