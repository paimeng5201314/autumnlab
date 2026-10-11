# T05 更新来源、字节签名与负载合同

Lab Chronicles AutumnOS；制作人：派蒙。实现位于 `src/AutumnOS.Update/`。本合同是本地开发实现，不代表已有正式公钥、公开签名 Release、Windows Authenticode 证书或 TUF 合规认证。

## 候选与来源

生产仓库固定 `paimeng5201314/autumnlab`。关闭“接受预览版邀请”时，自动检查仅以 `GET /repos/{owner}/{repo}/releases/latest` 作为稳定候选；开启时，以 Latest 稳定版和分页查询的已发布预览版共同选出严格高于当前版本的最高可信兼容版本。Release 的 `draft` 必须为 false，`prerelease` 与清单 `channel`（plus/meta）及 SemVer 预发布部分必须一致。历史选择使用 Releases 列表，每页 100 条，按 Release ID 去重，可继续加载和取消；自动预览检查有 100 页上限，未读完时报告信息不完整。Latest 不存在、网络失败、签名或资产不完整都分别报告，不能写成“最新版”。

版本比较复用 `AutumnOS.Store.SemanticVersion`，主程序入口仅规范化允许的 `v` 前缀；预览数字段按 SemVer 比较，`+build` 不改变优先级。GitHub 名称、tag、可比较版本和负载身份分别保存。清单可选 `releaseTag` 明确映射精确 tag；没有它时仅接受与清单版本一致的 SemVer tag（允许 v 前缀）或原 `plus-v`/`meta-v` 形式。日期/历史展示名不用于推断高低。未安装的预览目标在关闭开关后立即失去授权；已安装预览版不自动降级。

2026-10-10 只读核对：Latest 是 tag `meta` 的稳定 Release，另有预览 tag `meta0.0.2-20261007`。两者都缺签名清单、分离签名和可信 ZIP；源码也未接入生产根。它们可以展示，但不能作为已验证目标安装。详见 [发布工程](t05-release-engineering.md)。生产根缺失时显示 `UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED`，游客仍可读取公开发行记录，不需要 GitHub PAT。

主程序元数据协调器 `UpdateMetadataClient` 复用既有 `IGitHubTransport`，与商店策略隔离。并发启动/手动检查合并；单请求有 20 秒超时，服务端 5xx 最多重试一次，使用 ETag/Last-Modified 条件请求，遵守 Retry-After/限流复位时间并持久化暂缓截止。已知限流期间不继续请求。缓存公共字节每次重新验签；只有服务器成功响应（包括有效 304）且所需信息完整才更新成功时间，无网不把缓存日期当作新成功。下载继续复用 `DownloadService(hostUpdate:true)`：固定产品/仓库、ZIP 最大 4 GiB、独立 `Downloads/host-update-v1`；社区应用原有 `.autumn`/20 MiB 边界不变。

## 确认、精确目标和恢复

检查及说明弹窗只获取有限公共元数据。`ConfirmTarget` 才记录精确目标授权：仓库、Release ID、tag、版本、平台、资产 ID、负载摘要和清单摘要。下载和应用前重新读取该 Release 并验证精确身份，不能用 `/latest/download` 替换目标；撤回、草稿、资产替换、预览许可撤销或签名失败时终止。确认取消会清除授权，不安排安装。

“稍后重启”保存已验证暂存状态；普通退出、下次启动、进入空闲都不会自动应用。实际交接必须经用户主动“重启并安装”，取得维护锁，并调用异步 `ValidateStagedForCommitAsync` 完成新鲜元数据及完整负载复验。运行中的游戏、应用、关键写入和登录流程阻止交接；独立更新器继续验签、检查策略和进程身份。健康确认失败才执行受限事务恢复，不能把安装器退出码当作成功。

## autumn.update.json 与 autumn.update.sig

Schema 分别位于 `schemas/autumn.update.schema.json`、`schemas/autumn.update.sig.schema.json`，信任根为 `schemas/autumn.update.trust.schema.json`。实际严格解析还拒绝重复字段、未知字段、无效 UTF-8、BOM、过深 JSON 与超限数据。清单最大 4 MiB，签名 envelope 最大 16 KiB。生成器输出 UTF-8 无 BOM；JSON 换行/空格均属于签名字节，禁止读入再序列化后验签。

`RSA-PSS-SHA256` 使用 .NET 10 `RSA.SignData/VerifyData`、SHA-256、PSS，RSA 至少 3072 bit。`signature` 是完整原始清单字节签名的标准 Base64；envelope 的 schemaVersion、algorithm、keyId、trustRootVersion 与清单绑定。只拿 SHA-256、TLS、GitHub 资产 digest 或下载方附送公钥均不足以验证发布身份。来源资料：[Microsoft RSA.SignData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.signdata?view=net-10.0)、[GitHub Releases API](https://docs.github.com/en/rest/releases/releases#list-releases)。

清单还绑定不可变构建 ID、全局递增 sequence、UTC issuedAtUtc/expiresAtUtc、payload 名称/字节数/SHA-256、完整程序文件表、显式删除表以及数据兼容要求。有效期最长 90 天；提前超过 5 分钟、过期、时钟早于 2024 年或较上次可信检查倒退超过 5 分钟均停止新更新。已安装程序启动不受清单过期影响。

`Updates/state.json` 原子保存最高接受 sequence、对应原始清单 digest、预览开关与失败 buildId。`UpdateService` 还读取独立更新器 `.autumnos-update/security.json` 的不可回退水位和失败候选；同序号不同 digest 拒绝，失败 buildId 不会再次被选中。仅在完成可信下载暂存后推进客户端 sequence 水位，读取候选不会抢先占用序号。签名验证与提交前重检允许同一 sequence/同一 digest，便于同一次事务重新检查。正常更新不降级。手动回滚使用 30 分钟有效、绑定当前源构建和精确目标的 `UpdateRollbackAuthorization`；只有本次已确认旧目标可绕过 sequence 防回放下界，仍检查签名、有效期、最低安全版本和失败构建。水位始终取较高值，授权不能转用于任意旧版。最低协议 `1.1.0` 与 schema 1 兼容条件必须满足；历史旧协议目标禁用并解释。last-known-good 故障恢复由原事务日志授权，也不降低水位、不清除失败记录。

## 根接入、轮换与秘密

普通构建只允许 MSBuild `ProductionUpdateTrustFile` 编译嵌入 purpose=`production` 的公钥 JSON；未设置时根为空并拒绝安装。测试构建显式设置 `TestUpdateTrustFile`，定义 `AUTUMNOS_UPDATE_TEST_BUILD`，只允许 purpose=`local-test`；两种属性同设构建报错。`UpdateTrustStore.FromEmbedded()` 不读取普通设置、CLI 参数、环境变量、网页或下载附件的替代根。源码 API 的注入点供内部工具/单元测试；生产 Shell/Updater 使用编译根。

根支持 version、多个 keyId 和 revoked；未知、撤销、旧版本根均拒绝。本轮**没有实现通过网络自动采纳新根**。可审查的轮换流程是：离线生成新钥；用已受信旧钥签名包含新旧公钥的过渡客户端（根 version 暂时不变、不同 keyId）；验证所有实际渠道的独立稳定更新器也能验证新钥；然后才以新钥签后续版本。稳定更新器不在普通 payload 中替换，因此未预置新钥的安装必须经过独立受控安装器迁移，不能宣称一次普通自更新完成该轮换。撤销旧根/提高 root version 同样需要保留不可回退状态与兼容引导验证；缺这些证据保持 blocked。禁止删除旧钥条目或下调根版本来“恢复”验签。动态阈值根更新可另行实现；参考威胁模型不代表已实现 [TUF 规范](https://theupdateframework.github.io/specification/latest/)。

正式私钥在离线受控签名环境保存，不放入源码、客户端、命令行字面值、日志、源快照和分发包。不向派蒙索要私钥、密码或 token。工具以私钥文件路径读取并仅输出签名/公钥，不打印私钥。Windows Authenticode 与更新签名独立；没有证书的本地 EXE 如实标为未签名。

## 文件与数据边界

`UpdatePayload.CreateFileList(root)` 对实际文件字节计算 SHA-256，排除稳定 `AutumnOS.exe`/`AutumnOS.Updater.exe`、安装器/卸载入口（含 `AutumnOS.Uninstall.exe`）、AutumnOS_Data、`.autumnos*`、安装收据、更新元数据、ZIP，以及任意层级的 `.pem/.key/.pfx/.pk8/.p12/.dpapi/.snk` 文件。只排除，不删除。安装收据 `autumn.install.json` 由打包/更新器另行生成，不参与自身哈希。payload 必须包含 `AutumnOS.Client.exe`。

payload 是仅含文件条目的 ZIP；文件表大小写唯一，禁止文件/目录父子冲突。解包拒绝绝对路径、反斜线、`..`、ADS、控制字符、Windows 设备名、尾随点/空格、链接、重解析点、未知或重复条目、内容 hash/大小不符。最多 20000 文件、12 GiB 展开、单项压缩比不超过 1000。SHA-256 检查和 ZIP 读取保持同一个拒绝写入的 FileStream；解包目录须不存在或为空，以 CreateNew 创建文件。失败暂存保留给诊断，不被执行。更新器在事务使用前重验暂存文件并只更改安装收据拥有的文件。

本轮仅支持 `data.schemaVersion=1, minimumReadableVersion=1, rollbackCompatible=true` 的可读取兼容版本对；不可逆迁移明确拒绝。首次修改程序前，在维护锁内保存程序备份和带构建/版本/schema/UTC 时间/文件摘要的数据快照；快照失败停止替换。数据快照严格限于桌面主题/壁纸和布局的已知 schema，不复制账号凭据、首次引导状态、预览/固定策略、应用或游戏存档。健康失败的自动恢复连同匹配桌面快照恢复；手动同 schema 回滚保留当前兼容桌面设置，不恢复已注销登录、不重新触发引导。当前没有“强制回滚”或跨 schema 的历史快照恢复路径，未来引入迁移必须另加兼容验证和数据影响确认。

手动回滚只可删除当前安装回执中已验证、而目标可信清单不再包含的受管理程序文件；未知文件和用户数据保留。普通升级仍要求显式签名删除表。稳定 Bootstrap/Updater 不在此边界内，协议升级和首次生产根接入需要完整过渡包，见发布工程文档。

## 宿主 API 与演练源

`UpdateService.CreateDefault(dataRoot,currentVersion,currentBuildId,networkSettings,enterCriticalOperation,programDirectory)` 接入原维护协调和真实安装根。`Changed` 可能来自工作线程，UI 派发读取不可变 `Snapshot`。`CheckOnStartupAsync` 每真实进程只发起一次检查；`CheckAsync` 合并并发；`LoadHistoryAsync` 分页；`ConfirmTarget` 绑定目标；`DownloadAndStageAsync` 要求已授权；`ValidateStagedForCommitAsync` 重新读取网络状态并复验负载。同步 `ValidateStagedForCommit` 只供本地恢复验证，不能代替真正交接前的异步重检。

`SetPreviewEnabled`、`RestoreFollowingChannel` 和固定状态使用独立主程序策略存储，保存失败不保留虚假的 UI 状态；固定版本只在独立更新器健康成功提交的记录经 `ReconcileCommittedPolicy` 核对后生效。固定时仍检查，但不产生常规提醒。`DismissCandidate` 仅抑制本会话同一目标提醒。`CancelConsent` 撤销待安装计划，重新选择需再次确认。商店各应用的固定/预览设置保持独立。

取消不假装已回滚；下载暂停到同资源身份的缓存，重新进入仍检查 ETag/Range 与完整 hash。安全错误以固定码展示，不记录用户内容。检查/下载不改变已安装版本；只有独立更新器健康协议能提交成功。

仅测试构建可编译嵌入 `TestUpdateFeedFile`（一行绝对目录路径）。该目录 `releases.json` 使用 GitHub Release 数组结构；某个稳定条目须显式 `fixture_latest=true` 才模拟 `/latest`，不能按数组首项或发布日期猜测。精确 `/releases/{id}` 查询也返回同一条目，供下载前和应用前重检；附件位于 `<tag>/<assetName>`。`UpdateTestFeed` 将固定官方 URL 映射为此隔离目录的真实 FileStream；不是公开 GitHub，也不是 HTTP 故障实测。没有运行参数可以把普通构建改为测试源。目录可更新候选用于演练，但每个新附件仍必须由编译测试根签名。测试公钥可以随演练包；测试私钥不能随包。

C# 工具 API：`UpdateManifestCodec.Serialize/Parse`、`UpdateSignature.Create/Verify`、`UpdatePayload.CreateFileList/VerifyAsync/ExtractVerifiedAsync`。真实 CLI 命令与演练打包命令由 `tools/AutumnOS.Update.Cli` 及 T05 交付文档给出；本文件不捏造未执行参数。核心测试为 `UpdateCoreTests.Cases()`，涵盖受控 Release 分页/筛选、原始字节签名、未知/撤销根、时效/重放、路径/ZIP、真实下载缓存/暂存和渠道失效；真实 GUI/交接/回退属于另外证据。

`AutumnOS.Update.Cli inventory --source <发行目录> --output <新文件.json>` 使用同一个 `CreateFileList` 输出 `UpdateFile[]`，供便携包、安装收据和更新 payload 共用同一受管理文件边界；`--output` 采用 CreateNew，不覆盖现存文件。该命令已用真实 CLI 和不含秘密的合成文件测试私有扩展排除、卸载器排除、输出拒绝覆盖。旧专项报告只对应原构建，不是本轮验收；本轮实际覆盖与未运行的 Windows 原生检查，以更新验收报告和 `AUTUMNOS_PROGRESS.md` 为准。
