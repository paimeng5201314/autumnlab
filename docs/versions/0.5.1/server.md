# 独立服务器身份与HTTP合同

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1；后端HTTP合同1.0.0，OpenAPI3.1。制作人：派蒙。第五类backend-identity复用实际ServerIdentity和NativeIdentity样例，不提供宿主代签服务。identity.beginAppSession仍CAPABILITY_UNAVAILABLE。昵称、appScopedUserId、ID token或裸userId不能替代独立资源access token。

交付BackendIdentity/server包含自包含服务器和openapi.json；client包含系统浏览器PKCE客户端、config.example.json和config.schema.json。真实配置需独立Native/public客户端、127.0.0.1:17854/callback/回调、HTTPS API resource与最小scope登记。不能把宿主ClientId当独立资源ID。配置值是公开身份元数据；不要提供Client Secret、密码、私钥或token。缺字段时退出2，不启动监听。

服务器环境变量是AUTUMN_SERVER_AUTHORITY、AUTUMN_SERVER_METADATA、AUTUMN_SERVER_AUDIENCE、AUTUMN_SERVER_CLIENT_ID、AUTUMN_SERVER_REQUIRED_SCOPE。它们分别对应客户端公开配置的Authority、MetadataAddress、Resource、ClientId、RequiredScope。先按 `BackendIdentity/client/README.md` 创建新的config.local.json并填入已登记值，保留随包config/logto.public.json作为防误用宿主登记的检查材料。空模板不是合法配置。

在Developer/BackendIdentity/server目录的第一个PowerShell中，按该目录README把实际公开配置映射到五个本进程环境变量，再执行：

~~~powershell
& ./AutumnOS.ServerIdentity.Sample.exe
~~~

只监听127.0.0.1:5197。在Developer/BackendIdentity/client目录的第二个PowerShell中执行：

~~~powershell
& ./AutumnOS.NativeIdentity.Sample.exe --config ./config.local.json
~~~

客户端会打开系统浏览器等待实际授权；运行前须确认本机服务为自己启动，且独立登记获准。凭据仅在内存，通过Authorization header送到固定本机资源，不写查询串、日志、命令参数或聊天。正常结束自有服务器使用Ctrl+C。两个EXE保留相邻自包含运行时即可启动，不需要工作区scripts/Common.ps1或`$Dotnet`。旧源码构建命令只属于原工作区记录。

| 路由 | 输入、输出与失败 |
|---|---|
| GET /health | 无认证；返回service/producer/status=configured/real_provider_verified=false。只证明配置加载 |
| GET /v1/me | 单个Bearer头且无query；返回subject/issuer/clientId。幂等，不创建档案 |
| POST /v1/action-challenge | 验证后返回随机challenge与expiresIn=60；每次产生新挑战，不幂等；1024上限429 |
| POST /v1/confirm-action | application/json的challenge，正文≤4096字节；正确身份原子消费一次，返回accepted=true及无游戏变更的effect。重放/过期/跨身份409 |

签名验证仅RS256/PS256/ES256/ES384，固定issuer、audience、client_id、scope、iat/exp及30秒时钟偏差。可信HTTPS发现/JWKS不跟随重定向，10秒、128KiB、最多32key、30秒缓存。401 INVALID_CREDENTIAL，403 INSUFFICIENT_SCOPE，503 KEYS_UNAVAILABLE；所有响应no-store。JSON绑定/内容类型/大小错误另可能400/415/413，不承诺框架错误有JSON正文。取消请求中止网络取key；POST可能已消费后断线，不自动重试或宣称未执行。

挑战绑定issuer/sub/client，重启丢弃，内存实现不等于生产多实例防重放。有效Bearer本身可重复读取，账号验证也不能证明分数真实。真实部署需HTTPS、限流、密钥轮换和持久业务规则，须另行授权。临时RSA/ECDSA正反例、实际本机HTTP fixture和OpenAPI验证分开报告，均不能代替独立Logto登记与真实登录。源码在BackendIdentity/Sources，通过Build-Sources.ps1复制到干净目录再编译。参见[核心开发](core-development.md)。
