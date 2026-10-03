# ADR 0005 · 开发者服务器的独立 Logto 资源令牌

制作人：派蒙。关联 R034 / A034。决定：本地示例选择规范允许的独立 Logto 接入；新增真实应用/API 资源登记和部署须先由派蒙确认，本次不操作管理控制台、不创建公共服务、不更改 AutumnOS 宿主公开配置。

应用 SDK 的资料授权只证明用户愿意让该应用读取有限资料，不形成服务器认证。appScopedUserId、昵称、头像、宿主账号命名空间以及客户端断言都不是登录凭据。客户端不签发平台可信 JWT，不发送宿主 access/refresh/ID token 给开发者服务器。

开发者为自己的客户端申请独立 Native/public Logto 登记、精确回调和 API resource audience，配置该资源所需的最小 scope。客户端通过系统浏览器授权码 + PKCE 获得面向此 resource 的 access token，服务端每次请求验证签名、可信 issuer、资源 audience、有效期、iat、所需 scope 和独立客户端 client_id。第三方 Logto 同意界面与 AutumnOS 的本地 profile 同意是不同层次。该标准路径见 [Logto 第三方应用协议](https://docs.logto.io/integrate-logto/third-party-applications) 与 [ASP.NET Core API 保护](https://docs.logto.io/api-protection/dotnet/aspnet-core)。

本次交付 `samples/server-identity` 可运行本地 .NET 服务器与负面测试，以及 `samples/native-identity-client` 的真实系统浏览器/PKCE客户端、公开配置Schema和控制台字段指南。服务端五项配置必填，客户端也拒绝空模板和宿主ClientId，没有默认生产客户端、测试密钥回退或虚构资源。服务器固定本机127.0.0.1:5197，独立客户端回调固定127.0.0.1:17854/callback/，不与宿主17853混用；管理配置、独立登记及部署仍须确认。没有真实资源配置时实际往返保持blocked/not_run。

客户端只在进程内存持有独立resource access token，并在授权请求及代码交换发送resource。复用Identity严格验证器的内部typed opt-in仅供friend sample程序集，不增加宿主SDK导出能力；host默认scopes、回调及UserInfo验证保持原样。资源JWT不发往UserInfo：样例只使用已验签ID token主体，服务器独立验证access token。样例不请求offline_access、不保存refresh token，不承诺浏览器退出。这是开发者独立Native标准参考，不是允许.autumn启动任意EXE，也不是未实现的平台委托服务。

验证器使用固定 Microsoft.IdentityModel.JsonWebTokens 8.23.0，HTTPS 发现固定 issuer，并仅从同可信源读取 JWKS；禁止 HTTP 重定向，每次请求 10 秒、128 KiB 限制、最多 32 个签名密钥，30 秒有界刷新缓存。显式算法集合为 RS256/PS256/ES256/ES384；保留白名单，补入本地真实发现已观察到的 ES384，并用临时 ECDSA P-384 密钥测试正确及错误签名。时钟偏差 30 秒。缺少 exp/iat、裸 userId、错误 audience、平台 ID token、错误客户端、伪签名、过期、缺 scope 等均拒绝。验证错误只返回固定码，不把完整 token/claims/请求查询/exception 送日志。

GET `/v1/me` 是幂等读取，OAuth bearer token 在有效期内可重复使用；本示例不假称可以防止持有者重放一切 bearer 请求。敏感动作需额外业务约束。示例 `/v1/action-challenge` 发 60 秒、32 随机字节、绑定已验证 issuer/sub/client 的一次性挑战；`/v1/confirm-action` 原子消费，重复/过期/跨账号拒绝，最多 1024 待处理项。该示例只确认测试动作，不写分数；这不证明游戏成绩可信，也不取代 TLS、服务端规则和部署时分布式重放存储。重启会丢弃未完成挑战，不恢复已签发临时授权。

宿主 SDK 的 `identity.beginAppSession` 在没有独立登记与客户端协议适配前继续返回 CAPABILITY_UNAVAILABLE，不能把此服务端示例存在写成宿主已经完成代签/换取凭据。后续若改为平台服务器委托短期凭据，需要另一个明确 ADR、新服务授权及服务端密钥托管，不应偷偷改变本次边界。
