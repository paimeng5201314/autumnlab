# T00 数据目录与首次启动状态

制作人：派蒙。本文对应 `src/AutumnOS.Storage` 的真实内部 C# API，不属于公开应用 SDK。

`InstallationRoot.ForCurrentProcess()` 使用 `AppContext.BaseDirectory`，因此普通启动、快捷方式或从其他工作目录启动均使用入口旁的 `AutumnOS_Data`。不会在失败时改写到工作目录、AppData 或别的磁盘。可信宿主与测试也可使用 `new InstallationRoot(absoluteInstallationDirectory)`；相对路径被拒绝。

`EnsureCreated(CancellationToken = default)` 返回 `DataRootResult(Success, ErrorCode, RecoveryMessage)`。它只创建缺少的目录，保留所有已有文件，检查入口目录、数据根及 12 个固定子目录的 reparse point，并在数据根和每个子目录实际创建、写入、刷盘及删除随机探测文件。Windows 目录的 ReadOnly 属性不足以判断写权限，所以没有用属性替代真实写入。目录固定为 Apps、Packages、Downloads、Cache、AppData、Saves、Screenshots、Config、Logs、Runtime、Temp、Updates；路径由只读 `Directories` 字典提供。

失败返回固定错误码与恢复提示，不返回异常消息、堆栈或敏感数据。`DATA_ACCESS_DENIED` 要求用户将完整程序与现有数据移至可写位置后重试；不会提示日常提权。`DATA_PATH_CONFLICT` 表示文件/目录冲突；`DATA_UNSAFE_PATH` 表示重解析点；`DATA_IO_ERROR` 表示磁盘或占用问题；`USER_CANCELLED` 保留已创建的目录供下次继续。服务不会为了恢复而删除用户文件。

## 首次启动 API

```csharp
var root = InstallationRoot.ForCurrentProcess();
DataRootResult data = root.EnsureCreated();
var firstRun = new FirstRunStateStore(root);
FirstRunResult current = firstRun.Load();
// 仅在当前实际步骤完成后调用相邻步骤；UI 负责真正执行步骤。
FirstRunResult saved = firstRun.Advance(FirstRunCheckpoint.Hello);
```

`Load(CancellationToken = default)` 和 `Advance(FirstRunCheckpoint, CancellationToken = default)` 返回 `FirstRunResult(Success, State, ErrorCode, RecoveryMessage)`。成功时 `State` 含 `SchemaVersion`、`Checkpoint`、`Revision`、`UpdatedUtc` 及计算属性 `IsComplete`。失败时 `State` 为 null，调用方必须处理错误，不能当作全新首次启动。

顺序为 `Hello → Brand → Initializing → Completed`。初始缺少状态文件时返回 Hello/revision 0，不写文件。`Advance(Hello)` 可将当前初始步骤持久化。重复同一步幂等、不更改时间或字节；回退、跳步、未知枚举返回 `CONFIG_INVALID_TRANSITION`。UI 只能在真实工作完成后推进；保存 checkpoint 不代表已实现 T02 桌面。

状态存于 `Config/first-run.json`，当前 schema 为 1。文件最多 16 KiB，要求精确字段与正确类型；损坏、缺字段、重复字段、未知字段、无效 checkpoint/时间/修订均返回 `CONFIG_CORRUPT`，未来版本返回 `CONFIG_FUTURE_SCHEMA`。两种情况均保持原始字节，拒绝覆盖，无隐式重置或降级迁移。schema 1 是首版，无历史 schema 迁移可执行；新增迁移必须单独测试。

保存先独占 `Config/.first-run.lock`，在同目录创建随机暂存文件、完整写入并 `Flush(true)`，再通过 `File.Replace`（已有文件）或不覆盖的 `File.Move`（首个文件）提交。没有先删除正式文件的窗口。被其他写入者占用时返回 `CONFIG_BUSY`，可稍后重试。取消发生于提交前时保留旧文件；调用方收到成功后说明提交完成。崩溃留下的未知 `.tmp` 文件不自动提升或删除；重新启动从最后一个完整 checkpoint 继续。断电时底层文件系统持久性仍取决于设备与文件系统；本模块没有声称已通过断电硬件测试。

锁、状态与暂存路径在每次访问前均检查重解析点；目录检查拒绝数据根/一级受管子目录的静态 junction/symlink 重定向。这是宿主受信任数据基础，不是任意恶意本机同账户进程的文件系统沙箱；当前基于路径检查，不能保证抵御检查与访问之间由其他进程刻意替换目录的竞争。第三方应用不得调用本 API，也不得获得这些磁盘路径。

## 日志

`new StructuredLog(root).Write(DiagnosticEvent, string? errorCode = null, Guid? correlationId = null)` 返回 `DataRootResult`，写入 `Logs/diagnostics.jsonl`。调用前应已成功初始化数据根。每条只有 UTC 时间、固定事件 ID、登记过的错误码、关联 GUID；API 没有异常/任意消息/用户内容参数。传入未知事件或错误字符串抛 `ArgumentException`，拒绝将其写入。日志最大 1 MiB，满时返回 `LOG_CAPACITY_REACHED` 并保留现有记录；日志失败不应导致 Shell 崩溃。诊断导出、轮替及 SDK 限流属于后续阶段。

## 本地验证范围

`tests/AutumnOS.Tests/StorageTests.cs` 通过无 NuGet 控制台 runner 提供 `Cases()`。测试在自己创建的带中文和空格的临时根中运行，包含目录清单、重复启动、数据保留、工作目录无关、冲突、取消、断点继续、状态顺序、损坏/未来配置保护、原子保存失败、锁竞争与安全日志；Windows 另有真实 ACL 写拒绝及 root/child/state junction 拒绝测试。Windows ACL 测试仅修改其自建临时入口目录，finally 移除自己的 deny ACE 后清理；不修改用户程序或数据 ACL。

测试执行结果以根进度文件及本地报告为准；源代码存在不代表测试已运行。进程崩溃/断电注入、全平台文件系统、真实便携与安装路径的普通用户 GUI 验收不由这些单元测试代替。
