# T03 文件、存档和配置

制作人：派蒙。本文对应 `AutumnOS.Storage` 的实际 C# 服务；SDK 经 Runtime 和 Shell 适配调用，不能把这些宿主 API 直接暴露给应用。支持版本为本轮 T03 开发包、SDK 协议 1。真实测试结果以对应构建的报告为准，本文不表示 A037/A038 全矩阵已经通过。

## 数据身份与会话

`StorageScope(AppIdentity, AccountKey, InstanceId, Epoch, SourceBinding)` 仅由宿主从安装记录与已验证会话创建。`AccountKey` 为身份模块提供的稳定命名空间，游客为字面值 `guest`，不用昵称或邮箱定位。`SourceKey` 是 appId、repositoryId、签名指纹和宿主安装来源的规范 JSON SHA-256；更换同名应用的实际来源不会获得旧来源的数据。

普通数据位置为 `AutumnOS_Data/Saves/<appId>/<sourceKey>/<account>/` 与 `AppData/<appId>/<sourceKey>/<account>/`；账号目录为稳定命名空间的 SHA-256，原始身份值不进入文件路径。WebView Cookie/localStorage/IndexedDB 等隔离由 Shell 的对应账号配置目录承担；这里的存档目录隔离不能代替浏览器隔离。

旧的受控样例允许由宿主明确指定 `allowLegacyGuest:true`，保留 `Saves/<appId>/guest/game.json`。v1 记录只读时不迁移、不改写；第一次实际新保存升级到 v2 并将原始 v1 字节留在 `.bak`。其他应用来源不应获得此兼容开关。

每个服务固定一个实例、账号及代次。`isCurrent(scope)` 在操作开始和提交前验证。`commitLease` 必须同时覆盖 Runtime 关闭和账号代次切换的同步锁；在该租约中再次验证，然后执行原子替换。单独的布尔检查不能消除切换竞争。宿主先取消旧事务并关闭相关 WebView，再切换身份；不在锁内触发任意事件/取消回调，句柄撤销也在 Runtime 关闭锁释放后执行，避免锁顺序反转。

## 宿主服务接口

结果统一为 `DataResult<T> { Success, Value, ErrorCode }`，失败不返回文件路径、保存内容或异常堆栈。Runtime 将失败转换为统一错误响应，不能把失败结构套在 `ok:true` 内。

| API | 输入 → 输出 | 约束与权限 |
|---|---|---|
| `ReadSave(slot, ct)` | 逻辑槽位 → `JsonElement?`，不存在为 null | SDK `saves`；返回前重新验证会话 |
| `WriteSave(slot, value, formatVersion=1, ct)` | JSON、格式版本 → bool | SDK `saves`；每槽位 128 KiB，格式版本与应用版本分开 |
| `ListSlots(ct)` | 无 → `SaveSlot[]` | 槽位、格式版本、修订、字节数和备份有无；不返回磁盘路径 |
| `RestoreSave(slot, ct)` | 槽位 → bool | 显式用户恢复动作；保留 `.before-restore` 和有效 `.bak`；已有恢复记录时拒绝覆盖 |
| `MigrateSaveFormat(slot, from, to, transform, ct)` | 当前格式、目标格式、可信转换函数 → bool | 仅可信宿主/应用版本迁移代码；SDK 不接收可执行转换函数 |
| `MigrateGuestSave(slot, confirmed, ct)` | 显式确认 → bool | 仅宿主账号迁移入口；目标账号不得为游客，目标已存在就拒绝 |
| `ReadPrivate(key, ct)` | 逻辑键 → bytes/null | SDK `storage`；最多 1 MiB/文件 |
| `WritePrivate(key, bytes, ct)` | 键、内容 → bool | SDK `storage`；有备份的原子替换 |
| `DeletePrivate(key, ct)` | 键 → 是否存在 | 仅该键移至 `.bak`；不删除存档、凭据、浏览器缓存或应用 |

槽位和私有键仅接受 1–64 个 ASCII 字母、数字、`-`、`_`，拒绝磁盘路径、斜杠、ADS 冒号、点号以及 Windows 设备名。默认每账号/来源最多 32 个槽位、128 个私有文件、总计 16 MiB。总配额包含备份、恢复记录和本次暂存所需空间，满时拒绝新写而不清理原数据。SDK 的消息大小上限仍生效，因此单次可传数据可能低于存储服务的上限。

写入取得共享 `CriticalOperationCoordinator` 的关键写租约，并取得该账号目录的独占文件锁。先写唯一暂存文件、`Flush(true)`，再在会话租约内 `File.Replace` 或无覆盖 `File.Move` 提交。失败只清理本次未提交暂存，不删除原记录。相同 JSON 与格式版本重复保存返回成功且保留修订、备份、字节不变。并发保存可以返回 `STORAGE_BUSY`，调用者退避后重试；不要盲目循环。在途取消只在提交前有效；提交后收到取消不意味着原子提交已回滚，调用方应重新读取。

v2 存档绑定 schema、应用、来源、账号、槽位、格式版本、修订与规范 JSON 内容 SHA-256。损坏、不匹配身份、重复结构字段和较新 schema 都拒绝覆盖。校验和用于发现损坏，**不是防止本机当前用户篡改的签名**。

游客迁移是明确确认后的复制，不是登录副作用。原游客文件保留，先在目标创建绑定目标账号的 `.import-backup`，再提交目标。已存在目标或不匹配旧导入备份为冲突；若中断只留下匹配导入备份，可再次确认后恢复同一导入。转换异常、取消、磁盘满与配额不足不替换旧格式。正常退出账号不调用删除数据。

## 外部文件能力句柄

`FileCapabilityBroker` 仅由系统选择器完成后调用宿主注册函数。应用不得提交路径：

- `RegisterPickedFile(absolutePath, scope, lifetime)` 返回只读能力；内部保持实际文件流，应用拿到 256 位随机 handle、基本文件名、字节数和失效时间，不拿到路径。
- `RegisterPickedSaveFile(absolutePath, scope, lifetime)` 返回独立的一次性保存能力，登记时不创建、不清空目标。SDK 权限为 `files.save`，不是只读 `files.open` 的自动升级。
- `Read(handle, scope, ct)` 返回内容；`Write(handle, scope, bytes, ct)` 进行一次原子导出；`Close(handle, scope)` 主动释放。成功导出立即消费句柄；重复写返回失效。
- 已存在的导出文件在原子替换时保留同目录唯一 `.AutumnOS-backup-<随机值>`。这些是用户可见恢复文件，不会悄悄删除；导出 UI 应说明保留位置。目标自选择后内容改变、由存在变为缺失或反之时返回 `FILE_CHANGED_SINCE_PICK`，请用户重新选择。
- 每次使用检查完整应用/来源、账号、实例、代次、权限及有效期。读句柄不能写，保存句柄不能读。关闭应用、撤销权限或切换账号后撤销对应句柄。
- 最多 64 个句柄、15 分钟有效期、8 MiB 文件；SDK 单条消息仍受更小网关预算限制。读取或导出超出消息预算必须拒绝，不能绕过网关分配无限内存。

路径祖先和文件拒绝重解析点/目录混淆，导出只写系统选择器明确选择的路径，暂存和备份均同目录、名称由宿主产生。不宣称能隔离对本机文件系统具有同等 Windows 用户权限的恶意进程；现有 WebView 网络安全未完成项仍是独立发布门禁。

## 配置版本与恢复

`VersionedConfigurationStore(root, name, supportedVersion, coordinator)` 用于新的宿主配置文件。已有 `DesktopPreferencesStore` 和首启记录保留其既有 schema 与读写路径，不因新版本存在而重置主题、壁纸或首启进度。

`Load()` 返回 `{ SchemaVersion, Revision, Values }`；`Save(objectValues, ct)` 要求配置已经是当前 schema。读取较旧 schema 允许检查，但写入返回 `CONFIG_MIGRATION_REQUIRED`，需调用 `Migrate(fromVersion, trustedTransform, ct)`。转换成功才原子提交并保存 `.bak`。`RestoreBackup(confirmed, ct)` 显式恢复，保留损坏原件 `.before-restore`；已有恢复原件时拒绝再次覆盖。新 schema、损坏 JSON、不合规对象或超出 64 KiB 均拒绝覆盖。

恢复界面先调用宿主只读检查接口，不通过 SDK 暴露磁盘路径。`AccountDataStore.InspectSaveRecovery(slot, ct)` 返回主记录、备份及恢复前原件是否存在、备份是否有效与有限错误码；即使主记录损坏仍可单独校验备份。`VersionedConfigurationStore.InspectBackup()` 另外返回已校验的 `Backup` 配置快照及 schema，供功能模块再次核验实际字段（例如开发者模式 `enabled` 必须为布尔值），不能把有效 JSON 当成有效功能配置。检查不会创建、恢复、删除或覆写记录；恢复仍须明确确认及提交时的会话/维护检查。

`CriticalOperationCoordinator.ActiveWrites` 反映实际保存、导出、迁移中的租约；`TryEnterMaintenance()` 只在无写入时返回维护租约，持有期间拒绝新写入。T02 之前只有游戏 `BlocksMaintenance` 标记；本次新增的是实际关键写协调。未来 T05 还必须在共同启动/维护协议内合并游戏、安装、迁移和更新交接，单实例互斥和这里的计数都不等于 T05 更新已经实现。

## 错误、正反例与复核

常见错误：`INVALID_PARAMS`、`SESSION_STALE`（Runtime 对外统一为会话失效）、`USER_CANCELLED`、`STORAGE_BUSY`、`STORAGE_ACCESS_DENIED`、`STORAGE_DISK_FULL`、`STORAGE_QUOTA_EXCEEDED`、`STORAGE_UNSAFE_PATH`、`SAVE_TOO_LARGE`、`SAVE_CORRUPT`、`SAVE_FUTURE_SCHEMA`、`SAVE_MIGRATION_CONFLICT`、`SAVE_FORMAT_CONFLICT`、`SAVE_MIGRATION_FAILED`、`SAVE_BACKUP_NOT_FOUND`、`SAVE_RECOVERY_CONFLICT`、`FILE_HANDLE_INVALID`、`FILE_HANDLE_WRONG_OWNER`、`FILE_HANDLE_EXPIRED`、`FILE_HANDLE_ACCESS_DENIED`、`FILE_CHANGED_SINCE_PICK`、`CONFIG_MIGRATION_REQUIRED`、`CONFIG_FUTURE_SCHEMA`、`CONFIG_MIGRATION_FAILED`。权限拒绝/撤销由网关及句柄检查另外拒绝，未知能力不能成功。

正例：宿主从元素配对的真实安装来源及当前账号创建 scope，应用申请 `saves` 后写 `game`；返回桌面、唤回单实例或继续游戏不会改变 scope 或重建游戏。反例：把 `slot` 设为 `../other/game`，在参数里伪造另一个账号，或把应用 A 的选择器 handle 发给 B，均不能获得该数据。先登录 A 再登录 B 时，B 没有 A 或游客存档是默认行为；需要用户明确选择迁移。

`ScopedStorageTests.Cases()` 覆盖 A/B/游客/来源、槽位与配额、路径越权、字节级备份恢复、幂等、取消、中断、模拟磁盘满、坏数据、较新 schema、旧游客记录、显式迁移与中断重试、格式迁移失败、提交时账号代次变化、提交租约、维护竞争、句柄身份/权限/过期/容量、外部导出及配置恢复。磁盘满是注入 Windows 错误码 112 的自动化故障测试，**不是耗尽真实磁盘的测试**；时间过期使用模拟时钟。旧 Storage/T02/Runtime 测试应继续执行。

跨开发包不会静默寻找、搬迁或合并另一个目录的 `AutumnOS_Data`。用户迁移应先正常退出，备份源目录和目标目录，核对构建版本、来源与账号，完整复制到新的自有目录并保留旧目录，不能直接覆盖已有目标存档。正式跨发行数据导入工具/兼容矩阵若未有真实报告，应保持未执行；便携 DPAPI 凭据换机重新认证，不能为方便迁移而改成明文。
