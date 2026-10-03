# 独立服务器身份示例

Lab Chronicles AutumnOS · 制作人：派蒙。仅本地运行，没有预设生产服务、账号或签名密钥。

该示例接收开发者独立 Logto 客户端获得的资源 access token，不接收 AutumnOS 宿主凭据、昵称或 appScopedUserId。实现决定和部署边界见 `docs/adr-0005-independent-server-identity.md`。API 定义见本目录 `openapi.json`。

先取得独立客户端/API 资源登记批准，再由实际配置填写以下环境变量。不得把 `tjck5m8ohjkw272y42adv` 宿主应用当作已登记的业务 API audience，也不把示例 fixture 配置复制到生产。

| 环境变量 | 真实值来源 |
|---|---|
| AUTUMN_SERVER_AUTHORITY | 可信 Logto issuer，精确完整值 |
| AUTUMN_SERVER_METADATA | 此 issuer 的 `/.well-known/openid-configuration` |
| AUTUMN_SERVER_AUDIENCE | 为开发者服务登记的独立 HTTPS API resource identifier |
| AUTUMN_SERVER_CLIENT_ID | 获准调用这个服务的独立 Native/public 客户端 ID |
| AUTUMN_SERVER_REQUIRED_SCOPE | API resource 的已登记最小权限名，单个无空格 scope |

## 从交付套件启动

适用 AutumnOS 0.5.1：交付目录 `Developer/BackendIdentity/server/` 包含自包含 `AutumnOS.ServerIdentity.Sample.exe` 与全部相邻运行时文件，不依赖工作区或预装 .NET SDK。先按交付 `Developer/BackendIdentity/client/README.md` 建立并填写仅含真实公开登记值的 `config.local.json`，再在 server 目录的独立 PowerShell 中运行：

```powershell
$config = Get-Content -LiteralPath ../client/config.local.json -Raw | ConvertFrom-Json
$env:AUTUMN_SERVER_AUTHORITY = $config.Authority
$env:AUTUMN_SERVER_METADATA = $config.MetadataAddress
$env:AUTUMN_SERVER_AUDIENCE = $config.Resource
$env:AUTUMN_SERVER_CLIENT_ID = $config.ClientId
$env:AUTUMN_SERVER_REQUIRED_SCOPE = $config.RequiredScope
& ./AutumnOS.ServerIdentity.Sample.exe
```

这些环境变量只设置在当前 PowerShell 进程及其子进程，不写系统或用户持久环境。保持该窗口运行服务器；正常使用 Ctrl+C 结束自有服务器。再从另一个 PowerShell 执行 client 目录的 EXE。不要在尚未登记时填造假值来宣称真实对接成功，不向服务器传宿主 token。

如需重建，在 `Developer` 目录调用 `./BackendIdentity/Build-Sources.ps1 -Destination <不存在的新目录> -DotnetPath <已有SDK10.0.401的dotnet.exe>`。交付源码不包含工作区 `scripts/Common.ps1`；该独立脚本使用随包固定项目、props 和锁文件。

## 原工作区构建命令

以下只适用于完整源码工程根目录（使用已有本地 SDK，不全局安装），不是交付 Developer 目录的启动命令：

```powershell
& ./.tools/dotnet/dotnet.exe restore ./samples/server-identity/AutumnOS.ServerIdentity.Sample.csproj --locked-mode
& ./.tools/dotnet/dotnet.exe run --project ./samples/server-identity/AutumnOS.ServerIdentity.Sample.csproj --no-launch-profile
```

配置不全时退出码 2，监听器不会打开。配置完整后只监听 `http://127.0.0.1:5197`；`/health` 表示配置已加载，不表示真实 Logto 验证通过。此 HTTP 地址仅用于本机样例；真实部署需要明确授权、HTTPS、限流、持续密钥轮换/告警和适合多实例的重放存储。

配套调用方源码在 `samples/native-identity-client/`，交付 EXE 在 `Developer/BackendIdentity/client/`：复用严格 OIDC 流，以系统浏览器、独立 Native ClientId、固定17854回调，在授权与代码交换中请求真实 resource/scope。凭据只在内存，以 Authorization header 调用本机服务，不能粘贴到命令行/查询串/日志/聊天。准确控制台字段、配置 Schema、取消和实际命令见对应目录 README。代码可运行不代表独立登记已完成；只有实际授权且 audience 正确的 access token 能访问 API。

接口行为：

- GET `/v1/me`：无正文；验证后返回此独立服务器的 subject、issuer、clientId；幂等、可重复调用。服务端主体键使用 issuer+subject，不能用昵称定位。
- POST `/v1/action-challenge`：无正文；权限同上；返回随机 challenge 与 expiresIn=60。超过 1024 待处理返回 429。挑战由当前服务器进程持有，重启后无效。
- POST `/v1/confirm-action`：JSON `{ "challenge": "调用上一步实际返回的值" }`，正文最多 4 KiB；同一验证身份可原子消费一次；重复/过期/跨用户返回 409。确认仅作示例，不写入游戏分数。

取消：HTTP 请求中止取消 JWKS 等联网操作；HTTP 超时 10 秒，不自动重试变更请求。未知 token 无状态，不为验证失败创建玩家档案。密钥获取失败返回 503，不退回不验签或旧测试密钥；最多缓存 30 秒公钥，轮换窗口内客户端可稍后重试。

错误：401 INVALID_CREDENTIAL（含伪造、错误 issuer/audience/client、过期、缺失字段与裸身份）；403 INSUFFICIENT_SCOPE；503 KEYS_UNAVAILABLE；429 CAPACITY_REACHED；409 INVALID_OR_REPLAYED_CHALLENGE。任何错误均不回传 token、claims 或原始异常。默认清除日志 provider，后续日志只能接入经过脱敏的固定字段。

服务器显式允许 RS256/PS256/ES256/ES384，不依据 token 自报 alg 无限制放行。ES384 与本地已观察到的发现算法兼容，仍需真实资源 token 联调证明登记有效。`ServerIdentityTests` 使用临时 RSA 和 ECDSA P-384：正例验证完整签名资源 token，反例验证 P-384 错误签名、伪签名、过期、错误对象、错误客户端、ID token、裸昵称、无 scope、无 exp；挑战测试覆盖跨账号、重复、过期及容量。它们不算真实 Logto 后台验收。第二账号、独立应用登记、真实 resource/scope 和部署联调按证据保持 not_run/blocked。
