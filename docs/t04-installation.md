# T04 应用安装与版本管理

Lab Chronicles AutumnOS，制作人：派蒙。适用于当前 T04 开发增量、包 schemaVersion 1；这是宿主内部接口，不向第三方 SDK 暴露安装、卸载或任意路径访问。

## 一个安装通路

商店下载、本地选择及拖入 .autumn 最终均调用 ApplicationInstallService.Install。普通 EXE 即使改后缀，仍会被原 PackageInstaller 的 ZIP、文件类型和 PE/ELF 内容检查拒绝。包内容在校验结束前不会执行。

原校验器继续限制归档 20 MiB、128 条目、单文件 8 MiB、展开总量 16 MiB；拒绝越界、Windows 特殊路径、ADS、大小写碰撞、重复路径、重解析点和链接、压缩炸弹、非法入口及不支持权限。安装服务在同一打开的只读文件租约内检查实际字节，之后仍由原安装器再次校验和解包。可用空间预检不能保证后续磁盘永不变满，IO 失败仍按原子提交的实际状态处理。

首次来源由 UI 明确确认。网络包提供 ExpectedPackageIdentity，核对 appId、version、runtime、entry、permissions、SHA-256、文件长度及 saveFormatVersion。release 文件继续使用既有 13 字段；商店和开发工具共用 ReleaseManifestValidator，没有另一套宽松导入器。来源声明、哈希和仓库自标 PM 均不等于作者已审核或代码安全。

## 精确 C# 接口

~~~csharp
var saves = new InstalledSaveGuard(savesDirectory, applicationVersionBackupDirectory);
var installs = new ApplicationInstallService(appsDirectory,
    enterCriticalOperation: () => coordinator.TryEnterMaintenance()
        ?? throw new PackageException("PACKAGE_INSTALL_BUSY"),
    isRunning: appId => runtimeHasLiveInstance(appId),
    prepareSaveChange: saves.PrepareChange);
~~~

runtimeHasLiveInstance 是宿主运行状态查询适配函数，必须把 Starting、Foreground、Background、Suspended、Closing 视为存活。保存和安装使用同一宿主协调器，阻止提交期间新的关键写入。本接口不是 T05 独立更新器的跨进程维护交接。

| 接口 | 输入、输出及生命周期 |
|---|---|
| GetInstalled() / Find(appId) | 返回已安装记录；卸载项不返回。数组是当前快照，事件后重新读取。 |
| HasRegistration(appId) | 包括卸载墓碑；避免重启时重新安装用户已卸载的内置样例。 |
| Install(path, expected, intent, sourceConfirmed, downgradeConfirmed, cancellationToken) | 路径来自宿主下载缓存或文件选择结果；返回已提交 InstalledApplication，Package 可交给既有运行器。intent 是 InstallOrUpdate、Repair 或 Downgrade。 |
| RegisterExisting(package, source) | 仅迁入原安装器已验证的内置样例。已有记录优先，已卸载返回 null，不覆盖用户选择版本。 |
| EnterRuntimeLease(appId) | 创建 WebView 前取得；WebView 与会话完全关闭后 Dispose。返回桌面不释放。与安装共用 gate，阻止检查后立即启动的竞争。 |
| SetVersionPolicy(appId, pinCurrentVersion, allowPreview) | 原子保存应用固定版本与预览开关；与主程序 plus/meta 无关。固定时其他版本明确拒绝，用户先解除固定再选择。 |
| Uninstall(appId) | 存活实例时拒绝；原子取消注册并保存来源墓碑，重复调用幂等；不动 Saves、AppData、凭据。 |
| InspectRecovery() | 只读返回 committed / staged_not_registered，不把暂存内容自动登记。 |
| VerifyInstalled(package) | 按安装记录逐文件核验大小、哈希、缺失和多余文件，适合运行前及修复诊断。 |

接口同步，UI 在线程池执行磁盘工作。取消在校验、解包、提交前生效；注册提交以后不声称撤销已发生的安装。磁盘 IO 没有伪造即时取消；调用方取消等待不代表可以另开并行安装。服务内串行化，跨服务通过 .autumnos-registry.lock 文件句柄独占共享模式拒绝并发写入，不按普通文件存在判断锁。

Changed(ApplicationRegistryChange) 仅注册提交后触发。Kind 为 installed、updated、repaired、uninstalled、policy_changed。订阅者异常不回滚已提交操作；桌面重读注册表，重复安装不重复发事件。事件可能来自工作线程，Shell 派发到 UI 线程。

## 来源连续性与版本不可变性

PackageSource.Kind 为 github、local、bundled 或仅隔离测试使用的 test。GitHub 绑定稳定 repositoryId，owner/name 仅展示；重命名/转移但 ID 不变可继续更新，同名新仓库、相同 appId 的其他来源、本地包接管 GitHub 应用均拒绝。当前不支持来源迁移，卸载也不删除来源墓碑。无需认可名单。

HostSource 为 github:<repositoryId>、local:<appId>、bundled:<appId>、test:<repositoryId>，不随包哈希或版本改变。旧元素配对保持 bundled:cn.labchronicles.elementpairs，存档命名空间不分裂。

PackageProvenance 留存 ReleaseId、AssetId、StoreContentSha，不含登录令牌。ReleaseMetadataSha256 仅用于真实收到的原始字节摘要，未提供时保持 null。当前协调器另存 ReleaseCanonicalSha256：按共享 ReleaseMetadata.JsonOptions 将严格解析后的固定 13 字段记录序列化，再计算 SHA-256；它不冒充 HTTP 原始字节或任意 JSON 签名。缺失签名不伪称验签通过；当前传入签名指纹返回 PACKAGE_SIGNATURE_UNSUPPORTED。未来签名协议必须真正验证后开放，不能把指纹相同比较当验签。

注册表保留历史 appId/version/archive SHA-256；切换到新版本后仍不接受同一历史版本内容被替换。SemVer 按数值和预览标识比较，不按发布时间、标题或字符串大小。固定版本、应用预览偏好跨重启保存；当前没有暗中自动更新。

## 提交、中断、修复、卸载

内容进入 Apps/.content/<transactionId>/<appId>/<version>，由原安装器在同卷临时目录完成后移动；写入 .transactions/<id>.json 暂存记录。最后用 File.Replace 或初次 File.Move 原子切换 .autumnos-registry.json，保留 .bak。旧包目录与用户数据不覆盖。

提交前中断：旧记录有效，暂存没有图标。提交后收据写入前中断：注册表是最终事实，重启仍找到已提交应用。故障注入明确区分两个边界，不把所有崩溃都称为可撤销。再次选择同一包幂等继续；未登记暂存和历史包保留诊断，不自动删除用户文件。

修复使用当前安装版本同一已知摘要归档，逐文件核验后切换到新的不可变目录；损坏旧目录保留证据，存档不变。下载最新版不能冒充修复。

卸载取消注册和桌面入口；当前历史内容保留为恢复缓存，**不会立即释放全部安装字节**。它与清缓存、删除存档不同，本轮不提供静默删除历史证据的任务。Shell 收到卸载事件清理订阅、快捷动作和图标；后台 WebView 需用户明确结束。

## 实际存档兼容性及降级备份

manifest 新增可选整数 saveFormatVersion（旧包默认 1）、minReadableSaveFormatVersion、maxReadableSaveFormatVersion（缺少时等于当前格式），均限 1–1,000,000，区间须覆盖当前格式；release 的 saveFormatVersion 与包一致。

InstalledSaveGuard 扫描目标 appId 下真实全部来源、游客及账号主存档，验证 schema、appId、目录绑定、slot、revision、内容哈希及实际 formatVersion；兼容旧样例五字段 guest/game schema 1。损坏记录或任一账号格式不兼容时拒绝，不仅凭 release 声明通过。

降级要求 InstallIntent.Downgrade 及 downgradeConfirmed=true。先核对兼容，再在独立备份根写全部存档及既有备份的逐字节副本，flush、读回 SHA-256，最后写 .backup-complete.json；失败不切换应用。LastBackupPath 指向完整备份。备份不等于旧版可读新格式；不兼容仍拒绝。缺少真实存档检查委托时，带 saves 权限的版本变更返回 SAVE_COMPATIBILITY_UNAVAILABLE。

扫描/备份上限 4096 文件、每文件 8 MiB、总计 256 MiB，超出明确拒绝。备份不复制宿主凭据、不执行迁移脚本，不跨开发包搬数据。

## 错误及验证

PackageException.Code 仅含固定错误码，不包含不可信文本、用户内容或私密路径。

| 错误 | 后续操作 |
|---|---|
| PACKAGE_SOURCE_CONFIRMATION_REQUIRED | 展示真实来源/权限，明确确认后重试。 |
| PACKAGE_SOURCE_CONFLICT / PACKAGE_VERSION_CONFLICT | 停止并核对来源或发布新版本，不覆盖。 |
| PACKAGE_RELEASE_MISMATCH / PACKAGE_HASH_MISMATCH | 重取绑定清单/附件，不执行内容。 |
| PACKAGE_APP_RUNNING | 保存并结束实例；返回桌面不算结束。 |
| PACKAGE_VERSION_PINNED / PACKAGE_PREVIEW_DISABLED | 明确修改该应用版本策略。 |
| PACKAGE_DOWNGRADE_CONFIRMATION_REQUIRED | 确认降级，仍须通过真实存档检查。 |
| SAVE_FORMAT_INCOMPATIBLE / SAVE_CORRUPT | 保留新版本与数据，使用受支持的存档恢复。 |
| PACKAGE_DISK_FULL / PACKAGE_IO_ERROR / PACKAGE_INSTALL_BUSY | 释放空间或解除占用后重试，原记录保持。 |
| PACKAGE_CANCELLED | UI 重新读取当前注册状态，不能凭取消假定回滚。 |
| PACKAGE_REGISTRY_CORRUPT | 保留损坏记录和 .bak，进入诊断，不清空注册表。 |

tests/AutumnOS.Tests/StoreInstallTests.cs 使用独立临时目录与真实 .autumn ZIP，覆盖字段不符、源冲突、同版替换、重复注册、策略恢复、启动竞争、修复、卸载保留、提交前后中断、多个账号格式、坏存档及降级备份失败。磁盘满为受控异常注入，不宣称实际填满系统盘。真实 GUI、GitHub Release、隔离 HTTP 链由集成报告分别记录。

StoreCoordinatorTests 通过注入 HTTP handler 使用同一个 GitHubTransport、DownloadService、包校验器、安装器及注册服务验证本地受控字节链。已完成任务也重新核对发布声明与包内权限/入口/运行时/存档格式，幂等捷径不能绕过三层一致性。安装意图表限制 1 MiB / 200 条 / 深度 20，拒绝重复/未知字段、null 项、不合法来源及标识；错误统一 STORE_INSTALL_HISTORY_INVALID，不重建空表掩盖失败。

注册提交后下载服务刚好关闭、取消或完成收据写入失败，不把已提交应用报告为安装失败。StoreInstallCoordinator.RecoveryWarnings 返回有限固定码 INSTALL_RECEIPT_RECONCILIATION_REQUIRED；重建协调器时，对待安装/中断安装任务逐项核对任务身份、持久意图、当前注册的来源/版本/摘要、包清单与实际安装文件，只有一致且完整才将收据补为 Completed。损坏文件或不一致保持待恢复并记录 INSTALL_RECONCILIATION_REJECTED，不因存在目录就推断成功。相关故障通过真实注册提交点与已释放下载服务测试，未强杀用户进程。
