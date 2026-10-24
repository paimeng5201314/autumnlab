# AutumnOS 隔离代码核验包：派蒙实测说明

制作人：派蒙。2026-10-24。按本轮最新要求，代理完成代码、自动检查与打包，后续真实窗口和新隔离机制由派蒙检测。这个包不是第三方隔离已经完成的正式版本。

**启动拦截补充：** codecheck-01 已由派蒙实测失败，系统 CodeIntegrity 3077/3033 证明内层 Client 被 Windows 策略阻止。后续修复包补齐启动错误分类、真实报告及单文件签名格式兼容，但仍未取得可信代码签名，不能保证在开启智能应用控制的本机启动。请先看随包 `WINDOWS-STARTUP.md`（源码说明 `docs/windows-startup-signing.md`）；不要关闭系统策略或反复运行来把拦截记为通过。最外层被 Windows 拦截时程序没有机会记录日志；内层异常的新报告位于测试数据目录的 `Logs/Startup/`。

## 当前可以检验什么

更新修复包还包含 `PRODUCTION-UPDATES.md`：本地生产更新公钥已经接入，发行元数据读取与可安装验证分开显示。公开发布尚缺签名更新附件时仍会拒绝安装，不应把该提示解释为必须购买Windows签名证书。本轮不替派蒙执行GUI升级；旧数据和原入口保留。

`AutumnOS.exe` 是独立单文件测试入口，内含固定运行环境；`Reports/`、`ACCEPTANCE.json` 关联实际构建与源码摘要。解压副本已经逐文件核对字节，但没有启动实测。读取或编辑这些报告不会改变产品的运行资格。

已完成代码修复：宿主先施加 CSP、原生网络策略和 SDK 来源限制再加载应用；每个实例独立验证；错误页或新文档使旧会话失效；权限窗口绑定真实实例；取消回调抛异常时继续解除订阅，保留资源清理重试与维护锁；关闭后丢弃迟到 SDK 响应。已知缺口下，在 WebView 创建前拒绝外部包与开发预览。

**ChemLeap 普通模式目前仍不能运行。** 缺少真正阻止旧式复制/剪切清空系统剪贴板的受限宿主，这是尚未实现的边界，并非只缺一份通过报告。已有 Job 试验失败，因此没有集成；不提供风险开关、开发模式例外、包安全声明或应用认可名单。元素配对仅按宿主自身发行资产的来源与真实摘要匹配执行；GitHub 包不会转为 bundled。

另有固定 WebView2 154 在两次文档提交后执行 RTC 可能触发原生故障，以及关闭挂起权限窗口的真实修复复验，尚未完成。新探针源码在工程 `artifacts/isolation-code-handoff-20261024/probes/`，独立于产品；编译成功不表示进程机制或浏览器验收成功。

## 普通界面检测顺序

1. 正常退出旧 AutumnOS，再双击本包的 `AutumnOS.exe`。不要用根目录旧入口，不把真实 `AutumnOS_Data` 复制过来。单实例可能只唤回已运行的旧版。
2. 完成游客首启，在关于页核对本包 `Reports/build-result.json` 的显示版本和构建号；测试数据只保存在这个 EXE 旁边。
3. 打开元素配对：翻牌、输入合成昵称、允许保存、保存后返回桌面、继续游戏、结束并重新打开、读取合成存档。记录任何错误文字和操作步骤。
4. 在设置中保持开发者模式关闭。商店安装原始 ChemLeap 0.1.1 后分别从商店、桌面打开。当前期望是明确提示缺少剪贴板边界，应用不执行；安装记录保留。该结果只验证失败关闭，不能记“ChemLeap 正常运行通过”。不修改已装包或版本/摘要。
5. 关闭并重开测试入口，确认测试存档保留。正常退出后，再从 ZIP 的另一份解压副本重复步骤2–4；两份副本均用合成数据。

首次启动会把运行文件展开到测试 EXE 旁的 `AutumnOS_Data/System`。保留整个测试数据目录以便定位问题；不清理真实数据。若出现防火墙/UAC窗口，先取消并记录，普通界面检测不需要允许入站或提权。

## 可由派蒙主动运行的既有自动界面驱动

这些命令会操作桌面；只有派蒙决定开始时才运行。PowerShell 7 工作目录为本地工程根，先正常退出旧启动器。界面驱动针对 `Package-T05 -Stage Prepare` 的完整目录包运行，不能把单文件目录冒充该输入。实际目录路径见本构建的 `package-prepared.json`。

```powershell
$buildId = '从 Reports/build-result.json 读取实际 build_id'
$prepared = Get-Content -LiteralPath "artifacts/builds/$buildId/package-prepared.json" -Raw | ConvertFrom-Json
$run = Join-Path $PWD ('artifacts/user-verification/' + [Guid]::NewGuid().ToString('N'))
& ./scripts/Test-WindowsSmoke.ps1 -ExecutablePath $prepared.executable -ExerciseRuntime -ReportDirectory (Join-Path $run 'element-pairs')
& ./artifacts/isolation-20261018/drafts/Test-RuntimeBoundaryUnavailable.ps1 -ExecutablePath $prepared.executable -ReportDirectory (Join-Path $run 'boundary-refusal') -HelpersDirectory (Join-Path $PWD 'scripts')
```

前一项检测自带游戏和存档；后一项用新的自有包验证两次开发预览都在创建浏览器前被拒绝，并释放运行租约。它们不验证完整网络或剪贴板隔离。完整网络旧驱动与旧失败断言仍保留，但在当前失败关闭版本上不能把“探针没能启动”写成网络隔离通过。

## 独立机制试验

两文档 RTC 最小复现仅使用自有页面和固定本地浏览器，不是产品放行。受限 token / 新私有 desktop 草案改变新子进程的权限与新对象 ACL，尚未接入产品；需要派蒙先阅读其 `REVIEW.md` 并明确授权该具体试验，不能沿用之前已经用完的单次 UAC 授权。当前交接不会自动启动任何此类草案，也不操作桌面剪贴板。

后续即使机制小试通过，仍需实现并验证实际浏览器宿主、内部呈现、输入/存档/生命周期及全部网络通道；不能只改一个布尔值放行。

## 记录与回传

请记录测试入口的完整路径、构建号、每步通过/失败/未执行、准确错误码，以及测试副本的 `AutumnOS_Data/Logs/runtime-events.jsonl`。只提供合成测试数据，避免发送真实账号信息、令牌或存档。无需修改报告让它显示通过。

待验证项目保持 `not_run`：本包原生窗口、ZIP解压副本运行、ChemLeap游玩/存档、不同来源合规应用、真实权限撤销/关闭竞态、两文档RTC、完整剪贴板、真实Logto双账号。第三方宿主网络接口当前未实现，授权联网需求保留但本配置不支持。历史失败记录保留在 `artifacts/isolation-20261018/`，不并入本包通过成绩。
