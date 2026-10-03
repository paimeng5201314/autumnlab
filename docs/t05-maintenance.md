# T05 维护协调与身份写入

制作人：派蒙。本文对应 `AutumnOS.Storage.CriticalOperationCoordinator` 的进程内真实实现；跨进程更新锁、交接与恢复由启动器/更新器协议另行负责，不能用这里的计数或产品单实例 Mutex 替代。

## 同一个准入入口

Shell 创建一个 `CriticalOperationCoordinator`，注入运行生命周期、存档/私有文件/导出、配置、应用安装提交和身份服务。它只短暂锁定内部计数表，返回的租约不持有线程所属的 Monitor，也不会在等待维护时持有 Runtime、身份 `_gate` 或文件锁。存储服务仍保留原来的账号提交租约、每文件/目录独占锁与原子替换；安装器仍保留自己的按应用运行/安装锁。应用安装使用具名 `EnterWrite`，不是系统排他维护，否则安装会过度阻止其他游戏或与内层配置租约互相拒绝。

| 实际接口 | 语义与错误 |
|---|---|
| `EnterWrite(string reason = "数据写入") : IDisposable` | 在一个原子 gate 内登记具名关键操作；准备/维护期间抛 `DataStoreException("MAINTENANCE_IN_PROGRESS")`。标签是固定且不含秘密的宿主文字，限 160 字符；第三方不可传任意标签。 |
| `EnterGame(string appId, Func<AppInstance?>? snapshot = null) : IDisposable` | 在创建 WebView/Starting 之前取得。必须等 WebView、启动任务、响应流、资源和能力句柄真正释放后 Dispose。回调仅提供界面说明，不决定是否阻塞。 |
| `GetSnapshot() : MaintenanceSnapshot` | 返回 `IsPreparing`、`IsMaintenance`、`ActiveWrites`、`ActiveGames`、`WaitingReasons` 和派生 `IsIdle`。它是观察快照，不能据此自行提交更新；最终仍须取得维护资格。 |
| `TryEnterMaintenance() : IDisposable?` | 同一 gate 内检查没有游戏、没有关键操作、没有另一位准备者，然后取得排他资格；否则返回 null，无副作用。 |
| `PrepareMaintenanceAsync(TimeSpan timeout, CancellationToken ct = default) : Task<IDisposable>` | 无游戏时原子关闭新 launch/write 准入，等待已经登记的关键操作完成，再次检查并取得排他资格。timeout 必须大于零且不超过五分钟；有界等待、可取消。 |
| `ActiveWrites` / `ActiveGames` / `IsPreparing` / `IsMaintenance` | 仅供宿主状态显示与诊断；不是跨进程锁，也不是更新成功证据。 |

Starting、Foreground、Background、Suspended、Closing 都仍持有游戏租约。即便 Runtime 已标记 Closed/Crashed，只要资源释放尚未完成，租约仍阻止更新，界面显示“等待资源释放”。诊断回调异常也不释放租约。租约支持跨 await/线程持有和幂等 Dispose，但这不等于底层 UI 资源可以在任意线程释放。

游戏活跃时 `PrepareMaintenanceAsync` 在原子 gate 内立即返回 `MAINTENANCE_GAME_ACTIVE`，不进入准备态，保证等待期间游戏仍可发起存档操作。更新页显示真实原因，待游戏正常结束后再试。不能先冻结保存入口，然后要求仍在运行的游戏退出而丢失输入。

## 维护时序与失败

1. 后台完成候选筛选、下载和验证，这些步骤不取得排他维护。
2. 提交动作读取原因供 UI 显示；有游戏就延后，不能主动关闭游戏。
3. 调用 `PrepareMaintenanceAsync`。启动/写入和维护准备的准入竞争在同一 gate 内线性化；只可能由符合条件的一方进入。
4. 准备态等待旧操作，新的游戏和新的关键写入收到可恢复的 `MAINTENANCE_IN_PROGRESS`；既有操作保留租约并正常完成，维护不会调用其取消/终止逻辑。
5. 取得排他租约后再核验候选、渠道、安装根、所有外部占用和交接条件，保持租约至受保护的退出/交接流程。中途失败或用户取消必须 Dispose，恢复准入。
6. 新程序启动后创建自己的协调器；旧进程的 IDisposable 不能跨进程转移。整个退出、文件替换、健康确认间隙由启动器/更新器跨进程协议覆盖。

另一个准备者存在时抛 `MAINTENANCE_IN_PROGRESS`，不会重置原准备者。等待超时抛 `TimeoutException`，取消抛 `OperationCanceledException`；两者只恢复准入，不强行释放正在进行的操作、不清理其用户数据。现有操作长期不结束时，界面保留具体原因，更新可由用户稍后重试。不要无限无退避重试。

## 真实写入覆盖

`AccountDataStore` 的存档写入、恢复、格式迁移、游客迁移、私有数据写入和删除，各有具名租约；`FileCapabilityBroker` 的文件导出同样覆盖从暂存到原子替换。`VersionedConfigurationStore` 覆盖 Save/Migrate/RestoreBackup。旧 `DesktopPreferencesStore`、`DesktopLayoutStore`、`FirstRunStateStore` 构造器新增可选 `CriticalOperationCoordinator` 参数，Shell 显式传入同一实例；每次写入在创建配置目录和打开独占锁之前登记，拒绝时返回 `MAINTENANCE_IN_PROGRESS`，原字节保留。

参数可选用于旧测试和独立工具的源码兼容，不代表产品可以省略接线。每个对象自行 `new` 协调器不能协调跨模块更新。普通诊断日志不属于用户数据迁移/存档事务；持久更新日志由更新器自己的事务协议保护。

## 身份协议与持久化

`LogtoIdentityService(options, dataRoot, Func<string, IDisposable>? enterCriticalOperation = null)` 通过宿主提供的回调接入同一协调器，不新增 Identity → Storage 依赖。Shell 传 `reason => criticalOperations.EnterWrite(reason)`。

- 登录从进入事务前直到浏览器回调、令牌验证和 DPAPI 提交全部完成持有租约，最长沿用三分钟登录预算。维护等待不会杀浏览器、取消用户输入或假造完成。
- 刷新在串行 refresh gate 内、发出网络请求前取得租约，覆盖服务端 token 轮换以及本地原子保存。已进入的 refresh 可以在准备态完成写入；不能网络轮换后才申请一个会被拒绝的维护租约。
- 保持登录选择、退出标记/凭据清理和恢复凭据均有租约；身份 Vault 的读取可能收紧 ACL，因此也纳入关键操作。永久 30 秒 refresh timer 本身不持有租约，空闲服务不会永久阻止更新。
- 正常服务只在整个身份协议外层取得一次租约。`WindowsCredentialVault` 另有内部可选回调供独立宿主/测试直接使用；不要把外层已登记的事务再以新操作重复申请，否则准备态会拒绝本该完成的内层提交。
- 维护期间新登录、刷新、持久化选择或退出返回 `MAINTENANCE_IN_PROGRESS`，保留当前账号、epoch、内存会话和密文，不调用认证服务或清除凭据；UI 应提示维护结束后重试。已经开始的操作最终仍使用原有 epoch 检查，账号切换不能接受迟到结果。

DPAPI CurrentUser、固定 Authority/ClientId entropy、当前用户 ACL、无明文回退、sign-out marker、既有账号命名空间和回调登记未证实状态保持原合同。此协调器不迁移/复制用户凭据，不替代真实 Logto 验收。

## 已执行证据与范围

2026-10-02 本模块通过项目内 SDK 分别构建 Storage/Identity。Storage 首次辅助构建禁止项目引用，Identity 同模式因旧 Contracts 引用缺少类型失败；随后允许正常项目引用构建通过，未通过改接口掩盖错误。

实际专项命令：

```powershell
.\.tools\dotnet\dotnet.exe run --project artifacts/reports/t05-maintenance-20261002/MaintenanceRunner.csproj -c Release -v:minimal
```

14/14 测试通过，报告 `artifacts/reports/t05-maintenance-20261002/maintenance-tests.json`。测试源为 `tests/AutumnOS.Tests/MaintenanceTests.cs`，正式测试入口同时接入 `MaintenanceTests.Cases()`。包括：全部生命周期与资源租约、160 次并发准入竞态、真实有界超时和取消、实际存档原子提交排空、配置原字节保护、配置迁移/恢复、登录与 refresh 协议替身，以及临时目录中的真实 Windows DPAPI 读写保护。

同日另执行 `.\.tools\dotnet\dotnet.exe run --project artifacts/reports/t05-maintenance-regression-20261002/RegressionRunner.csproj -c Release --no-restore -v:minimal`，既有存储、桌面外观/排列、身份、持久化、回环响应和 OIDC 协议测试 235/235 通过，报告 `artifacts/reports/t05-maintenance-regression-20261002/module-regression-tests.json`。该辅助 runner 第一次编译遗漏既有 IdentityPersistenceTests/CallbackResponseTests 文件并失败，补齐引用后运行成功；没有删减原用例。

身份用例使用明确的本地 fixture，无真实账号/令牌；测试结果不证明真实 Logto 会话保留。游戏生命周期测试为协调器协议测试，不等同最终交付 EXE 的真实 WebView 升级与回退。U03/U04/U05 只覆盖上述实际断言；真实 Windows 客户端、重复双击、独立更新器替换、最终 A/B 演练以及正式公钥接入另行记录，未执行项保持 not_run。
