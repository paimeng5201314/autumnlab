# 03 · 对接地图与协议要求

本文件是待实现的合同要求，不是已经可调用的 API 文档。下面的接口名称为工程命名基线；真实签名冻结后需从代码和 schema 生成准确文档。

2026-10-01 T03 实现增量：实际可调用签名以 `src/AutumnOS.Contracts/`、`sdk/autumn-sdk.d.ts`、`sdk/message.schema.json` 和 [T03 SDK 文档](../../docs/t03-sdk-permissions-desktop.md) 为准；身份见 [原生账号指南](../../docs/t03-native-account-guide.md)，数据见 [存储与迁移](../../docs/t03-storage-and-migration.md)，独立服务器见 [ADR 0005](../../docs/adr-0005-independent-server-identity.md)。本页保留跨阶段要求，未接通的接口仍返回能力不支持，不根据下表名称宣称已经可用。

## 三种边界

1. 主程序内部 C# 接口：模块之间调用，由进程内部可信代码使用。
2. 第三方应用 SDK：经网关、权限、应用实例校验的受限消息协议。
3. 服务端 HTTP 接口：在远端验证身份、权限和数据归属，独立于客户端 UI。

不得把第一类接口直接暴露成第二类。客户端提供了用户资料不代表第三方服务器已经验证该用户。

## 内部模块合同

| 接口/模块 | 主要输入输出 | 消费者 | 必测失败场景 |
|---|---|---|---|
| IInstallationRoot | 入口目录、写入能力、数据根 | 全模块 | 相对路径、快捷方式工作目录、只读目录 |
| IAppRegistry | 应用身份、安装版本、来源、注册状态 | 桌面、商店、运行器 | 重复安装、部分事务、来源替换 |
| IAppRuntimeManager | launch/activate/suspend/resume/close、实例状态 | 桌面、多任务、更新器 | 启动中、崩溃、强制关闭、焦点恢复 |
| IMaintenanceCoordinator | 启动锁、保存/安装/迁移租约、维护锁 | 运行、存档、安装、更新 | 检查后立即启动的竞争、进程崩溃、超时 |
| IApplicationGateway | 消息、实例身份、会话代次、结果 | 运行环境、SDK | 伪造 appId、页面跳转、过期请求、超大消息 |
| IIdentityService | 登录状态、资料、登录/退出/切换 | 账号 UI、网关、服务端适配 | 取消、过期、离线、错误回调 |
| IPermissionService | 清单权限、用户决定、撤销、查询 | 全部敏感服务 | 未声明、拒绝、重复骚扰、升级新增权限 |
| IStorageService | 应用/用户命名空间、文件能力句柄 | 设置、存档、SDK | 越权路径、文件锁、配额不足 |
| ISaveService | 槽位、内容、版本、备份、恢复 | 游戏 SDK、存档 UI | 写入崩溃、格式不兼容、账号切换 |
| ISettingsService | 版本化配置、订阅变化、迁移 | 桌面、系统应用 | 损坏配置、旧值迁移、写入中断 |
| IStoreCatalog | 查询、分页游标、过滤、缓存时间 | 商店 UI | 空结果、限流、截断、元数据错误 |
| IGitHubClient | 公共 API、Release、附件、响应元数据 | 商店、更新 | 失效链接、重定向、权限/限流错误 |
| IDownloadService | 来源、目标、重试、进度、校验 | 商店、更新、安装 | 断点失效、代理错误、磁盘满 |
| IPackageManager | 安装/更新/卸载/修复事务 | 商店、应用管理 | 路径穿越、半安装、运行中更新 |
| IUpdateService | plus/meta 候选、准备、状态 | 设置、系统通知 | 错误渠道、回放旧版、签名错误 |
| IUpdaterHandoff | 经验证的交接、维护锁状态、启动健康 | 独立更新器 | 中断、文件占用、健康失败、更新器升级 |
| INotificationService | 通知、角标、点击操作 | 系统应用、SDK | 静音、撤销、重复、应用已卸载 |
| IDeepLinkRouter | 协议、应用身份、受限参数 | Shell、运行管理 | 未安装、恶意参数、外部方案 |
| IDiagnostics | 结构化日志、关联 ID、故障包 | 全模块 | 令牌泄露、用户内容泄露、日志占满 |
| ICapabilityRegistry | 支持能力与协议版本 | SDK、商店兼容筛选 | 能力缺失与未知协议 |
| IDeveloperService | 项目预览、校验、打包、调试 | 开发者系统应用、CLI | 模式关闭、无效清单、调试数据泄露 |

公开服务不得暴露任意文件路径或任意命令执行。系统文件选择器返回经授权的能力句柄；网关在每次使用时验证所属应用和有效期。

## SDK 消息格式

每个请求至少包含 protocolVersion、requestId、method、params。appId、实例身份、权限、当前账号和会话代次由宿主绑定，不相信消息自己声明的值。

成功响应：requestId、ok=true、result。错误响应：requestId、ok=false、error(code,message,retryable,correlationId)。不要在错误中返回堆栈、令牌或本机私密路径。

定义最大消息大小、请求频率、超时与取消、并发上限、幂等键以及升级策略。退出的应用不能继续获得返回结果。恢复后先获取最新状态，再订阅事件，避免只依赖一次可能丢失的通知。

## 公开 SDK 能力清单

| 能力 | 示例方法（待实现） | 核心权限或限制 |
|---|---|---|
| 平台能力 | platform.getCapabilities | 只读；未知能力不可假成功 |
| 生命周期 | lifecycle.getState / onStateChanged | 仅当前应用实例 |
| 用户资料 | identity.getProfile / requestProfile | identity.profile；仅授权字段 |
| 应用服务端登录 | identity.beginAppSession | 服务端验证方案独立冻结 |
| 权限 | permissions.query / request | 清单必须先声明；由用户动作触发 |
| 私有设置 | preferences.get / set | 应用+用户命名空间；类型与大小校验 |
| 私有文件 | storage.read / write | 逻辑键或句柄，不接受任意磁盘路径 |
| 外部文件选择 | files.pickOpen / pickSave | 明确用户选择；句柄不可跨应用 |
| 存档 | saves.list / read / write / restore | 原子写入、格式版本、迁移、账号隔离 |
| 网络 | network.getStatus / request | 声明域名、重定向核验、超时和大小 |
| 通知 | notifications.show / setBadge | notifications；撤销和静音立即生效 |
| 外观 | appearance.get / onChanged | 主题、语言、缩放、减少动态效果只读 |
| 输入和窗口 | window.getViewport / input.onBack | 不绕过系统焦点；快捷键冲突规则 |
| 桌面扩展 | shortcuts.register / widgets.update | 清单声明；负载受限；非任意脚本 |
| 深层链接 | links.openInternal | 应用身份与参数校验，不执行 shell |
| 诊断 | diagnostics.log | 脱敏、限速、容量限制 |

方法名在真实 API 冻结前允许由 ADR 调整，但不得多个工作组同时使用不一致命名。文件清单与运行中权限状态分开；主题订阅与身份订阅不能使用同一越权广播总线。

## 用户资料端到端流程

应用先检测平台能力；检查 identity.profile；在用户操作后请求。网关验证运行实例和清单，权限服务展示来源与字段，身份服务根据当前会话返回最小资料。未登录可返回 AUTH_REQUIRED，由用户决定登录，不能强行弹浏览器循环。

拟返回 appScopedUserId、displayName、avatarUrl、isCached。应用范围的稳定 ID 如何形成必须写入身份 ADR；客户端随意哈希或昵称不得充当服务器认证。手机号、邮箱和完整 claims 不默认返回。

错误至少区分 AUTH_NOT_CONFIGURED、AUTH_REQUIRED、USER_CANCELLED、PERMISSION_NOT_DECLARED、PERMISSION_DENIED、PERMISSION_REVOKED、SESSION_EXPIRED、OFFLINE、CAPABILITY_UNAVAILABLE。

切换账号时增加 sessionEpoch，停止旧会话订阅，拒绝旧 epoch 的异步响应，重新打开数据命名空间并通知应用刷新。授权绑定到账号、应用身份/来源和具体权限，不绑定显示名称。若无法可靠切换运行环境中的账户数据，应先关闭该用户应用并提示保存，不能混用 Cookie/本地存储。

撤销权限后停止后续数据提供，并按生命周期要求关闭会话或清理受控缓存。不能宣称能够从不可信应用已经收到的内存或外传内容中远程收回资料；授权说明需强调最小化披露。

## 应用自己的服务器

读取 nickname/userId 不能作为登录凭据。可选实施路线包括独立 Logto 应用接入，或经平台服务端验证后签发面向目标应用的短期凭证；实施前用 ADR 选定一种规范，不在客户端“伪签发”。

任何后端都验证签名、签发者、audience、有效期、scope 和重放要求。不得把宿主 refresh token 发给第三方。游戏分数真实性、排行榜规则不能仅靠账号登录解决。

后端示例应有可执行开发环境、配置说明和负面测试；使用本地模拟身份时必须显著标为开发测试，不算生产登录验收。

## 数据与身份规范

AppIdentity 至少考虑 appId、GitHub repositoryId、发布者/签名指纹（存在时）和已安装来源；仓库同名或另一个仓库声明同一 appId 不得接管更新。

区分账户资料、应用私有数据、共享系统设置与应用安装文件；注销只清理会话，不无声删除存档。存档格式版本与应用版本分开，备份/恢复要校验内容和目标账号。

安装成功事件只在事务提交并完成注册后发送；下载完成、验证完成、安装完成是三个状态。重复请求应复用任务或返回已处理，不重复覆盖。

## WebView2 安全验证

每应用采用隔离的来源与配置数据；账号切换也要隔离持久化状态。页面跳转、子框架、host messages、ExecuteScript 和 host objects 分别审查。宿主以普通用户权限运行。[S4]

网络不能只限制 SDK 的 network.request。还需评估 fetch、图片、脚本、iframe、WebSocket、WebRTC、Service Worker、下载、新窗口和外部协议等通道。在未证明覆盖前，不声称“完整网络沙箱”。某通道无法约束时必须禁用、采用更强隔离或明确列为发布阻塞，不能用文案代替隔离。

### T04 本地实现接口映射（2026-10-02）

接口由原生宿主使用，不新增第三方网页任意下载/安装权限。实现和输入/输出/取消/错误/数据归属的详细合同：根 docs/t04-catalog.md、t04-downloads.md、t04-installation.md、t04-developer-cli.md。

| 接口/类 | 当前实现路径 | 事件与边界 |
|---|---|---|
| IStoreCatalog | src/AutumnOS.Store/CatalogModels.cs / GitHubCatalog.cs | 匿名公开查询；页码与缓存时间；原生 UI 取消旧查询与代次检查 |
| IGitHubTransport | src/AutumnOS.Store/GitHubTransport.cs | API/附件来源区分；固定 HTTPS 跳转与头白名单；不承载 Logto |
| IDownloadService | src/AutumnOS.Store/DownloadModels.cs / DownloadService.cs | Changed 发布快照；完成字节校验进入 AwaitingInstall，不注册应用 |
| StoreInstallCoordinator | src/AutumnOS.Store/StoreInstallCoordinator.cs | 三层及来源一致才提交；原子提交与下载回执分离，关闭竞态重启核对 |
| ApplicationInstallService | src/AutumnOS.Packages/ApplicationInstallService.cs | runtime lease覆盖Starting至真实WebView释放；Changed在注册提交后发出 |
| InstalledSaveGuard | src/AutumnOS.Packages/InstalledSaveGuard.cs | 读取各账号/游客实际格式，降级前备份；关键操作协调不等于T05维护交接 |
| 本地发布CLI | tools/AutumnOS.Developer.Cli | pack/generate-release/validate-publication；只创建本地新输出，不上传 |

原 Store DTO 与三层 Schema 存档格式统一范围 1..1,000,000；Release 的 entry 另拒连续点号。一般第三方 WebView 仍有 T01 安全隔离前置缺项，PM 自标分类不会绕过。T03完整收尾暂缓，T04保持in_progress。
