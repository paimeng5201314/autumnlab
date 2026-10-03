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
