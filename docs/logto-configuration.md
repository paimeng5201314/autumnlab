# Logto 公开配置与发现接口（T00）

制作人由工程品牌元数据统一管理。本页记录公开配置和只读发现接口；这些接口本身不创建身份会话。T03 已另接真实浏览器登录、签名验证、受保护凭据与账号切换，见 [原生账号接口](t03-native-account-guide.md)。配置检查不能标记 A030 通过，真实往返与完整安全验收以本地报告为准。

## 单一配置来源

真实公开值只维护在 `autumnos-spec/config/logto.public.json`。Shell 和测试工程通过 MSBuild 链接复制到产物的 `config/logto.public.json`，运行时使用 `AppContext.BaseDirectory` 定位，不根据进程当前工作目录定位。`autumnos-spec/config/logto.registration-status.json` 是人工核对记录；派蒙已确认 Native/public 类型与登录回调，退出回调尚未确认。

配置版本为 1。只允许 `SchemaVersion` 和 `Logto` 根字段，以及当前公开配置定义的 Logto 字段。拒绝未知字段（包括任何 ClientSecret）、重复属性、错误类型、注释、尾随逗号、过深 JSON、超过 64 KiB 和不合法 UTF-8 的文件。配置缺失不会回退到模拟登录。

固定部署合同：Endpoint 是规范的 HTTPS 根源；Authority 精确为 Endpoint 下的一次 `/oidc`；MetadataAddress 精确为 Authority 后的 `/.well-known/openid-configuration`。生产源仅接受 DNS 主机、443 端口，无用户凭据、查询或片段，不接受 loopback。三者须同源。此检查信任随本地构建交付的配置，不是抗本机管理员篡改或替换安装文件的签名校验。

回调严格为 `http://127.0.0.1:17853/callback/` 与 `http://127.0.0.1:17853/logout-callback/`，保留末尾斜杠。T00 仅校验这份合同，不绑定端口。T03 必须先独占绑定再打开系统浏览器；端口占用须明确报错，不杀进程、不偷偷换端口。原生应用通过系统浏览器、回环回调和 PKCE 接入的依据为 [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252.html)。

授权模式只接受 `code`、`UsePkce=true`、`S256` 与客户端认证方法 `none`。默认且仅请求 `openid profile`；只有用户明确选择保持登录时才添加 `offline_access`，它不保证服务端签发刷新令牌。不接受预设邮箱、管理 API 范围、秘密或业务 audience。

## 真实 C# 接口

命名空间为 `AutumnOS.Identity`，目标框架 `net10.0`。以下配置/发现接口不依赖 OIDC 会话状态；同模块的 T03 身份实现另使用固定 Duende/IdentityModel/DPAPI 依赖，详见 dependencies.md。接口供受信任宿主模块调用，不能直接暴露为第三方应用 SDK。

| 接口 | 输入 | 输出和行为 |
|---|---|---|
| `LogtoConfigurationLoader.Load(string configurationPath)` | 本地公开配置文件路径 | `LogtoConfigurationResult`，包括 `IsValid`、验证后才非空的 `Options`、`Issues`、`SafeSummary`；只读、同步、64 KiB 上限 |
| `LogtoConfigurationLoader.ValidateJson(string json)` | 配置 JSON 内容 | 相同结果；纯校验，不读文件、不联网 |
| `LogtoPublicOptions.GetRequestedScopes(bool rememberSignIn)` | 用户是否明确要求保持登录 | 不可变 scope 列表；默认无 `offline_access` |
| `RegistrationStatusLoader.Load(string path, string? expectedClientId = null)` | 登记记录文件及可选预期公开 ID | `RegistrationStatusResult`：`IsReadable`、`Status`、`Code`、`SafeSummary`；传预期 ID 时拒绝不一致记录 |
| `RegistrationStatusLoader.Parse(string json, string? expectedClientId = null)` | 登记 JSON | 同上；纯校验 |
| `LogtoDiscoveryProbe.ProbeAsync(LogtoPublicOptions options, CancellationToken cancellationToken = default)` | 已验证 Options，取消令牌 | `Task<LogtoDiscoveryResult>`：`IsValid`、`Document`、`Issues`、`SafeSummary`、`CorrelationId`；一次只读 GET |
| `LogtoDiscoveryProbe.ValidateJson(LogtoPublicOptions options, string json)` | 已验证 Options 及发现文档 | 同样的验证结果；离线测试用，无网络 |

`LogtoPublicOptions` 只能由成功校验的加载器创建。它保存不可变 URI、ClientId、范围及固定协议选项。调用方应先检查 `IsValid`，再访问 `Options`；发现失败时 `Document=null`，不能继续使用部分端点。

`LogtoRegistrationStatus.IsAuthenticationEvidence` 永远为 false。即使本地记录声称执行过登录测试，也不能借此授予权限或建立会话。登记文件不会被以上接口写入。人工记录的 `ProductLoginTestExecuted` 只是记录的原值。

登记文件可选的 `probe_history` 必须是最多 32 条的数组；旧文件可省略，也可提供空数组。每条历史记录与 `discovery_probe` 使用同一严格结构：必需 `status`（1–128 字符，仅 ASCII 字母、数字、下划线、短横线、斜线或空格）、`attempted_read_only`（布尔）、`reason`（1–4096 字符的非空白文本，无控制字符）及 `product_login_test_executed`（布尔）。所有层级拒绝未知字段和重复键，整个文件仍受 64 KiB UTF-8 大小与深度限制。`pack_version` 和 `public_values_source` 是必需的非空白文本，无控制字符，长度分别不超过 128 和 4096；`console_changes_performed` 必须为布尔，`contains_secrets` 必须为 false。历史只参与结构校验，不覆盖当前探测状态，也不能成为认证证据。

```csharp
var configDirectory = Path.Combine(AppContext.BaseDirectory, "config");
var result = LogtoConfigurationLoader.Load(Path.Combine(configDirectory, "logto.public.json"));
if (!result.IsValid)
{
    // 展示安全摘要和固定错误代码；不记录原始配置或异常消息。
    ShowConfigurationError(result.SafeSummary, result.Issues);
    return;
}
var registration = RegistrationStatusLoader.Load(
    Path.Combine(configDirectory, "logto.registration-status.json"), result.Options!.ClientId);
var probe = await LogtoDiscoveryProbe.ProbeAsync(result.Options, cancellationToken);
// probe.IsValid 仅表示公开发现检查通过；不得在这里标记用户已经登录。
```

示例中的 `ShowConfigurationError` 代表调用方 UI，不是 Identity 模块提供的方法。

## 网络与元数据边界

Probe 创建专用 HttpClientHandler，禁用所有自动重定向、Cookies 和默认 Windows 凭据；不发送 Client ID、授权头或任何令牌。TLS 使用平台正常验证，不替换证书校验。没有 GitHub 代理或下载加速配置入口；操作系统网络代理仍由平台网络栈处理。重定向直接报错，不访问 Location 地址。[.NET AllowAutoRedirect 文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0)

探测总超时为 15 秒，响应上限 128 KiB，要求 application/json，流式计数同时约束无 Content-Length 的响应。取消返回 `USER_CANCELLED`，超时返回 `AUTH_TIMEOUT`。探测无自动重试、缓存或后台循环；用户可以重试一次，重复执行不产生服务端身份变更。应用退出时调用方取消当前探测。探测函数没有用户持久化；同模块的 T03 身份服务负责真实会话与账号切换。

发现 issuer 必须与 Options.Authority 字符串精确一致。授权、token、UserInfo、JWKS 端点必需；退出、撤销端点可选。出现的所有 `*_endpoint` 均必须是同可信源的规范 HTTPS URI。检查 `code`、`S256`、公共客户端认证 `none` 声明；若提供 grant_types、response_modes、scopes，则还检查 `authorization_code`、`query` 与所配置 scope。签名算法必须与显式非对称集合 `RS256/384/512`、`PS256/384/512`、`ES256/384/512` 有交集，结果的 `AllowedIdTokenSigningAlgorithms` 只保留交集；不允许把 token 的 `alg=none` 或 HMAC 算法交给后续验证器。这是本项目固定服务的更严格部署合同，不是声称所有符合通用 OIDC 的跨源服务都被支持。发现协议与 issuer 校验依据为 [OpenID Connect Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html)，PKCE 元数据字段依据为 [RFC 8414](https://www.rfc-editor.org/rfc/rfc8414.html)。

签名算法元数据检查仅确认声明，不读取 JWKS、不验证 token。T03 使用固定 Duende.IdentityModel.OidcClient 7.1.0 与 Microsoft.IdentityModel.JsonWebTokens 8.23.0，真实会话另验证签名、issuer、audience、nonce、有效期及关联约束。2026-10-01 本机只读发现得到 ES384；此前仅接受 RS256 的初始探测失败报告保留，客户端已修正算法策略，并增加真实 P-384 签名正负测试。Logto 官方说明支持 EC 和 RSA 签名，见 [Logto 签名密钥轮换文档](https://docs.logto.io/logto-oss/using-cli/rotate-signing-keys)。这不要求修改服务端密钥或客户端登记。

## 错误与核对步骤

| 代码 | 含义 | 可恢复方式 |
|---|---|---|
| `AUTH_NOT_CONFIGURED` | 公开配置文件或目录不存在 | 检查产物打包配置路径 |
| `AUTH_CONFIGURATION_ERROR` | 结构、版本、值、大小或读取错误 | 修复唯一配置源后重新构建；不输入秘密 |
| `AUTH_REGISTRATION_UNVERIFIED` | 人工登记备注缺失、不匹配或不可读 | 在 Logto 控制台核对 Native 类型和两个完整回调，并保存证据 |
| `REGISTRATION_NOTES_ONLY` | 已读到人工记录 | 仍然不能据此证明真实登录 |
| `AUTH_METADATA_UNAVAILABLE` | 网络、TLS、HTTP、重定向、内容类型或发现文档失败 | 使用安全 Field 确认失败环节；不降低 TLS/issuer 校验 |
| `AUTH_TIMEOUT` | 公开 GET 超过 15 秒 | 检查网络后按用户动作重试 |
| `USER_CANCELLED` | 调用方取消探测 | 保持当前游客/离线状态 |

Issue 只包含固定字段名、安全中文摘要和代码；HTTP 错误可附状态数字。DNS、TLS、连接和代理隧道错误有不同的安全 Field。原始服务端响应、异常消息、授权码、回调查询串、密码和令牌不得写日志。`CorrelationId` 每次结果独立生成，仅用于本机诊断关联。

派蒙已确认 Native/public 类型与登录回调；退出回调的完整登记仍需核对。发现 GET 无法证明这两项。Windows 取消、端口占用、登录期间单实例已有实测；合成协议中的回调与令牌负面测试分别记录。真实用户浏览器登录往返、真实刷新、退出和第二账号仍为 `not_run`，最新命令、结果与证据见根 AUTUMNOS_PROGRESS.md。不索取 Client Secret。

## 本地验证

`IdentityTests.Cases()` 是无需外部测试包的控制台 runner 用例来源，使用 example.org 合成公开配置，并直接读取随测试产物复制的真实公开配置与登记记录做回归。覆盖严格结构、未知秘密字段、重复属性、同源约束、回调精确性、范围最小化、PKCE 禁用、元数据端点/issuer/能力错误、大小限制、取消、登记历史条数/结构边界以及登记文件不能认证。单元测试不连接 Logto、不计作真实登录通过。

可单独构建本模块：

```powershell
& ./.tools/dotnet/dotnet.exe build src/AutumnOS.Identity/AutumnOS.Identity.csproj -c Release --no-restore -p:Platform=x64
```

完整执行命令、真实退出码、源文件快照及测试报告由根 `AUTUMNOS_PROGRESS.md` 和本地 artifacts 报告记录。不要将文档示例或源码生成视为执行证据。
