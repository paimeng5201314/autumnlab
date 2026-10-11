# 独立服务器身份 · 0.5.1

AutumnOS 0.5.1 的 Developer 套件包含 `BackendIdentity/server`、`BackendIdentity/client` 及可重建源码。它展示独立 Native/public client 的系统浏览器 PKCE 登录和本机 API 验证，不使用宿主凭据，也不将普通 EXE 导入内部应用。真实登记和认证必须另行完成；提供示例、健康检查或合成 fixture 通过不表示真实服务器身份已验证。

先由有权管理租户的人登记或批准独立 Native Client ID、HTTPS API resource identifier 和真实最小 permission。不能复用 AutumnOS 宿主 Client ID 作为独立业务客户端或 API audience。Native 回调固定为 `http://127.0.0.1:17854/callback/`，与宿主 17853 分开；保留末尾斜杠，不占公网端口。代码使用授权码、PKCE S256 和公开客户端 `none`，无需 Client Secret。

套件 client 目录的 `config.example.json` 是故意不可运行的空模板。复制到新的本地公开配置后填写 Authority、MetadataAddress、ClientId、RedirectUri、Resource、RequiredScope 与 schemaVersion。先按该目录 README 检查关联规则，绝不把 token、密码或管理密钥放入配置。定位单文件展开后的 `$developerRoot` 方法见 [第一个应用](first-app.md)。

```powershell
Set-Location (Join-Path $developerRoot 'BackendIdentity/server')
$config = Get-Content -LiteralPath ../client/config.local.json -Raw | ConvertFrom-Json
$env:AUTUMN_SERVER_AUTHORITY = $config.Authority
$env:AUTUMN_SERVER_METADATA = $config.MetadataAddress
$env:AUTUMN_SERVER_AUDIENCE = $config.Resource
$env:AUTUMN_SERVER_CLIENT_ID = $config.ClientId
$env:AUTUMN_SERVER_REQUIRED_SCOPE = $config.RequiredScope
& ./AutumnOS.ServerIdentity.Sample.exe
```

保持服务器窗口运行，在另一 PowerShell 的 client 目录执行 `./AutumnOS.NativeIdentity.Sample.exe --config ./config.local.json`。先确认固定 5197 监听者是自己启动的示例。服务器只监听本机 HTTP，远端部署需独立 HTTPS 与运维设计；客户端不跟随重定向转发 token。真实成功须来自本次 `RESOURCE_API_VERIFIED` 结果。

API 顺序为 GET `/v1/me`、POST `/v1/action-challenge`、POST `/v1/confirm-action`。服务器验证签名、issuer、audience、client、有效期及 scope；挑战 60 秒内同一身份只消费一次。401/403/503 分别要求核查凭据、权限、可信密钥可用性，不能通过关闭校验解决。第二账号、取消、端口占用、重放和真实错误 scope 应独立留存脱敏证据。详情以套件两侧 README、config.schema.json 与 openapi.json 为准。
