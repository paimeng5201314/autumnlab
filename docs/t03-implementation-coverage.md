# T03 实现与证据覆盖审计

Lab Chronicles AutumnOS，制作人：派蒙。审计时间 2026-10-01 18:19（本地），以正在修改的本地源码为准。这是增量审计，不是最终验收报告，不修改规范验收状态。后续实现与最终冻结证据应追加，不覆盖失败报告。

## 证据口径

`artifacts/reports/t03-foundation/solution-compile-04.log` 为实际 Release 构建，零警告、零错误。最近完整 C# 运行 `tests-03.json` 为 **306/306 passed**；其 build_id 是 `local-untracked`、source_snapshot_id 是 `not_recorded`，且早于随后新增的取消、恢复与迟到刷新测试，不能证明最新字节全部通过。`identity-tests-01` 的 215/216 与 `tests-02` 的 272/274 失败记录保留，不因后续修正删除。

Windows 实证 `account-ui-01/account-smoke.json` 为 **30 项 passed**：真实 WinUI 账号页面、未登录状态、保持登录默认关闭、端口占用、登录中重复启动仍原主 PID/监听、取消后端口释放。主 PID 13640、HWND 9110504；转发 PID 21608 退出 0。使用构建目录的隔离副本，不是最终交付目录；没有真实用户完成浏览器认证。截图为真实窗口捕获，视觉审查范围另看独立记录，不能把存在 PNG 等同于全部逐图审查。

`discovery-live-01.json` 为真实公开 HTTPS 发现校验 passed，明确 login=not_run；只读发现不是账号认证。Native/public 类型与精确登录回调由派蒙确认；退出回调尚未确认。`server-http-01.json` 为实际本机 HTTP **7/7 passed**，使用显式合成配置验证无配置 fail-closed、裸身份和未认证请求被拒绝；没有对真实 API resource 做正向联调。

## 主责与交叉要求

| 需求／验收 | 当前实际实现 | 已有自动化／本机证据 | 仍需补齐或实测 |
|---|---|---|---|
| R030 / A030 | 系统浏览器、授权码 PKCE S256、事务回环、签名/issuer/audience/nonce/state、UserInfo sub、取消/超时、DPAPI、串行刷新、本地与浏览器退出；双栏账号 UI | 真 TCP 回调＋实际 Duende 协议 adapter 的合成 HTTPS/RSA fixture；真实 DPAPI；账号 Windows 30 项；真实公开 discovery | 用户实际登录往返、保持登录后实际 refresh/重启恢复、服务端退出、真实离线/失效现场；退出登记待确认；最终包尚未生成 |
| R031 / A031 | 已声明权限、真实原生输入、宿主显示应用/来源/用途/字段，拒绝与撤销保存；最小资料与按应用权限页 | Permission/Runtime fixtures：拒绝、撤销、新权限、伪造来源、队列撤销、受限资料；SDK 不导出宿主凭据 | 最终 EXE 的授权/拒绝/设置撤销/新增权限交互；真实登录资料往返 |
| R033 / A033、X02 | 已验证 issuer+sub 派生命名空间、sessionEpoch、最终提交租约、旧工作取消；切换前要求用户已保存并同意关闭；WebView 按账号/来源独立目录及 InPrivate，实际 dispose | A/B/游客、迟到响应与写提交竞争、权限事件清理 fixtures；账号变更被用户取消的 UI 逻辑已接线 | 两个真实账号交替、Cookie/localStorage/IndexedDB/cache 实机隔离、拒绝关闭保留未保存输入；新增迟到刷新修复需再跑最新 tests |
| R034 / A034 | ADR 0005 选独立 Logto Native 客户端与 API resource；真实 .NET HTTP server、资源 JWT 验签/对象/权限、有限一次性挑战、OpenAPI | 15 项签名/错误对象/过期/裸身份/挑战 fixture；server 无配置 exit 2 与 HTTP 401 实测 7 项 | 审计时缺可运行独立 Native **客户端**，README 仅要求开发者自行实现；已分配补齐。独立应用、resource、scope 和真实权限登记/正向联调仍 blocked，不能借用宿主 token |
| R037 / A037 | 私有文件、32 存档槽、配额、原子替换、校验与备份、旧游客兼容、显式游客复制、格式迁移服务、系统选择器受限读/写句柄 | ScopeStorage 测试覆盖隔离、越权、中断、损坏、配额、幂等、恢复、迁移、最终租约；磁盘不足为注入错误码 112，时效为模拟时钟 | 真实系统选择器取消/导入/导出/恢复 UI、实际登录游客迁移与冲突；格式转换服务没有任意 JS 执行入口，具体未来应用版本的转换尚未登记 |
| R038 / A038 | VersionedConfigurationStore Load/Save/Migrate/RestoreBackup；原配置和首启格式保留，developer-mode 使用新版存储 | 配置旧格式、较新格式拒绝、失败不覆盖、备份恢复 fixtures；原 Desktop 存储测试保留 | 审计时生产没有 Migrate/RestoreBackup 调用，损坏 developer-mode 后只关闭并禁用开关，缺用户可操作恢复入口；集成负责人正在补齐。跨包迁移只明确手工说明，未做自动搬迁 |
| R039 / A039 | 内部通知/去重/静音/角标、声明的快捷操作/内部动作/纯数据小组件、SDK 快照与事件、关闭清理；现有桌面和通知容器接线 | Runtime/desktop/SDK fixtures；真实样例面板已调用 SDK | 最终 EXE 通知、角标、菜单、事件/撤销、开发预览关闭清理实测；当前静音 UI 仅受控元素配对；AnimationsEnabled 未见直接变化订阅，需补监听或如实限制；外部 Windows 协议/系统 toast 未实现 |
| R040 / A040 | 持久化开关、真实开发者桌面入口、独立预览、有限项目打包与检查、发布资料校验、生命周期/权限诊断、200 条脱敏 trace、关闭取消并撤销 | 10 项 DeveloperTools 测试；mode-off 取消、包字节检查、拒绝原生/隐藏/数据目录、输出不覆盖、trace 容量等 | 最终 EXE 真实选择项目→打包→预览→关闭模式，普通游戏/数据不受影响；发布资料缺可直接由样例生成的完整演练记录；文档独立空项目验收未跑 |
| X01 | 原 .autumn 安装运行与保存保留；新增关键写计数/维护租约作为未来更新输入 | 旧 T02 检查点与本轮写入竞争 fixtures | GitHub 获取/安装、T05 安全替换/回退不在本轮，不能把写租约标为更新完成 |
| X07 | SDK 类型/Schema、C# 服务、受控身份/存档/桌面样例、开发工具、服务器负面样例文档已有 | 测试实现及 server 实际本地负面请求 | 独立开发者按文档从空项目打包与接入尚未实际演练；R034 客户端正在补；跨 T04/T06 的发行文档门禁保持未完成 |

## 接线与文档核对

1. 真实已实现的功能不因 A030 等仍 `not_run` 被重做。规范状态保持整个验收未通过是合理的，但应在本轮冻结后追加部分证据，不能长期留空掩盖实际工作。
2. 数据默认按游客/A/B/来源分离。`RuntimeServicesUi.OnImportGuestSave` 已有明确二次确认并仅复制 `game` 槽，不能报告“游客迁移无 UI”；批量选择/其他应用槽位不在此入口已实现范围。`AccountDataStore.MigrateSaveFormat` 是可信宿主方法，尚无生产格式升级注册表，不应宣称所有未来版本能自动迁移。
3. WebView 实际使用 InPrivate 与分账号/来源环境目录，并在账号变化前关闭相关 WebView。此源码边界不代替浏览器隔离实测；`networkSandboxVerified=false` 保留，T01 全网络/导航/物理输入安全矩阵不能被 T03 fixture 代替。
4. `appearance.changed` 已接 ActualThemeChanged、XamlRoot.Changed，可反映主题与 DPI 变化；减少动态效果读取是真值，但仅此并不能证明它的独立动态变化立即同步。界面只支持 zh-CN，无假语言切换。SDK 文档共同约定与实际样例的“订阅＋快照竞态处理”应统一。
5. 清缓存、清数据、卸载和删存档未合并成破坏性按钮；本轮没有因为退出身份删除存档。实际卸载事件尚未由 T04 生命周期接入，当前只证明应用关闭/账号变化后的清理。
6. `sdk`、受控样例、服务器 README 与 ADR 是真实文件，不能把 docs 列出接口视为那些接口均已在 Windows 上执行。自动化时钟、RSA、HTTP handlers 与实际 Logto 成功必须分别归档。
7. 本轮最终 EXE、ZIP、新快捷方式、源快照和 PRI/XBF/字节检查尚待集成生成。旧 T02 包与实机截图是保留基线，不是 T03 新产物证据。

## 接下来的独立工作与外部边界

独立可推进：补独立 Native 客户端与协议 fixtures；配置恢复入口；减少动态效果事件；最新全量 C#/SDK 回归；最终目录 Windows 授权、文件、通知、开发工具与 T02/单实例回归；冻结哈希后验证最终 EXE，再 ZIP。

必须真实用户/配置的项：派蒙亲自完成浏览器认证（不收集密码、验证码或 token）；确认退出回调；第二真实账号；独立开发者 Native app 与 API resource/scope/角色登记。这些缺项应逐项保留 blocked/not_run，不能以本机 fixture、配置可读、能显示窗口或服务 health 回应替代。

## 18:33 本地补齐记录

上述 R034 客户端缺项已实际补上 `samples/native-identity-client/`，不是仅修改说明。它是可执行 .NET Native 进程，以独立 ClientId、固定127.0.0.1:17854/callback/、系统浏览器、PKCE、resource授权及代码交换获得内存凭据，随后请求固定本机5197服务器，核对同一主体并消费一次性挑战。不复用宿主ClientId或17853，不读取AutumnOS_Data，不接入任意EXE导入；SDK平台委托能力仍明确不支持。新增 public config模板/Schema、准确控制台登记与角色字段指南、失败/取消/限额/生命周期文档。

真实执行 locked restore 与 Release build，`native-client-restore-02.log`、`native-client-build-02.log` 为零警告/错误；独立运行 `--self-test` 的 `native-client-tests-02.json` **17/17 passed**。这是显式合成 HTTPS/API、临时RSA及真实TCP回调，real_logto=not_run。进一步对实际样例EXE执行无参数和空公开模板，`native-client-entry-01.json` 验证分别exit2/4、前后17854无监听，不打开浏览器。均为辅助local-untracked构建，最终全工程冻结还需重跑。

集成负责人已将两个服务器/客户端真实项目接入解决方案和正式测试脚本；最新代码由BrandInfo写入fixture报告构建身份。18:33之后追加的顶层异常脱敏防御还需下一轮构建，不能拿02报告冒充该字节已运行。R038恢复UI、系统动画事件等其他接线由集成继续补证，保留上述时间点的发现，不倒写成从未缺项。真实独立Logto登记/正向认证仍blocked/not_run，未操作管理控制台或创建远端服务。

## 冻结前签名兼容与配置来源复核

发现服务器示例算法集合遗漏本地公开发现已观察到的ES384。已在原白名单中明确加入ES384，未关闭算法/签名校验；ServerIdentityTests和IdentitySessionTests各新增临时ECDSA P-384有效签名与同keyId伪签名测试，等待整合负责人06轮执行。独立Native样例已实际执行ES384完整metadata/JWKS/PKCE/ID token/API签名链及两种错签名负例，加上缺宿主配置fail-closed，`native-client-es384-tests-01.json`为**21/21 passed**，`native-client-build-03.log`为零警告/错误。该构建已包含此前顶层异常脱敏改动。

宿主ClientId防复用不再在Identity/sample源码或Schema硬编码真实值；sample项目MSBuild链接复制唯一`autumnos-spec/config/logto.public.json`，通过既有严格Loader读取，缺失/无效时拒绝运行。实际源和输出配置SHA-256均为`1723776a22b94e2ccebc4c022bccff0fd89e6503806eb23b42e9a1abfea06063`。一次拟用`native-client-tests-03.json`的调用发现报告已存在，runner返回REPORT_ALREADY_EXISTS并保留原JSON；改用全新es384报告后才执行上述21项。最新最终快照、Windows交付与真实Logto状态由后续Progress/spec/构建报告承接，不倒填本增量审计为完整通过。
