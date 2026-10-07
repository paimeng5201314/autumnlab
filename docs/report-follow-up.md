# 2026-10 问题报告后续验证

对应用户提供的《06-问题与改进报告.md》与 AutumnOS 内部版本 0.5.1。这里记录功能状态和剩余证据要求；未执行验收仍为 `not_run`。实现与验收状态分别记录，不能把本次源码修订或 Linux 检查等同于已交付 Windows EXE 完成修复。

## 本次源码修正 · 2026-10-07

- ISSUE-04：抽出原生宿主实际使用的 `AccountDataRequestHandler`，与测试共用同一适配器。桥接回复使用专用 JSON 编码器；写入存档和偏好前，按完整读回响应、最长 64 字符 requestId、实际 UTF-8 转义字节计算 32768 字节预算。超限在落盘前拒绝，保留旧记录；游客路径也遵循相同预算。原始私有存储写入增加解码 20 KiB 限制，与读取一致。恢复存储层取消异常后，宿主能够区分超时与主动取消。
- ISSUE-02：偏好使用独立目录，旧 `pref_*.bin` 在账号锁内一次性迁移，保留原始文件与备份。迁移标记在新的同前缀原始存储写入前落盘，避免重启后误导入新 storage 内容。损坏 JSON/重复键和迁移冲突返回专用错误。迁移副本计入账号配额，空间不足时停止并保留原字节。逻辑 key 仍为 1–59 位，未放宽公开合同。
- ISSUE-01：登记记录支持可选、最多 32 条的 `probe_history`，严格验证条目字段、类型、文本与总大小。实际交付配置可读；历史说明仍不能建立登录状态或成为认证证据。
- GAP-07：`StructuredLog` 保留当前文件加两份轮转，每份最多 1 MiB；新增进程内写入失败计数、最近原因、恢复状态与设置页提示。诊断导出跨轮转取每来源最近 200 条有效白名单事件。Launcher/Runtime 两个独立日志仍沿用原来停写策略，此部分缺口未全部关闭。
- 测试辅助：样例测试以实际解决方案文件定位源码，不再要求不存在的根 `AGENTS.md`。测试程序新增可选 `--filter` 前缀筛选，默认全量不变；零匹配返回失败并在报告中保留筛选条件。

## 本次验证证据

环境：Linux x64；微软官方 .NET SDK 10.0.401（SHA-512 已核验）、运行时 10.0.12、PowerShell 7.6.6；Node 24.19.0、npm 11.6.1、TypeScript 5.9.3。原始 Windows 依赖锁保留，Linux 恢复使用 `obj/linux` 的独立副本与输出。此处 C# 编译仅覆盖测试依赖图，不包含 WinUI/XAML 原生构建。

| 检查 | 本次结果 | 证据 |
|---|---|---|
| C# 编译 | 0 警告、0 错误 | 当前云工作区 `/workspace/onboarding/report-fixes-focused.log` |
| 本次修复专项 | 71/71 通过：Bridge 14、Preferences 12、Registration 25、diagnostics 6、support_bundle 14 | `artifacts/onboarding/report-fixes-focused.json` |
| C# 全量 | 487/495 通过，8 项失败，未屏蔽断言 | `artifacts/onboarding/report-fixes-csharp.json` |
| SDK 与模拟 DOM | 28/28 通过 | `artifacts/onboarding/report-fixes-node-tests.log` |
| 机器合同 | 352/352 通过，包括实际 TypeScript、Schema、OpenAPI 与版本文档检查 | `artifacts/onboarding/report-fixes-contracts-final.json` |
| 文档/布局静态检查 | 144/144 通过，涵盖源码及复制的 Developer 布局链接 | `artifacts/onboarding/docs-ui-checks.json` |
| XAML XML 结构 | 解析通过；Windows 编译、排版与输入未运行 | `src/AutumnOS.Shell/MainWindow.xaml` |

8 项 C# 全量失败分别是 storage、desktop_preferences、desktop_layout 的文件忙/只读替换测试，以及 runtime 的忙存档/只读存档测试。独立文件操作探针确认：Linux 的独占文件冲突错误低 16 位为 11，而现有产品代码映射 Windows 32/33；Linux 的原子替换可以替换只读目标。没有修改这些断言或把失败标成通过，仍需要在目标 Windows 文件系统运行完整套件。

复制的 Developer 布局整体合同在 Linux 的 TypeScript 阶段还遇到现有 `SDK` 目录与测试导入 `sdk` 的大小写差异；该结果没有记为通过。上表 352 项是源码工作区的实际检查，144 项是链接/结构静态检查，两者均不替代 Windows 中从空目录启动并创建、预览应用的验收。

当前云工作区用 `bash /workspace/onboarding/check-autumnlab-linux.sh` 执行恢复、样例生成、编译和全量检查；它保留真实非零退出码。已构建的测试程序以 `--filter 'bridge.,storage.preferences.,Registration:,storage.diagnostics,support_bundle.'` 执行上述专项；零匹配退出 1、缺参数退出 2 均已实测。JavaScript 与合同分别使用 `node --test tests/sdk-tests.cjs tests/t03-sample-tests.cjs` 和 `node tools/contract-checks/contract-check.cjs --report <新的报告路径>`。源快照清单位于 `artifacts/onboarding/report-fixes-source/source-snapshot.json`。

## 界面及交付文档

- ISSUE-05：设置桌面说明改为实际三列拖动排序、长按/右键/Shift+F10 前后移动、成功后重启保留；同步更正为单击图标继续，双击空白或 F6 查看运行实例。明确文件夹、多桌面、完整搜索、自定义 Dock、抖动编辑及边缘自动滚动尚未提供。依据 `DesktopInteractionsUi.cs` 静态核对，Windows 排版与物理输入回归为 `not_run`。
- ISSUE-06：恢复 [0.5.1 版本文档](versions/0.5.1/README.md)，说明单文件外层入口可独立交付、普通目录版保留完整资源。Developer 位于实际客户端程序目录，CLI `--host` 必须匹配 `AutumnOS.Client.exe` 所在目录，不能取外层入口或猜选旧 Product 目录。源码与开发套件两种文档布局需校验链接；从空目录实际启动、创建并预览仍须 Windows 验收。

## GAP 状态与下一步

| 编号 | 当前实现/配置状态 | 尚需执行的验收与完成条件 |
|---|---|---|
| GAP-01 | `partial`：有 CSP、导航与资源过滤，`networkSandboxVerified:false`，保留受控包运行门禁 | 对 fetch/XHR/WebSocket/EventSource/WebRTC、导航重定向、frame、worker/service worker、wasm、图像/样式/字体、弹窗、下载、外部协议与桥接来源逐项建立真实正负例网络证据；当前完整矩阵 `not_run`。不能用移除门禁完成。 |
| GAP-02 | `blocked`：生产更新信任根缺失，正式自动安装关闭 | 由有权持有人配置生产信任材料，保留 TLS/摘要/RSA-PSS/序列验证；用真实授权签名产物验证，不提交私钥或接受未签名更新。 |
| GAP-03 | `partial`：有更新事务、租约、journal、健康恢复；新单文件完整闭环未证明 | 在独立测试信任构建执行 A→B、健康失败回退、各阶段崩溃恢复；覆盖 Starting/Background/Suspended/Closing、存档最终提交、安装并发、已更新种子重启、磁盘满、忙文件和数据兼容。本次 `not_run`，旧布局成绩不复用。 |
| GAP-04 | `partial`：已有三列排序、长按菜单和切换器 | 文件夹、多桌面、完整搜索、自定义 Dock、抖动编辑、触控闭环和控制中心系统音量/网络仍未完整实现。分项设计、实现并验收；文案修复不代替这些功能。 |
| GAP-05 | `partial`：有严格身份与独立 Native/API 示例，人工登记说明不等于登录 | 用真实获准配置验证第二账号、浏览器退出、长期刷新、独立 client/resource/scope 与挑战往返，保存脱敏结果。缺真实登记或用户认证时记 `blocked/not_run`，模拟账号不计成功。 |
| GAP-06 | `partial`：卸载/失败准备保留恢复与用户资料，尚无完整清理策略 | 先建立活资源、恢复资源、用户数据归属清单与容量预览，再在无实例且持维护租约时执行明确选项；覆盖取消、并发、重解析点、磁盘失败。完整清理实现/验收未完成，不按后缀或年龄删除 Data。 |
| GAP-07 | 本次增加有界日志轮转、落盘失败状态与诊断展示；部署后的行为仍待验收 | 验证轮转后最新事件继续写入、保留代数/总量有界、落盘失败可见、恢复后状态更新、导出包含有效新日志且不泄密；Windows UI 与真实磁盘故障为 `not_run`，以本次测试报告记录代码测试结果。 |
| GAP-08 | `not_run`：尚缺完整平台、输入与外部用户证据 | 对新构建分别记录 Windows 10、干净系统无 SDK、离线首启、多屏/DPI、输入法、触屏/笔、外部新人及制作人审阅；当前机器启动成功不外推到这些设备。 |
| GAP-09 | `blocked/partial`：正式 Authenticode、第三方许可与项目许可收尾未完成；安装包暂缓 | 授权签名、实际验证签名链与时间戳、核实发布适用的根 LICENSE 和逐组件归属；存在许可证文件不代表许可门禁通过。Setup 源码存在不表示安装 EXE 已交付。 |

每次执行应附具体构建/源快照、输入、命令与退出码、报告路径、结果及未覆盖范围。对新布局、真实账号、物理设备和外部新人分别保留报告；完成上述条件前，不把整个报告或完整发行状态改为已通过。
