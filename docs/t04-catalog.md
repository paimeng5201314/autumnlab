# Autumn Store：发现、清单与本地发布材料

制作人：派蒙。适用于 T04 开发增量，清单 schemaVersion=1。T03 收尾由用户暂缓，旧待验收项目保留。代码入口为 AutumnOS.Store.GitHubStoreCatalog、ReleaseMetadata；公共发布校验核心为 AutumnOS.Packages.ReleaseManifestValidator。不存在认可名单或审核服务。

## 发现和缓存

生产查询固定 topic:autumnos-app is:public，关键词仅允许字母、数字、空格、点、下划线和连字符，最多100字符，不能注入搜索运算符。公开 REST 搜索按更新时间每页30条，最多34页且不超过GitHub搜索1,000条范围。返回按 repositoryId 去重的当前页、total_count、incomplete_results、缓存时间和警告。UI的本地筛选仅代表当前已加载结果；取消旧查询和防抖由Shell协调。

类别来自精确Topics：autumn-app-pm为“受认可分类（仓库自标，未核验）”；autumn-app-sq为“社区分类（仓库自标）”；都没有为未分类，都有为冲突。根清单category不同只产生警告，不覆盖Topics，不授予权限。offlineCapable是开发者声明，不是独立认证。无开发者图标时使用原生中性图标。

详情重新读取owner/name并核对immutable repositoryId，同名重建报STORE_SOURCE_CHANGED。根autumn.store.json通过Contents API读取实际默认分支，不假设main；记录Git blob SHA、ETag与时间并交叉校验内容哈希。blob SHA不是commit SHA或开发者签名。仓库重命名/转移需从新结果重新选择；更新来源仍绑定repositoryId。

每次详情只读一页30个Release，历史页显式加载；不用latest、正文链接或源码ZIP。每版须有唯一autumn.release.json及其声明的唯一.autumn附件，URL绑定所选仓库、Release Tag和Asset。文本有长度上限，纯文本渲染，不执行HTML。

同一catalog请求串行且至少间隔120ms。ETag或Last-Modified条件请求；最多64个精确自有缓存文件、32MiB、七天有效期，包含公开URL/时间/摘要/字节，不含账号令牌。304保留实际原文读取时间。缓存写入失败不阻断成功网络响应，也不伪称已经持久化。路径含reparse point拒绝。

403/429记录Retry-After或X-RateLimit-Reset，等待期间不再发请求；有缓存显示STORE_RATE_LIMITED_CACHE，无缓存报STORE_RATE_LIMITED及RetryAfter。没有无限循环重试。5xx、超时、网络失败可显示带STORE_OFFLINE_CACHE的已校验缓存；首次离线报错，不能伪装空结果。单请求20秒，调用方取消传递到底层流且不返回缓存成功。

## 模块接口

下列为宿主C#接口，不开放给第三方SDK；公开浏览不要求Logto或PAT。构造GitHubStoreCatalog(cacheDirectory,hostVersion,sdkVersion,IGitHubTransport)。生产注入GitHubTransport；测试注入与生产缓存/下载根分离。

| 方法 | 输入 → 输出 | 权限、生命周期与边界 |
|---|---|---|
| SearchAsync(search,page,ct) | 关键词与页码 → CatalogPage | 公开只读；取消、限流和离线明确；重复查询条件请求 |
| GetDetailsAsync(repository,ct) | 宿主选中的来源 → CatalogDetails | 核对ID，返回根资料、首个版本页、警告、blob SHA/ETag |
| GetVersionsAsync(repository,store,page,ct) | 来源/根清单/页码 → CatalogVersionPage | 每页30；当前页SemVer排序，调用方合并历史页去重 |
| GetScreenshotAsync(repository,imageUrl,ct) | 同仓库图片 → CatalogImage(Bytes,MediaType) | 按需读取；拒任意外链和执行型内容；失败不阻断详情 |
| ParseStore / ParseRelease | 最多64KiB JSON → 固定模型 | schema 1；拒未知/重复字段；缺少必填不猜测 |
| GenerateRelease(packagePath,minHost,minSdk,ct) | 真实.autumn → ReleaseManifest | 先PackageInstaller.Inspect，从真实字节生成身份、权限、入口、大小、SHA与格式 |
| GenerateStore(manifest,developerName,description,category,offlineCapable) | 已验包与展示字段 → StoreManifest | 生成后同一ParseStore复验 |
| MatchPackage(release,inspection,path) | Release与实际包 → 成功或固定错误 | 调共享ReleaseManifestValidator.ValidatePackage逐项核对 |

截图仅接受同仓库github.com/owner/repo/blob/ref/path或raw.githubusercontent.com/owner/repo/ref/path，通过Contents API读PNG/JPEG，最多2MiB，单边4096且总像素12Mi。拒SVG、HTML、凭据/查询串/片段、跨仓库及越界路径；最多6张，逐张加载。斜杠命名ref暂不支持，可用commit SHA URL。

## 三层清单

机器合同在sdk/store.schema.json、sdk/release.schema.json、sdk/manifest.schema.json。大小写精确，重复字段在schema验证之前拒绝。

autumn.store.json必填schemaVersion、appId、name、description、category、developer、screenshots、offlineCapable。developer必填name，可选url。无截图写空数组。category为pm/sq/unclassified；添加approved等未知字段会失败。appId须与发布和安装包一致。HTTPS网址不自动执行。

autumn.release.json保留T03既有13字段：schemaVersion、appId、version、channel、runtime、minHostVersion、minSdkVersion、entry、asset、bytes、sha256、permissions、saveFormatVersion。CLI、开发者工具和商店共用Packages.ReleaseManifestValidator，避免宽松的第二套协议。

- version、minHostVersion、minSdkVersion采用规范SemVer 2.0；按数字/预发布标识比较，build metadata不影响优先级，不按文件名、标题或时间猜版本。
- channel为stable/preview，preview当且仅当version包含预发布标识；GitHub prerelease须一致，Tag为version或v+version。
- 当前仅runtime=web兼容；宿主仍为win-x64，Web/WASM包不等于可导入原生EXE。未知运行时显示不兼容。
- asset只能是单个.autumn文件名；bytes为1–20MiB；sha256为64位小写十六进制。GitHub digest存在时额外交叉核验，缺失仍检查平台清单。
- permissions只允许当前协议支持的权限，必须与包内一致；安装不代表已授权。
- saveFormatVersion与包内当前格式一致。manifest新增可选saveFormatVersion（旧包默认1）、minReadableSaveFormatVersion/maxReadableSaveFormatVersion（默认当前），安装器检查有效区间和降级可读性。

反例：未知schema、重复appId、同版本不同摘要、两个同名附件、source.zip、setup.exe、跨仓库URL、预览错配或权限不一致，均不能显示安装成功。摘要一致不证明程序无害、开发者可信或存在签名。

主要安全错误：STORE_QUERY_INVALID、STORE_OFFLINE、STORE_TIMEOUT、STORE_RATE_LIMITED、STORE_NOT_FOUND、STORE_SERVICE_UNAVAILABLE、STORE_RESPONSE_TOO_LARGE、STORE_METADATA_MISSING、STORE_METADATA_INVALID、STORE_SCHEMA_UNSUPPORTED、STORE_METADATA_DUPLICATE_FIELD、STORE_NO_RELEASES、STORE_RELEASE_METADATA_MISSING、STORE_PACKAGE_ASSET_MISSING、STORE_ASSET_AMBIGUOUS、STORE_ASSET_SOURCE_INVALID、STORE_APP_ID_MISMATCH、STORE_RELEASE_VERSION_MISMATCH、STORE_METADATA_DIGEST_MISMATCH、STORE_RUNTIME_UNSUPPORTED、STORE_HOST_TOO_OLD、STORE_SDK_TOO_OLD、STORE_PACKAGE_METADATA_MISMATCH、STORE_SOURCE_CHANGED。返回固定错误码，不回显响应正文、令牌、签名URL或私密路径。

## 独立样例和真实回环测试源

运行scripts/New-StoreFixturePackages.ps1生成artifacts/store-fixtures下真实五个.autumn、各自Release JSON、根Store JSON和fixture-index.json。输入为独立samples/store-probe与当前SDK，不改元素配对。ZIP顺序/时间戳固定；生成字节改变时历史文件保留.previous-*。开发包仅复制当前*.autumn/*.json，不包含启用测试源的配置。

| 版本 | 用途 | Release ID / Asset ID |
|---|---|---|
| 1.0.0 | 首次安装、未保存文字和存档 | 40001 / 400011 |
| 1.1.0 | 同格式升级和修复 | 40002 / 400021 |
| 1.2.0-preview.1 | 应用预览过滤 | 40003 / 400031 |
| 2.0.0 | 存档格式2，不兼容降级 | 40004 / 400041 |
| 1.9.0 | 故意错误SHA，必须下载失败 | 40005 / 400051 |

StoreTestSource仅由开发模式显式入口在游客状态构造；repositoryId=90004001，appId=cn.labchronicles.storeprobe。UI必须醒目标记“本地集成测试数据，不是GitHub实时结果”。构造绑定127.0.0.1随机端口，通过真实HttpClient/HTTP将固定合成GitHub URL映射到回环，不改变生产域名/TLS策略。监听只读固定内存资源，随机能力路径、精确Host、GET、8KiB请求头、四连接上限；支持ETag/Range/If-Range。关闭开发模式应Dispose、取消任务并关闭监听。

运行gate只认可编译进程序集的fixture-index及真实包SHA。外部索引/包字节改变会拒绝，不把开发目录当任意社区入口。源、缓存和队列与普通商店分开，普通启动默认公开GitHub。T01任意社区执行缺项继续限制未知包，PM无例外。

样例真实调用permissions.query/request和saves.write/read，仅申请saves。拒绝正常降级。写入测试文字、返回桌面、双击后台图标继续时输入保留；结束后新实例主动读档可取回已保存内容。SDK没有公开实例ID，页面只显示真实生命周期，不伪造标识；同实例证据由宿主记录检查。样例不使用localStorage或外部网络，不绑定真实账号。

StoreCatalogTests的模拟传输、loopback集成、真实GitHub搜索、真实公开Release安装和最终WinUI验收分别记证据。公共搜索无结果就显示空结果；没有合规Release专项保持blocked/not_run，不替用户发布远端资源。

## 发布与交接

开发者在本地打包.autumn，调用GenerateStore/GenerateRelease并用共享校验器检查三个清单。自行将根Store清单放默认分支，添加autumnos-app及pm/sq之一，按版本Tag发布.autumn与autumn.release.json。此为第三方发布说明，本地工具不自动创建仓库、上传或发布Release。

取消加载不安装；下载完成、校验成功、安装提交、注册通知是不同事件。固定版本/每应用预览设置由应用注册服务保存，具体事务/修复/降级/保留存档见安装文档。公开目录缓存不含账号数据，账号切换不将游客存档绑定给新账号。T05可复用网络服务，本轮未实施主程序plus/meta更新。

官方依据：[GitHub Search](https://docs.github.com/en/rest/search/search)、[Contents](https://docs.github.com/en/rest/repos/contents)、[Releases](https://docs.github.com/en/rest/releases/releases)、[Release assets](https://docs.github.com/en/rest/releases/assets)、[REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)、[SemVer 2.0](https://semver.org/)。文档不是通过证据，实际命令、构建身份、结果和截图见AUTUMNOS_PROGRESS.md和本次artifacts报告。

历史 Release 分页独立限制为每页最多 30 条、最多 34 页（1,020 条），以避免单次浏览无限占用公开 API。达到上限后应使用仓库原始 Release 页面检查更早版本；当前界面不声称已经列出所有历史。GitHub 搜索本身的 1,000 条上限属于另一限制。Release entry 不允许连续的 `..` 字符，Schema 与共用 C# 校验器一致；普通包内路径仍按 PackageInstaller 的路径安全规则校验。
