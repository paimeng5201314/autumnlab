# Lab Chronicles AutumnOS · 本地项目规则

**制作人：派蒙。用户入口：AutumnOS.exe。**

当前目录为本地开发工作区；不需要远端仓库、GitHub授权或.git。不要克隆、拉取、推送、创建PR/Release或转交云端任务，不擅自初始化/提交Git。不从远端覆盖用户本地文件。

先读LOCAL_WORKFLOW.md、CODEX_START.md、autumnos-spec/AGENTS.md和对应需求/任务文件，再按T00至T06的依赖进行真实开发。保留已有源码、许可证与用户规则；本文件与已有AGENTS.md合并，不盲目覆盖。

严格保留.NET/WinUI原生Windows客户端、内部.autumn应用、iPad式桌面、便携和安装包、Logto权限、存档、安全更新和完整开发者文档要求。制作人统一为派蒙。商店及更新仍使用GitHub读取能力，不建设认可名单，不代表允许远端写入。

公开配置在autumnos-spec/config/logto.public.json。Native类型和回调登记未证实，不虚构通过、不重复索要已有公开值、不索取Client Secret。敏感凭据不写入源码/日志/提示词。

优先本机Windows构建、运行、测试和打包；云端CI不是前提。没有Git时用实际源文件快照与哈希关联证据。自动测试本地可重复执行，不删减验收。

真实进度记入AUTUMNOS_PROGRESS.md。未跑测试标not_run。源码生成不等于EXE可运行，计划检查不等于产品测试。缺少工具时报告具体缺项并继续独立工作，不为赶发布改成网页或假功能。

未获明确发布授权，不上传项目或发布候选；交付本地源码、真实产物路径、报告、待验证事项。保留工具安全审批。

## 工程入口与协作

工作根为本文件所在目录，原始开工包保留在 AutumnOS_Local_Kickoff_v0.3/ 和同名 ZIP。当前有效规范在 autumnos-spec/；阅读 docs/01-baseline.md、03-interfaces.md、11-logto-integration.md、tasks/index.json 和当前阶段任务。恢复工作先读 AUTUMNOS_PROGRESS.md。

集成负责人统一修改解决方案、Directory.Build.props、Directory.Packages.props、公共 Contracts、构建脚本和规范状态。模块工作分别拥有自己的 src 子目录和测试文件。T00 原生窗口通过 Windows Release、普通用户启动和数据验证后才进入 T01；T01 通过后再并行 T02/T03/T04。

依赖只从官方源恢复，固定版本并保留 packages.lock.json。项目内 SDK 在 .tools/dotnet，不修改系统 PATH。快照脚本排除用户数据、缓存、凭据和生成物；每次构建从实际字节生成 ID。所有未运行验收保持 not_run。

## 完成后自动清理（用户授权，2026-11-11）

本目录为唯一有效工程，父目录 `.tools/` 是共享依赖。每次制作和相应运行验证完成、CURRENT及验收摘要更新后，主动运行 `../maintenance/Complete-LocalWork.ps1 -Apply`，不再询问。脚本核验匹配构建、九组自动测试、实际交付摘要与源归档后清理旧构建、原始错误日志、重复包和下载残件。未修好的当前失败保留诊断，不得改为通过。

保留当前源码/规范/许可证/锁、必要原生测试源码、最新完整安装、源归档与精简验收记录、存档/凭据和工具链；不穿透链接、不清理在用程序目录、不跨工作区。父目录旧源码的 `-IncludeLegacySource` 仅用于本次已审计清理。旧原始日志和过期产物不再永久保留；本条用户新授权覆盖旧交接保留要求，历史原始证据已清理时须如实说明。
