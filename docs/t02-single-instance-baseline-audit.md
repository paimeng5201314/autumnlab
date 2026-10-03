# 单实例增量前的 T02 只读基线审计

制作人：派蒙。审计日期：2026-10-01。工作区：`D:\特殊\chem`。

本次核对对象为已交付的 `T02-20261001-interactions-01`，不是单实例实现的验收。实际读取现有进度、源码、冻结报告、原始测试日志、ZIP 和截图；重新计算内容哈希。没有启动或关闭产品窗口，没有重跑 UI、构建、C# 或 Node 测试，没有修改旧源码、旧包和旧报告。唯一新增文件为本审计文档。

## 构建身份与实际字节

基线源 ID：`sha256:4476c5657ed189206ea0df22ce41ca83fbc5921626e40f7ff835624ff172e8d3`。报告根目录：`artifacts/builds/T02-20261001-interactions-01/`。commit 为 `not_applicable`。

2026-10-01 07:51:04 UTC 的只读比较结果：`source-snapshot.json` 包含 88 个文件，按原算法重新计算摘要，得到同一源 ID；逐个比较当时工作区的这 88 个文件，字节差异为 0。该时间点位于本审计文档写入之前；不能据此宣称稍后单实例改动仍具有旧源 ID。后续构建必须生成新快照。

8 份报告的 build_id/source_snapshot_id 一致：`build-result.json`、`tests.json`、`sdk-tests.json`、`package-result.json`、`package-verification.json`、`visual-review.json`、`package-smoke/windows-smoke.json`、`after-delivery/verification.json`。

| 核对对象 | 本次实际读回结果 |
|---|---|
| interactions ZIP | 91,004,859 字节，SHA-256 `12f884ca3de1ff418c50f49857cb98c8dfb6d8c3be0cfe46701e2abefbc6b554`，与冻结报告一致 |
| ZIP 内每个文件 | 重新读取 554 个文件的解压字节并计算 SHA-256，与 `package-verification.json` 及当前交付目录逐个对照，0 差异 |
| ZIP 用户数据 | 0 个 `AutumnOS_Data/` 文件；未把实际运行产生的数据重新打入 ZIP |
| 构建清单到交付 | 524 个有原构建哈希的交付文件全部一致，其余为交付附加许可/说明等文件；全部 554 个仍由 ZIP/交付清单核对覆盖 |
| 交付 EXE | SHA-256 `52dc9b926ec956e0ae404c7930efaef1954a3fce2a3e4b99432569aeec8e24b3`，与窗口测试报告及交付清单一致 |
| 更早的 desktop ZIP | `T02-20261001-desktop-01/AutumnOS-0.2.0-t02-checkpoint-win-x64-development.zip`，90,997,444 字节，SHA-256 `8ef872664b2cd0c292ffb8941113278f458c48c92aed4873fad3bc84305150f0`，与其原报告一致 |
| 原交互检查脚本 | `scripts/Test-DesktopInteractionSmoke.ps1`，SHA-256 `6bfd07e1ebb722cb3d0106b7504449dd973e062aeb7bab01540e13c5e881c715`，与进度记录一致 |

当前基线包路径：`artifacts/packages/T02-20261001-interactions-01/AutumnOS-0.2.1-t02-interactions-win-x64-development.zip`。目录中的实际入口为同名无 `.zip` 文件夹下的 `AutumnOS.exe`。这是本地开发检查包，`release_candidate=false`。

实际使用的只读命令与算法：`Get-Content -Raw | ConvertFrom-Json` 读取报告；`Get-FileHash -Algorithm SHA256` 核对磁盘字节；用 `[IO.Compression.ZipFile]::OpenRead(...)` 遍历 ZIP，各 Entry.Open() 流经 `[Security.Cryptography.SHA256]::HashData(...)`，不解压覆盖文件。源摘要按清单顺序拼接 `sha256 + 两空格 + 相对路径`，LF 分隔且无尾部 LF，再计算 UTF-8 SHA-256，与 `scripts/New-SourceSnapshot.ps1` 的原算法一致。

## 153 / 15 / 80 / 21 的真实出处

| 上轮执行内容 | 原始证据与本次核对 |
|---|---|
| Release 构建 | `build.log` 记录实际 `dotnet build AutumnOS.slnx -c Release -p:Platform=x64 --no-restore -p:BuildId=... -p:SourceSnapshotId=...`；结果 0 警告、0 错误。`build-result.json` 为 passed，SDK 10.0.401，Windows 11 10.0.26200，win-x64 |
| C# 153/153 | `tests.json` 的 results 实际含 153 条 passed、0 failed；其中 Runtime 29 条、外观存储 18 条。`runtime.long_session_12000_requests_keeps_bounded_replay_memory` 为 passed |
| Node SDK 15/15 | `sdk-tests.json` 记录 Node v24.19.0、退出码 0；具体 15 条及 pass 15/fail 0 位于 `test-runs/434b45334e9e47b89155e21845e36c4f/sdk-tests.log`。其中包括 12000 次调用、速率窗口和取消/超时释放容量 |
| Windows 80/80 | `package-smoke/windows-smoke.json` 的 checks 实际有 80 条 passed，非提权；对应交付目录副本，测试进程 6308、19944。80 是记录的检查次数，包含重启/导航重复检查，不冒充 80 项独立完整产品验收 |
| 21 张截图 | 窗口报告登记 21 个 captured 项，磁盘上有相应 21 张 PNG。`visual-review.json` 单独记录 9 张人工查看图的哈希，本次重新计算均一致；其余 12 张只有原捕获记录，未补造原人工审阅结论 |
| 交付目录直接启动 | `delivered-launch.json` 记录普通窗口进程 8508、句柄 24118372、8 秒后 Responding=true、无测试置顶。它是 07:27:22 UTC 的历史观察，不表示本次重新启动或持续监视 |

原自动化窗口测试使用 UI Automation 和归属受控的合成鼠标/键盘输入；仅测试自启窗口曾临时置顶，不能推断物理键鼠、触控或任意桌面遮挡条件已验收。原报告 `about_dialog=not_run` 与 `about_page=passed` 并不矛盾：本增量验收的是双栏设置内的关于页。

本次额外静态查看了现存 `01-settings-overview.png`、`05-double-click-actions.png`、`06-continued-application.png`：可见左栏/右栏层级、后台图标状态与继续/结束面板，以及恢复后的未保存昵称。没有重新捕获、合成或修改图片。派蒙最终设计确认仍为 `not_run`。

## 保留的功能与代码对应

- 双栏设置：`MainWindow.xaml` 的五个分类及左右列仍在；`SettingsUi.cs` 只切换右侧详情、维护唯一选中态，外观依然调用原存储实现。通知/控制中心仍明确未接入，不返回假成功。
- 后台面板：`RunningAppUi.cs` 保留后台单击不恢复、双击打开原生 Flyout、Enter/Space 等价操作；继续和结束均捕获宿主引用/实例 ID 并再次核对。`MainWindow.xaml.cs` 继续在返回桌面时调用 Background，确认结束才 Dispose 宿主。
- 真实运行证据：`package-smoke/runtime-events.jsonl` 中实例 `a33683d2-8ba3-4319-b69c-b1fe963e057b` 先 Background、再同 ID Foreground、最后 Closed；随后出现新实例 `72a2073b-7367-4da6-a1e1-ff045caef424`。后台 blocksMaintenance=true，结束为 false；这是当前逻辑生命周期，仍不是 T05 跨进程维护锁。
- SDK：`autumn-sdk.js` 保留 BigInt 单调 ID、32 KiB、16 待决、120/60 秒限制；`RuntimeSession.cs` 保留 pending ID 预留、10 分钟/2048 条有界去重，未恢复累计 4096 次封顶。JS/C# 两侧 12000 次测试源码和已运行日志仍在。可控时钟测试不等于两小时真实 Windows 耐久测试。
- Runtime、Packages、Storage、Identity、Contracts、SDK、样例及配置未在审计中修改。上轮 preservation-check 的 46 文件范围与其更早 45 文件描述相差旧 `Test-DesktopSmoke.ps1`，原进度已解释；没有发现数量冲突。

## 差异、限制与未执行项

截至上述只读核对时点，没有发现基线记录与所核对源文件、报告身份、包字节或已记录图像哈希不一致。`sdk-tests.json` 自身只有状态/退出码，没有测试数量；本审计从原始日志确认 15。窗口报告保留 `captured_not_visually_reviewed`，后续另有 9 图 `visual-review.json`，因此不将 21 张全称为已审阅。

本次构建、C# 测试、SDK 测试、Windows UI 回归、单实例/并发/故障注入均为 `not_run`，仅执行文件审计。完整 T01 网络通道隔离、物理输入/IME/触控、DPI 矩阵、浏览器崩溃/真正挂起，完整 T02 多页/文件夹/排序/搜索/通知控制中心/其余个性化，Windows 10/干净机器/安装 EXE、真实 Logto 登录及 Native/回调登记核验，保持原未完成状态。T03 未进入，不删改 T04–T06 或原功能范围。

## 单实例增量的异常与并发测试建议（尚未执行）

1. 同一安装目录并发启动 8–16 个进程，并覆盖主窗口尚未建立的时间段；应只有一个主 Shell 窗口和数据初始化拥有者，次进程在有限时间内退出或报告明确错误，不能靠杀同名进程收敛。
2. 已运行时通过 EXE、快捷方式和不同工作目录重复启动；验证恢复/激活原窗口、PID 不变。最小化状态应恢复，系统拒绝抢前台时如实记录结果，不能无限重试。
3. 在游戏后台及带未保存输入的前台游戏重复启动；原运行实例 ID、内存输入、存档哈希和外观设置应保持，第二启动不能重开游戏或代替用户确认结束。
4. 主实例取得所有权后但激活通道尚未就绪时启动次实例；需有明确就绪握手和有界重试，不因短暂窗口句柄缺失创建第二主实例。
5. 仅对测试自持、核对 PID/启动时间的主进程注入挂起，验证次实例超时可诊断；finally 恢复挂起进程。IPC 连接成功但无响应也必须有截止时间，不能永久挂住启动器。
6. 仅终止测试自持主进程，随后重启；进程死亡应释放所有权，允许新主启动，无需手删锁文件。另测正常退出与第二启动同时发生时的交接竞态。
7. 明确按用户会话/安装目录划分的作用域；同一可执行文件通过大小写、规范化路径或别名启动不得意外绕过，同名无关程序不得被激活。不同便携副本按已决定合同处理，不混用另一份 AutumnOS_Data。
8. 激活协议只接受最小固定消息及受限长度，处理错帧/断连/未知参数；不可把任意路径、命令或 UI 动作直接作为 IPC 授权。故障报告不得输出秘密，失败不得假报激活成功。

新测试须使用隔离数据副本，只清理自身创建并验证的路径/进程；保留本基线包和用户当前窗口。后续测试结论应关联新的源 ID、构建 ID、主/次进程身份、退出码与耗时，不能沿用本基线 153/15/80/21 作为单实例通过证据。
