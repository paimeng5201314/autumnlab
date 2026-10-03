# T04 下载与 GitHub 网络接口

Lab Chronicles AutumnOS，制作人：派蒙。适用当前 T04 开发增量，宿主 C# 合同命名空间 `AutumnOS.Store`。这些是系统应用内部接口，不属于第三方 JavaScript SDK。没有新 NuGet 依赖。此模块不实现 T05 主程序更新、安装签名或第三方运行安全豁免。

## 网络配置及范围

`StoreNetworkSettingsService(dataRoot)` 读取/原子保存 `AutumnOS_Data/Config/store-network.json`，与账号凭据、桌面设置分开；构造入参必须是宿主解析的绝对数据根。不存在配置时默认使用 Windows 系统网络代理、API/附件 URL 加速均关闭。现有损坏配置报告错误，不静默覆盖。用户配置代理只影响该 `GitHubTransport`，不会修改全局 Windows 设置，也不会改变 Logto、OIDC 或游戏 WebView 的网络服务。

| C# 字段 | 默认值 | 约束与效果 |
|---|---|---|
| UseSystemProxy | true | 使用现有 Windows 代理；false 为本 transport 直连，不写系统配置 |
| ApiUrlTemplate | 空 | 默认公开元数据直连；独立选择后才改写 API 请求 |
| AssetUrlTemplate | 空 | 单独改写 Release 附件请求 |
| AllowDirectFallback | true | 加速连接异常、超时、408/5xx 后，对原 GitHub 公共 URL 最多直连一次 |
| MaximumConcurrentDownloads | 2 | 1–4；新启动任务遵循最新值，已进行任务不会因降低上限被强杀 |
| BytesPerSecond | 0 | 0 不限速；最大 1 GiB/s；所有任务共享字节调度，设置变更从后续块生效 |

模板例如 `https://用户选择的加速主机.example/fetch?url={url}`；示例域名仅展示语法，不内置、不自动启用。`{url}` 必须恰好出现一次；嵌套地址使用 `Uri.EscapeDataString` 编码一次，不能把未编码的查询串拼进另一个查询。必须 HTTPS 默认端口、无用户信息/片段、DNS 主机、无 token/secret/key/password 查询参数；拒绝回环 IP、`.localhost`、`.local` 和账号主机。保存前调用 `Validate`，失败保留旧配置。

生产请求原始目标只允许 GitHub API 或 `github.com/{owner}/{repo}/releases/download/{tag}/{asset}`。不接受 file、脚本、HTTP、任意第三方或私网源。GitHub 返回的附件跳转只能到代码定义的 GitHub/CDN 主机，最多 5 次；加速目标只允许同一用户配置主机或允许的 GitHub/CDN。加速主机 DNS 结果禁止回环/私网/link-local；直接连接回调再次校验实际解析地址，连接已检查的 IP，避免检查后再次解析连接。Windows 用户自己配置的系统代理可能在本机/内网，此地址属于显式系统代理设置；加速源仍检查，TLS 始终由系统验证。通过受信任系统代理时，其远端 DNS 行为不由客户端控制。

全部请求为无 Cookie、无默认凭据 GET。调用方只可提供 Accept、If-None-Match、If-Modified-Since、Range、If-Range、X-GitHub-Api-Version。Authorization、Cookie、Host、Proxy-Authorization 等在任何网络发送前拒绝，跨主机也不会携带身份材料。返回日志/界面只用安全错误码、HTTP 状态及 direct/accelerated/direct-fallback 路径，不记录 CDN 签名查询串或原始异常正文。

`IGitHubTransport.SendAsync(Uri, GitHubRequestKind, headers?, CancellationToken)` 返回响应头后即可流式读内容，调用方必须 dispose response。固定 User-Agent、GitHub API 版本 2022-11-28；不发压缩协商以保持附件字节长度可验证。生产自动跳转关闭，由上述规则手工处理。30 秒请求超时，单次连接 15 秒，调用者可随时取消。

`TestConnectionAsync` 只检查公开 `https://api.github.com/rate_limit`，结果 `GitHubConnectionTest(Succeeded, Code, Route, StatusCode)`；API 探测通过不能冒充附件加速通过。`TestAssetConnectionAsync(已选择的公开Release附件URI, token)` 发送 Range `bytes=0-0`，读取响应头后即释放连接；没有实际附件时该专项未执行。连接检查不是哈希检查或下载成功。失败/被取消均返回明确状态，不关闭 TLS。

## 下载状态和合同

构造 `DownloadService(dataRoot, IGitHubTransport, Func<StoreNetworkSettings>)`；HTTP 替身只能由测试代码显式注入，没有生产配置的测试源开关。所有任务位于 `Downloads/store-v1`；不碰 Apps、Saves、账号凭据或其它开发包的数据目录。

```csharp
var network = new StoreNetworkSettingsService(dataRoot);
using var transport = new GitHubTransport(network.Load);
await using var downloads = new DownloadService(dataRoot, transport, network.Load);
// selected 包含真实三层清单解析后的身份与不可省略的仓库/Release/Asset ID。
var task = downloads.Enqueue(new DownloadRequest(selected.AppId, selected.Version,
    selected.AssetUrl, selected.Bytes, selected.Sha256,
    selected.RepositoryId, selected.ReleaseId, selected.AssetId));
// UI 从 Snapshot 读数据；Changed 仅通知刷新，通过 DispatcherQueue 异步返回 UI 线程。
```

`DownloadRequest` 必须绑定原始固定 Release URL、repositoryId/releaseId/assetId（正整数）、应用 ID/版本、SHA-256 和精确大小。不能以一次性 CDN 地址代替来源。SHA-256 必须 64 位十六进制；只接受 `.autumn`；大小 1–20 MiB，继承当前包安装器上限。未知大小/来源没有可安装任务，不伪造进度或 ETA。

`Enqueue` 对同一完整 request 去重；重复点击返回原 ID。最多 32 个非终态任务、200 条总历史；满时淘汰最旧已完成/取消/失败的本模块历史和缓存，不删除安装或存档。缓存理论上限为 200 × 20 MiB，实际受任务历史/磁盘空间共同约束。下载前至少预留剩余字节与 1 MiB 余量，实际写入失败仍报告 IO 错误。人工重试用 `Retry(id)`，不是无限创建新任务。

顺序：Queued → Downloading → Verifying → AwaitingInstall → Installing → Completed。下载完成停在 AwaitingInstall，只有安装协调器注册提交成功才调用 `MarkInstalled`。暂停为 Paused；取消为 Cancelled；错误为 Failed。`MarkInstallFailed(id, 安全错误码)` 回到 AwaitingInstall 并保留已校验包以便重试；Completed 允许用户明确的修复操作调用 MarkInstalling，但安装器仍必须重新校验包与来源。此状态 API 本身不授予执行或权限。

`Snapshot()` 返回最新不可变列表：Id、Request、State、BytesReceived、TotalBytes、BytesPerSecond、EstimatedRemaining、LocalPath、ErrorCode、Attempts、CreatedUtc、UpdatedUtc、Route。速度来自本轮已写字节/实际时间；未开始、暂停/验证时为 0，ETA 为 null。运行期间按真实块更新，通知/历史检查点约 250 毫秒；客户端不可拿次数推算进度。`Changed` 在后台线程触发，订阅者必须轻量、只 enqueue UI 更新，不能同步等待 UI；宿主退出时解除自己的 UI 订阅。

Pause/Resume/Cancel/Retry 都按 ID 返回状态转换是否接受。取消停止读取，关闭流后只删本任务 `.part`，保留历史和错误记录；不删除应用或用户数据。Pause 保留部分文件。`DisposeAsync` 停止传输、把活跃任务写为 Paused 并等待自有 worker 清理；`Dispose` 仅请求取消并持久化。重新构造读取历史后显示暂停任务，由用户继续，不自动重新安装。崩溃时 Installing 记录恢复为 AwaitingInstall/INSTALL_RECONCILIATION_REQUIRED，安装协调器需与注册记录对账，不能凭此重复提交。

## 断点、校验、错误

任务历史持久化稳定 URL、三个 GitHub ID、长度/hash、强 ETag 与已写字节。恢复偏移从实际 `.part` 长度读取；无强 ETag 或弱 ETag 时清理自己的 partial 后安全重下。Range 请求同时发送 If-Range。只有 HTTP206、同一强 ETag、Content-Range 起止与总长完全匹配才能 append。收到 HTTP200，截断原 partial 后读取完整资源；资源变化、416、错误 Range 会最多尝试一次完整重下，不能拼接不同内容。

网络断开/408/429/5xx 最多 3 次尝试并退避；尊重 Retry-After，超过 30 秒则保留明确失败供以后用户重试，不提前自动重试，`RetryAfterUtc` 之前人工 Retry 也会拒绝并保留错误。读取每块 30 秒超时，所有等待支持取消。HTML MIME、HTML 文件开头、长度超出/不足、Content-Encoding、错误 hash 都不成为可安装包。全量 SHA-256 验证后，同目录原子 rename `.part` → `.autumn`，只有此后返回 LocalPath。重启对待安装缓存重新计算 hash，损坏记录为 DOWNLOAD_CACHE_INVALID。文件与祖先目录的重解析点均拒绝。

常见安全错误：DOWNLOAD_REQUEST_INVALID、DOWNLOAD_QUEUE_FULL、DOWNLOAD_DISK_FULL、DOWNLOAD_NETWORK_FAILED、DOWNLOAD_TIMEOUT、DOWNLOAD_HTTP_403/404/429/5xx、DOWNLOAD_HTML_RESPONSE、DOWNLOAD_ENCODING_NOT_ALLOWED、DOWNLOAD_LENGTH_MISMATCH、DOWNLOAD_RANGE_INVALID、DOWNLOAD_HASH_MISMATCH、DOWNLOAD_CACHE_INVALID、DOWNLOAD_IO_ERROR、DOWNLOAD_ACCESS_DENIED。网络与下载错误不等于安装失败，更不影响旧版本资源与存档。

正例：同一 SHA/ID/ETag 的 206 恢复，最后完整 hash 一致才进入 AwaitingInstall。反例：加速器返回 HTML200、改变 ETag 后仍返回206、同版本附件字节被换、诱导 file URL 或 Authorization header，均不能视为成功。下载 hash 只证明与取得的发布清单一致，不证明开发者已被审核。

## 测试与交接

`tests/AutumnOS.Tests/StoreDownloadTests.cs` 使用明确的受控 HttpMessageHandler/IGitHubTransport 与真实临时文件字节：模板保存/拒绝、DNS地址分类、凭据/不安全跳转拒绝、直连回退、原子落盘、去重、暂停/重启、强/弱/变更 ETag、200/206/错误 Range、磁盘不足注入、HTML/长度/hash、有限重试、Retry-After、并发/限速、取消、缓存篡改。测试工作根为随机隔离目录，清理前核验仍在自有测试前缀；不清理用户数据。这些是受控响应与真实文件测试，不是公开 GitHub Release 或真实 TLS 故障实验。实际执行命令/结果由本轮构建报告记录，源码存在本身不代表已通过。

公开 GitHub 发现、实际附件、最终 EXE UI、TLS 环境以及隔离端到端分别验收。T05 将来可以复用受限下载能力，但主程序源、签名、维护锁、健康检查和回滚仍需自己的协议与实现，不能调用 MarkInstalled 伪造系统更新完成。

实现核验资料：[GitHub Release Asset API](https://docs.github.com/en/rest/releases/assets?apiVersion=2022-11-28)、[Microsoft HttpClient 自动重定向说明](https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.allowautoredirect)。
