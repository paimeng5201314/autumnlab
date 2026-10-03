# T05 更新来源、字节签名与负载合同

Lab Chronicles AutumnOS；制作人：派蒙。实现位于 `src/AutumnOS.Update/`。本合同是本地开发实现，不代表已有正式公钥、公开签名 Release、Windows Authenticode 证书或 TUF 合规认证。

## 候选与来源

生产仓库固定 `paimeng5201314/autumnlab`。`UpdateService.CheckAsync` 使用 Release 列表，每页 100 条、最多 20 页；未发现分页结束则 `UPDATE_RELEASE_LIST_INCOMPLETE`，不提供不完整列表中的安装候选。关闭主程序预览只读 `plus-v<SemVer>`，开启只读 `meta-v<SemVer>`。逐项核对草稿、Tag、SemVer 预发布部分、GitHub prerelease、签名清单渠道/版本/产品/仓库、Release ID、Payload Asset ID、大小、win-x64 和最低更新器。排序由既有 `AutumnOS.Store.SemanticVersion` 完成；不使用发布时间或 latest，不自动降级。每个版本的标签和附件应保持不可变。

缺生产信任根时仍读取真实 Release 列表，展示网络状态和 `UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED`，不会产生可下载提交的未验签候选。2026-10-02 的真实只读探测 HTTP 200、第一页 0 条发布；见 `artifacts/reports/t05-production-release-probe.json`，该证据不等于真实 GitHub 下载或更新验收。

T04 `CatalogResponseCache` 与 `GitHubTransport` 直接复用：ETag、有限缓存、限流、退避、受限 GitHub 加速、直连回退。缓存只保存公共字节；缓存的清单仍逐次验签、检查有效期。T04 `DownloadService` 新增显式 `hostUpdate:true`：仅本产品/固定仓库 `.zip`，最大 4 GiB，独立 `Downloads/host-update-v1`；默认社区配置仍为 `.autumn`/20 MiB，包安装器的原生代码禁令不变。

## autumn.update.json 与 autumn.update.sig

Schema 分别位于 `schemas/autumn.update.schema.json`、`schemas/autumn.update.sig.schema.json`，信任根为 `schemas/autumn.update.trust.schema.json`。实际严格解析还拒绝重复字段、未知字段、无效 UTF-8、BOM、过深 JSON 与超限数据。清单最大 4 MiB，签名 envelope 最大 16 KiB。生成器输出 UTF-8 无 BOM；JSON 换行/空格均属于签名字节，禁止读入再序列化后验签。

`RSA-PSS-SHA256` 使用 .NET 10 `RSA.SignData/VerifyData`、SHA-256、PSS，RSA 至少 3072 bit。`signature` 是完整原始清单字节签名的标准 Base64；envelope 的 schemaVersion、algorithm、keyId、trustRootVersion 与清单绑定。只拿 SHA-256、TLS、GitHub 资产 digest 或下载方附送公钥均不足以验证发布身份。来源资料：[Microsoft RSA.SignData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.signdata?view=net-10.0)、[GitHub Releases API](https://docs.github.com/en/rest/releases/releases#list-releases)。

清单还绑定不可变构建 ID、全局递增 sequence、UTC issuedAtUtc/expiresAtUtc、payload 名称/字节数/SHA-256、完整程序文件表、显式删除表以及数据兼容要求。有效期最长 90 天；提前超过 5 分钟、过期、时钟早于 2024 年或较上次可信检查倒退超过 5 分钟均停止新更新。已安装程序启动不受清单过期影响。

`Updates/state.json` 原子保存最高接受 sequence、对应原始清单 digest、预览开关与失败 buildId。`UpdateService` 还读取独立更新器 `.autumnos-update/security.json` 的不可回退水位和失败候选；同序号不同 digest 拒绝，失败 buildId 不会再次被选中。签名验证与提交前重检允许同一 sequence/同一 digest，便于同一次事务重新检查。正常更新不降级；last-known-good 恢复由独立事务授权，不降低水位、不清除失败记录。

## 根接入、轮换与秘密

普通构建只允许 MSBuild `ProductionUpdateTrustFile` 编译嵌入 purpose=`production` 的公钥 JSON；未设置时根为空并关闭自动安装。测试构建显式设置 `TestUpdateTrustFile`，定义 `AUTUMNOS_UPDATE_TEST_BUILD`，只允许 purpose=`local-test`；两种属性同设构建报错。`UpdateTrustStore.FromEmbedded()` 不读取普通设置、CLI 参数、环境变量、网页或下载附件的替代根。源码 API 的注入点供内部工具/单元测试；生产 Shell/Updater 使用编译根。

根支持 version、多个 keyId 和 revoked；未知、撤销、旧版本根均拒绝。本轮**没有实现通过网络自动采纳新根**。可审查的轮换流程是：离线生成新钥；用已受信旧钥签名包含新旧公钥的过渡客户端（根 version 暂时不变、不同 keyId）；验证所有实际渠道的独立稳定更新器也能验证新钥；然后才以新钥签后续版本。稳定更新器不在普通 payload 中替换，因此未预置新钥的安装必须经过独立受控安装器迁移，不能宣称一次普通自更新完成该轮换。撤销旧根/提高 root version 同样需要保留不可回退状态与兼容引导验证；缺这些证据保持 blocked。禁止删除旧钥条目或下调根版本来“恢复”验签。动态阈值根更新可另行实现；参考威胁模型不代表已实现 [TUF 规范](https://theupdateframework.github.io/specification/latest/)。

正式私钥在离线受控签名环境保存，不放入源码、客户端、命令行字面值、日志、源快照和分发包。不向派蒙索要私钥、密码或 token。工具以私钥文件路径读取并仅输出签名/公钥，不打印私钥。Windows Authenticode 与更新签名独立；没有证书的本地 EXE 如实标为未签名。

## 文件与数据边界

`UpdatePayload.CreateFileList(root)` 对实际文件字节计算 SHA-256，排除稳定 `AutumnOS.exe`/`AutumnOS.Updater.exe`、安装器/卸载入口（含 `AutumnOS.Uninstall.exe`）、AutumnOS_Data、`.autumnos*`、安装收据、更新元数据、ZIP，以及任意层级的 `.pem/.key/.pfx/.pk8/.p12/.dpapi/.snk` 文件。只排除，不删除。安装收据 `autumn.install.json` 由打包/更新器另行生成，不参与自身哈希。payload 必须包含 `AutumnOS.Client.exe`。

payload 是仅含文件条目的 ZIP；文件表大小写唯一，禁止文件/目录父子冲突。解包拒绝绝对路径、反斜线、`..`、ADS、控制字符、Windows 设备名、尾随点/空格、链接、重解析点、未知或重复条目、内容 hash/大小不符。最多 20000 文件、12 GiB 展开、单项压缩比不超过 1000。SHA-256 检查和 ZIP 读取保持同一个拒绝写入的 FileStream；解包目录须不存在或为空，以 CreateNew 创建文件。失败暂存保留给诊断，不被执行。更新器在事务使用前重验暂存文件并只更改安装收据拥有的文件。

本轮仅支持 `data.schemaVersion=1, minimumReadableVersion=1, rollbackCompatible=true` 的兼容数据更新；不可逆迁移明确 `UPDATE_MIGRATION_UNSUPPORTED`。不执行远端脚本，不覆盖账号凭据、游戏存档或其他未知文件。

## 宿主 API 与演练源

`UpdateService.CreateDefault(dataRoot,currentVersion,currentBuildId,networkSettings,enterCriticalOperation)`；最后参数可注入统一维护协调的关键写入租约。`Changed` 为 `EventHandler`，可能来自工作线程；UI 应派发读取不可变 `Snapshot`。`CheckAsync(preview,token)`、`DownloadAndStageAsync(token)` 分别执行真实检查和下载/验签；`SetPreviewEnabled` 持久化并废弃旧候选/暂存资格。`ValidateStagedForCommit()` 返回 `StagedUpdate` 的清单、签名和负载路径，同时再次验证渠道、版本、时钟/水位、失败候选和暂存元数据；它不取得维护租约，调用方须先取得共享维护资格，独立更新器仍须重新验证。`RecordFailedCandidate(buildId)` 用于恢复 UI 同步已知失败状态。

取消不假装已回滚；下载暂停到同资源身份的缓存，重新进入仍检查 ETag/Range 与完整 hash。安全错误以固定码展示，不记录用户内容。检查/下载不改变已安装版本；只有独立更新器健康协议能提交成功。

仅测试构建可编译嵌入 `TestUpdateFeedFile`（一行绝对目录路径）。该目录 `releases.json` 使用 GitHub Release 数组结构；附件位于 `<tag>/<assetName>`。`UpdateTestFeed` 将固定官方 URL 映射为此隔离目录的真实 FileStream；不是公开 GitHub，也不是 HTTP 故障实测。没有运行参数可以把普通构建改为测试源。目录可更新候选用于演练，但每个新附件仍必须由编译测试根签名。测试公钥可以随演练包；测试私钥不能随包。

C# 工具 API：`UpdateManifestCodec.Serialize/Parse`、`UpdateSignature.Create/Verify`、`UpdatePayload.CreateFileList/VerifyAsync/ExtractVerifiedAsync`。真实 CLI 命令与演练打包命令由 `tools/AutumnOS.Update.Cli` 及 T05 交付文档给出；本文件不捏造未执行参数。核心测试为 `UpdateCoreTests.Cases()`，涵盖受控 Release 分页/筛选、原始字节签名、未知/撤销根、时效/重放、路径/ZIP、真实下载缓存/暂存和渠道失效；真实 GUI/交接/回退属于另外证据。

`AutumnOS.Update.Cli inventory --source <发行目录> --output <新文件.json>` 使用同一个 `CreateFileList` 输出 `UpdateFile[]`，供便携包、安装收据和更新 payload 共用同一受管理文件边界；`--output` 采用 CreateNew，不覆盖现存文件。该命令已用真实 CLI 和不含秘密的合成文件测试私有扩展排除、卸载器排除、输出拒绝覆盖。当前核心专项共 23 项，真实结果在 `artifacts/update-core-harness/reports/t05-update-core-tests.json`；最终构建仍须执行统一回归。
