# T05 本地双包与签名材料

Lab Chronicles AutumnOS · 制作人：派蒙。仅本地工程，不执行 Git 或上传，不创建 GitHub Release/标签。正式发布需另行授权；本次仅修改和验证本地源码，不提交、不推送、不创建或发布 Release。

**2026-10-02 制作人范围调整：安装版暂缓，本轮只交付可直接运行的便携目录/ZIP。** 已有 Setup 源码、产物及测试证据保留，不把暂缓项改为通过。`Package-T05.ps1` 默认仅生成便携产物，记录 `setup=deferred_by_user`；下述安装流程属于保留的实现，未来明确恢复该项工作后才使用 `-IncludeSetup`。本轮继续验证便携 EXE 的签名更新、原入口、数据和失败恢复。

## GitHub 版本身份与本次边界

2026-10-10 只读核对官方仓库：Latest 稳定版为 Release `402398412`，tag=`meta`，名称=`v0.0.1-meta0.0.1`，`prerelease=false`；另一个已发布预览版为 Release `406973009`，tag=`meta0.0.2-20261007`，`prerelease=true`。前者只有 `AutumnOS.exe`，后者有 `AutumnOS.exe` 和 `SHA256SUMS.txt`；两者都没有可信更新所需的 `autumn.update.json`、`autumn.update.sig` 和 ZIP 负载。历史文档的“仍为草稿”已经不是当前远端事实。这次没有修改这些发行版。

版本沿用 GitHub 的实际发行体系，本次不自行指定下一版本。需求第八节 A 中的 0.6/0.7 版本只用于测试。展示名、Release tag、内部 SemVer、构建 ID 分别记录：`Build.ps1 -ReleaseLabel` 控制显示标签，`-BuildVersion` 控制可比较版本，程序集 `SemanticVersion` 保存后者。`build/Brand.props` 的 `0.5.1-local.1` 是本地工程默认值，不代表最新公开版本；历史已交付 `meta0.0.1-20261001` 的内部版本为 `0.5.1-local.4`，不能从此推算另一发行版。

非 SemVer 的历史 tag 不按日期或标题猜版本。维护者发布新材料时，用签名清单的 `releaseTag` 明确绑定所选 GitHub tag 与经确认的内部 SemVer；缺少可靠映射或可信资产的历史条目只能查看。GitHub 稳定/预览字段必须与签名清单及其 SemVer 一致，不能因名称含 `meta` 就改判渠道。Release 发布说明唯一来源是 GitHub `body`，无需另填一份更新说明。

## 构建输入和产物

本机固定 SDK 在 `.tools/dotnet`；依赖版本与 `packages.lock.json` 不浮动。新增 Update、UpdateProtocol、Bootstrap、Updater、Setup 和 Update.Cli 复用 BCL 及既有项目依赖，没有新安装全局工具。`scripts/Build.ps1 -BuildId <新ID>` 对实际源字节生成快照，Rebuild 并核对程序集 BuildId/SourceSnapshotId 和 deps。`scripts/Test.ps1` 执行本机 C#、SDK 长会话、样例、合成身份协议及真实本地 CLI 回归。测试信任根和 feed 的**公开**构建输入额外纳入源快照，私钥禁止进入。

`scripts/Package.ps1 -Checkpoint T05 -Stage Prepare` 从已跟踪编译输出 publish WinUI 客户端，补齐实际 PRI/XBF，publish 两个独立自包含单文件稳定 EXE；所有文件有白名单/哈希。`-Stage Full` 或准备后 `-Stage Finalize` 在交付字节执行原生回归，再生成 ZIP、读回校验每个条目。仅在以后恢复安装包范围并明确传入 -IncludeSetup 时，才将同一 ZIP 内嵌进实际编译的 Setup EXE。Setup 不等于改扩展名的 ZIP；它执行预检、路径检查、空间检查、暂存、文件安装、当前用户注册和保留数据卸载。失败报告不覆盖，必须使用新 BuildId。

`Package-T05.ps1 -SkipNativeRegression` 仅允许显著标识的测试变体，其最终验证由更新专项演练负责；普通开发包禁止跳过原生回归。`package-result.json` 中 `setup_execution=not_run` 直到真实运行安装器测试，生成 EXE 不等于安装通过。ZIP 排除 AutumnOS_Data、事务日志、私钥、凭据和启用的测试配置文件。普通包没有编译测试根或 feed。

## 三项运行环境

客户端 `.NET SelfContained=true`，Windows App SDK 同样单独自包含；打包验证 coreclr.dll、Microsoft.UI.Xaml.dll、deps、PRI/XBF。Bootstrap/Updater/Setup 单文件内嵌其 .NET 与托管依赖，在更新业务 DLL 时不依赖同根旧 DLL。

固定 WebView2 Runtime 选择微软官方 x64 `154.0.4258.53`，与 NuGet WebView2 SDK `1.0.4258.31` 分开记录。`scripts/Cache-WebViewRuntime.ps1` 从固定微软 HTTPS 链接下载 CAB，解压到项目 `.tools/webview2`，校验运行时 Microsoft Authenticode 和文件版本；不运行 Runtime 安装器，不修改系统 PATH。发行包复制完整目录至 `WebView2Runtime`，WebAppHost 明确传该目录。Loader.dll 不是完整 Runtime。开发输出缺固定目录可使用本机 Evergreen，但本脚本交付模式缺固定 Runtime 会失败。

固定运行时不会通过 Evergreen 自动更新；以后升级需重新选择受支持且验证过的版本，锁定官方 URL/摘要/签名并随受管理 payload 更新。离线包内依赖齐全与干净机器/Windows10离线首次运行是不同证据，后者未执行不能通过。第三方许可证保留在 ThirdPartyLicenses 和 Runtime 自带文件。

官方依据：[Windows App SDK 自包含](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)、[WebView2 分发](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)、[微软固定 Runtime 下载](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)。这些资料支持部署选择，不能代替本机测试。

## 安装和卸载

Setup 默认 `%LOCALAPPDATA%/Programs/AutumnOS`，普通用户运行。通过原业务 Mutex、T05 维护门、按物理安装根的文件占用租约协调；有用户实例则拒绝并提示正常退出。已有安装的不同版本走客户端签名协议；同一安装器可以幂等重试。非冲突未知文件及既有 AutumnOS_Data 原位保留，任何计划路径上的未知文件或用户改动都在程序提交前拒绝覆盖。ZIP 校验拒绝 Windows 设备名、非法字符、越界、数据/维护路径、大小写重复、父文件与子路径冲突、符号链接/重解析/目录/设备等特殊条目，并限制数量、单文件、展开总量和压缩比例。

安装使用私有 `.autumnos-setup/<事务ID>/stage`，阶段字节、文件表与 SHA-256 同实际内嵌 ZIP 对应。`.autumnos-setup/journal.json` 在首次程序写入前持久化，阶段为 `prepared → applying → filesCommitted → completed`。每个文件经临时副本、摘要复核、`Flush(true)` 后替换；稳定 `AutumnOS.exe` 最后写入，确保中断的新安装不会先暴露半套业务资源。中断后使用同一 payload 和安装器恢复，已经写入且摘要一致的受管理文件跳过；改变过的文件保留并报错。不同安装器不能接管进行中的事务。注册表/开始菜单写入失败时程序文件保留，重跑同一安装器完成注册。

卸载先持久化 `.autumnos-setup/uninstall.json` 中的当前更新后受管理文件表与初装稳定引导身份，再逐项删除摘要仍一致的文件。先移除未改动的稳定入口；即使随后中断、当前 `autumn.install.json` 已删除，重试仍依靠持久卸载清单完成。更新后的受管理程序文件使用当前安装清单，用户改动、未知文件、AutumnOS_Data、更新备份和安装回执保留。完成后写 `uninstalled.json`，允许在仅有历史元数据、卸载器、用户数据与非冲突未知文件的目录重新安装，不把这些残留误判为永久不可安装。存在改动文件与新 payload 同路径时仍停止，交由用户保留/处理；不递归擦除根目录。运行中的卸载 EXE 保留，不执行任意延迟 shell 清理，不能虚称全部磁盘空间已释放。

自动化安装测试仅使用 `--silent --test-name <ASCII唯一值>`，目标固定为当前用户 Programs/AutumnOS-Test-<值>，不能传任意覆盖根。`--uninstall` 使用同一测试名。默认交互界面为原生确认/结果对话框；不要求管理员，不修改系统安全策略，不伪造证书发布者。

## 签名工具实际接口

工具：`tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll`，使用项目 dotnet 调用。命令只输出结果、路径及公开身份，不输出私钥材料。

* `keygen-test`：生成 3072 位 RSA 短期演练密钥，私钥 PKCS8 保存在当前用户 ACL 限制的临时目录，公钥 JSON Purpose=local-test。只用于演练，结束删除临时私钥。此命令不生成正式生产信任根。
* `inventory --source <发行根> --output <新文件>`：输出与 payload 完全相同的受管理文件清单。双包脚本通过此接口生成安装回执，避免两套文件过滤规则不一致。
* `prepare --source <发行根> --output <全新目录> --version <SemVer> --build-id <ID> --channel plus|meta --sequence <递增数> --key-id <公开ID> --release-id <ID> --asset-id <ID> [--release-tag <精确tag>] [--minimum-updater-version <SemVer>] [--previous-manifest <原版本清单>]`：按安全白名单生成实际 payload、完整文件表、autumn.update.json；默认七天有效期，最大九十天。最低更新器默认 `1.1.0`。`--previous-manifest` 从旧文件表与新文件表的差集生成受管理删除声明。生成后重新校验 ZIP；没有传入 `releaseTag` 时省略该字段，保留旧格式严格解析的兼容空间。
* `bind-release --manifest <准备清单> --payload <冻结ZIP> --output <新清单文件> --release-id <实际ID> --asset-id <实际ID> --release-tag <精确tag>`：先验证已有 ZIP，再将真实 Release/Asset/tag 身份写入新清单。ZIP 字节保持不变，不覆盖原清单、不联网、不上传、不自动签名。解决“先准备 ZIP 才能获得资产 ID”的发布顺序问题；必须签署并发布重新绑定后的字节，不能沿用旧签名。
* `sign --manifest <文件> --private-key-file <受控PKCS8路径> --output <新sig>`：对确定原始 UTF-8 清单字节作 RSA-PSS/SHA-256 分离签名。命令行只传文件路径，不能传私钥内容。
* `verify --manifest <文件> --signature <sig> --trust <公钥JSON> --payload <zip> [--minimum-sequence <序号>]`：运行真实签名、有效期、版本化清单、文件表和ZIP字节验证。

`prepare` 不自动签名。生产签名在受控离线环境进行，客户端/交付包只含经确认的公开信任根。不向派蒙索要私钥、密码或令牌。更新签名与 Windows Authenticode 独立，开发EXE未签名必须如实标记；不绕过 SmartScreen 或关闭验签。

经制作人确认的生产**公钥**可通过 `Build.ps1 -ProductionUpdateTrustFile <公开trust.json>` 接入，Purpose 必须为 production，并进入实际源快照和稳定更新器。生产与测试根互斥。没有该输入时仍可构建本地正常版，但可信安装明确未配置，不能用测试根或空签名补位。

## 未来 GitHub 发布操作（未授权、未执行）

先在本地完成同构建测试和双包、确定版本/渠道及公开生产根。依照 [GitHub Releases API](https://docs.github.com/en/rest/releases/releases) 区分Release列表与latest；签名清单绑定最终实际Release/Asset身份，不能拿本地测试ID发布。未来获授权后，草稿中上传冻结 payload，得到实际 ID 后使用 `bind-release` 生成最终清单，在受控签名环境对最终清单签名，再上传这两项和校验材料；完成只读下载复验和最终审核后才发布。准备阶段使用的本地占位 ID 不能作为正式发布身份。plus使用无预发布版本且预发布标记false；meta使用预发布SemVer且标记true。不按发布时间选新版，不移动已公开版本Tag。

更新协议本次为 `1.1.0`。稳定入口/独立更新器当前不在热替换白名单；旧 `1.0.0` 辅助程序无法通过业务 payload 获得目标限定的回滚授权、数据快照和新协议。最低更新器要求不满足则拒绝，不能下调最低协议伪装兼容。

首个过渡版本需要维护者提供带新 Bootstrap/Updater 和经确认生产公钥的完整 EXE/便携目录。单文件用户先正常退出全部程序，保留同级完整 `AutumnOS_Data`，核对新 EXE 身份后人工替换原入口 EXE；不要复制旧 DLL 覆盖新发行目录，也不要删除旧缓存和事务证据。目录便携版使用完整新目录并按原数据根规则迁移保留的数据；有未完成事务时先用原入口恢复。旧公开 EXE 缺生产根和签名资产，因此不能宣称新代码能让它自动发现并安装此过渡包。

单文件入口将运行文件放在 `AutumnOS_Data/System/Product-<种子摘要>`；成功签名更新修改此已绑定运行目录，原 EXE 再次启动根据安装回执和事务验证当前版本，不用旧内嵌负载覆盖新版。新的持久入口绑定还会在曾使用的运行目录丢失时拒绝自动重新播种，避免悄悄恢复旧代码。更换完整 EXE 会产生新的种子目录，需要保留旧缓存以便审计和恢复。动态下载信任根轮换尚不支持；新生产根的授权、撤销和重建稳定组件需受控离线流程与独立演练，不能把签名更新实现宣传为完整TUF。

T06交接保留完整系统/安装/升级矩阵、真实签名Release、正式信任根与证书、真实账号同根升级、Windows10/干净机器、触控/DPI/无障碍、旧协议竞争和故障注入未跑子项。最终报告逐项列真实覆盖；不以文档或源码存在代替实测。

## 可重复本地演练

先按上文 `keygen-test` 生成临时测试密钥，再运行：

```powershell
./scripts/Build-T05LocalDrill.ps1 -RunId <全新ASCII标识> -PublicTrustFile <test-public.json> -PrivateKeyFile <临时test-private.pk8> -RunNativeDrill
```

该脚本编译有真实更新协议的隔离 A/B/Bad 构建（T05 使用 0.5.0/0.5.1/0.5.2，仅测试条件编译的 Bad 核心初始化失败；这些数值不是实际发行版本），各有不同 BuildId 和实际程序集。隔离 FileStream feed 与可信测试根编译进显著标识的专用变体，不能在普通版通过设置、环境变量或命令行打开。公开构建输入和源快照一并记录；私钥留在调用者受控临时目录，仅签名工具读取。所有材料完成后删除临时私钥，未来演练重新生成，不能将其当生产密钥。

A 经完整模块回归后生成便携 ZIP；专项脚本将冻结发行目录复制到全新的中文测试目录，通过原生界面创建真实游戏存档及桌面偏好，验证确认前不下载、取消确认不生成计划、确认后下载、“稍后重启”不安装、运行中拒绝明确重启、用户保存并结束且再次确认重启后升级、核心健康确认、原数据与原快捷方式重开；另用新的 A 副本验证失败回退。测试不替换派蒙原包、不覆盖历史报告。该 feed 的 Release 列表是本地合成输入，不宣称公网 GitHub 下载通过。日志中的模块测试、协调器测试和原生产品测试分开解释。

若第一次演练发现缺陷，保留失败目录与报告，修复后使用新的 RunId 或 BuildId 重建。仅重跑自动化脚本时也要新的 ReportDirectory。正式默认构建必须不传任何 TestUpdate 属性，重新 Rebuild，重新验证同构建便携目录与 ZIP（安装包依派蒙指示暂缓），不能直接把演练 ZIP 改名交付。
