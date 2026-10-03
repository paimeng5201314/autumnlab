# 独立 Native 客户端与服务器身份联调

Lab Chronicles AutumnOS，制作人：派蒙。此目录是可执行的 .NET Native 控制台程序：系统浏览器 → PKCE 授权码 → 独立资源 access token → 本机服务器验证与一次性挑战。它复用 Identity 的严格 OIDC 验证器，不调用 AutumnOS SDK、不读取宿主凭据、不触碰 AutumnOS_Data；不把任意 EXE 导入成内部游戏。

选择见 `docs/adr-0005-independent-server-identity.md`。代码与 fixtures 已可运行，真实独立应用/API resource 登记尚未完成。没有借用宿主 ClientId，没有虚构生产服务器或成功认证。`.autumn` 的 `identity.beginAppSession` 继续返回 CAPABILITY_UNAVAILABLE；此独立标准示例不是尚未批准的宿主委托服务。

## 准确控制台字段与授权边界

以下涉及**新增独立应用/API/角色配置**，必须先取得派蒙或对应租户管理者批准。本轮只提供本地代码和字段指南，未调用管理 API，不需要 Client Secret、密码、验证码或管理密钥。

1. 创建或选择开发者自己获准使用的 **Native/public client**，记录独立 Client ID。不能复用 `autumnos-spec/config/logto.public.json` 中 AutumnOS 宿主的 ClientId，代码会从构建链接复制的同一配置读取并拒绝复用；配置缺失时也拒绝，不在代码或 Schema 散布另一份真实 ID。采用授权码、PKCE S256、token endpoint authentication method `none`，不嵌入共享 secret。
2. 精确登记 Redirect URI **`http://127.0.0.1:17854/callback/`**，保留末尾斜杠。这与宿主 17853 独立；不随机换端口、不监听公网、不结束占用者。样例没有浏览器退出能力，不要求登记未实现的退出回调。
3. 创建或选择获准访问的独立 API resource，记录**真实 HTTPS resource identifier**。它是 access token 的 audience，不是 Native Client ID，也不是本机 HTTP 监听地址；127.0.0.1:5197 仅承载本机验证，不代表存在同名公网服务。
4. 为资源选择真实最小 permission。按租户 RBAC/第三方应用设置批准独立客户端请求 resource/scope，并为测试用户分配含此权限的角色或实际授权。请求 scope 不代表实际得到授权；未授予时 API 返回 403。
5. 核对 issuer/metadata。可使用已确认租户 Authority，但独立 ClientId/resource/scope 必须来自真实登记，不把占位符当值。实际 token 缺少示例要求的 `client_id`/`scope`/资源 audience 时核查登记，不关闭验证。

复制 `config.example.json` 为新的本地公开配置文件，再填写以下七项。空模板故意不可用：

| 字段 | 输入约束及来源 |
|---|---|
| schemaVersion | 固定 1 |
| Authority | 可信完整 HTTPS issuer，无用户信息/查询串/片段 |
| MetadataAddress | 精确 `Authority.TrimEnd('/') + '/.well-known/openid-configuration'` |
| ClientId | 独立已登记 Native/public 客户端，1–128 字符，无空白 |
| RedirectUri | 固定 `http://127.0.0.1:17854/callback/` |
| Resource | 已登记 HTTPS API resource identifier，无查询串/片段；与服务端 Audience 相同 |
| RequiredScope | 单个真实 API permission，1–128 字符，无空白；不是 openid/profile/offline_access |

Schema 在 `config.schema.json`，运行时另校验关联字段、重复键与协议。文件只放公开标识；增加 token/secret 等字段会被拒绝。

## 从交付的 Developer 目录执行

适用 AutumnOS 0.5.1 的完整开发者套件。打开交付目录 `Developer/BackendIdentity/client/` 中的 PowerShell，保留相邻 DLL、运行时和 `config/logto.public.json`。这里的 `AutumnOS.NativeIdentity.Sample.exe` 已自包含，不需要工作区、`scripts/Common.ps1`、`$Dotnet` 或预装 .NET SDK。

先建立一个不存在的新公开配置文件，按上表填入已经登记的真实值。文件只含公开标识，空模板运行会被拒绝；本步骤不完成登记，也不请求任何秘密：

```powershell
$configPath = Join-Path (Get-Location) 'config.local.json'
if (Test-Path -LiteralPath $configPath) { throw '已有配置保持不变；使用原配置或另选新文件名。' }
Copy-Item -LiteralPath ./config.example.json -Destination $configPath
```

填写完配置后，在另一个 PowerShell 中按交付 `Developer/BackendIdentity/server/README.md` 启动真实本机服务器。确认 5197 监听者是自己启动的样例，再从 client 目录执行：

```powershell
$configPath = Join-Path (Get-Location) 'config.local.json'
& ./AutumnOS.NativeIdentity.Sample.exe --config $configPath
$LASTEXITCODE
```

此命令会按下文规则打开系统浏览器并等待用户认证；未授权或未登记时不要运行来制造成功记录。退出码和固定结果必须来自实际执行。`config/logto.public.json` 是随包提供的宿主公开配置，仅用于拒绝误复用宿主登记；不要把它替换为独立客户端配置或删掉后跳过检查。

需要重建源码时，从 `Developer` 目录运行交付脚本，并指定已经存在的精确 SDK 和一个不存在的输出目录：

```powershell
& ./BackendIdentity/Build-Sources.ps1 -Destination 'D:/你的新后端构建目录' -DotnetPath 'D:/已有SDK10.0.401/dotnet.exe'
```

路径按本机实际位置填写。脚本只在新目录恢复固定依赖和编译，不生成全局工具、证书或修改 PATH；详细输出路径以本次构建日志为准。下面保留早期工作区构建命令与测试历史，它们不适用于交付套件目录。

## 原工作区构建与执行记录

在工程根普通 PowerShell 使用已有 SDK 和缓存，无全局安装。以下为本轮实际成功的构建方式；将 ArtifactsPath 替换为本机工作区内一个新的绝对输出目录：

```powershell
. ./scripts/Common.ps1
$sampleArtifacts = Join-Path $ProjectRoot 'artifacts/native-client-build-01'
Invoke-Dotnet -Arguments @('restore','samples/native-identity-client/AutumnOS.NativeIdentity.Sample.csproj','--locked-mode','-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$sampleArtifacts")
Invoke-Dotnet -Arguments @('build','samples/native-identity-client/AutumnOS.NativeIdentity.Sample.csproj','-c','Release','--no-restore','-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$sampleArtifacts")
```

现有工程统一 OutputPath，所以此构建方式的实际程序是 `samples/native-identity-client/bin/x64/Release/AutumnOS.NativeIdentity.Sample.exe`；必须保留相邻 DLL。obj 位于指定 artifacts 路径。依赖使用现有固定版本和 lockfile，不升级基础框架。正常解决方案构建的程序路径以构建报告为准。

在完整源码工作区按 `samples/server-identity/README.md` 配置并运行服务器。五个变量与客户端逐项对应：AUTUMN_SERVER_AUTHORITY=Authority，AUTUMN_SERVER_METADATA=MetadataAddress，AUTUMN_SERVER_CLIENT_ID=ClientId，AUTUMN_SERVER_AUDIENCE=Resource，AUTUMN_SERVER_REQUIRED_SCOPE=RequiredScope。以下是旧工作区命令；交付包使用上一节的 EXE：

```powershell
& $Dotnet ./samples/native-identity-client/bin/x64/Release/AutumnOS.NativeIdentity.Sample.dll --config '<真实公开配置文件路径>'
```

配置通过后独占绑定回环，获取可信 HTTPS discovery/JWKS，再打开系统浏览器。用户自己完成认证；Ctrl+C 取消，最长三分钟。验证回调 state、ID token 签名/nonce/issuer/audience/有效期后，只打印固定结果码。随后内存中的 Authorization header 调用固定本机 `GET /v1/me → POST /v1/action-challenge → POST /v1/confirm-action`；同一身份验证与挑战消费都通过才打印 RESOURCE_API_VERIFIED。

**本机 API 信任边界**：先确认 5197 是自己启动的样例服务。本机 HTTP 不适合生产远程 token 传输；真实远端服务需要另行批准与 HTTPS 运维配置。程序禁用代理/Cookie/重定向，不跟随 30x 将凭据发送到另一地址。

## 协议与生命周期

请求 `openid profile`＋RequiredScope，授权和代码交换均带同一 resource。不提供保持登录、不请求 offline_access；即使服务意外返回 refresh token 也丢弃。资源 JWT **不用于 Logto UserInfo**：此样例主体来自已验签 ID token，服务器独立验证资源 access token；宿主账号流程仍使用 UserInfo 并校验 sub，不受此内部 opt-in 影响。

每次运行独立 state/nonce/PKCE，仅一次登录，无重试循环。另一 sample 占用 17854 时立即失败，不开第二监听；这不改变 AutumnOS 主程序的单实例召回协议。成功/拒绝/取消/超时都释放回环。凭据只在内存，不写普通 JSON、日志、报告、localStorage 或宿主 DPAPI 文件；程序退出不冒称 Logto 浏览器会话退出。

提供方每次 HTTP 15 秒/128 KiB；本机 API 每次 10 秒/8 KiB，无 POST 自动重试。挑战60秒、绑定主体、有界且原子消费；只是示范确认，不修改游戏分数。取消后未知网络结果不自动重发；重新启动重新认证。参数、query、请求正文及控制台都不携带 token。

| 固定结果／退出码 | 含义 |
|---|---|
| SAMPLE_CONFIGURATION_REQUIRED / 2 或配置阶段 4 | 命令参数/公开配置缺失或无效；不开放监听/浏览器 |
| SAMPLE_HOST_CONFIGURATION_REQUIRED / 4 | 同目录 config/logto.public.json 缺失/无效；修复完整构建产物，不跳过宿主 ClientId 防复用检查 |
| USER_CANCELLED_OR_TIMEOUT / 3 | 主动取消或总体超时，监听已清理 |
| AUTH_PORT_IN_USE / 4 | 17854 被占用，不杀进程、不改端口 |
| USER_CANCELLED / 4 | 提供方拒绝；没有认证成功 |
| AUTH_INVALID_RESPONSE / 4 | 身份验证失败，不能降低 TLS/签名/issuer 等检查 |
| SESSION_EXPIRED / 4 | 代码交换 invalid_grant，开始新事务，不能重放回调 |
| SAMPLE_RESOURCE_TOKEN_REQUIRED / 4 | 未获得资源 JWT 或 token 已过期 |
| SAMPLE_API_UNAUTHORIZED / 4 | 服务器拒绝凭据签名、对象、客户端或有效期 |
| SAMPLE_API_FORBIDDEN / 4 | 所需资源权限没有实际授予 |
| SAMPLE_API_IDENTITY_MISMATCH / 4 | API 主体与已验证 ID token 不同 |
| SAMPLE_API_UNAVAILABLE / 4、SAMPLE_REQUEST_FAILED / 5 | 本机服务、网络或响应失败；不输出原始内容/凭据 |
| RESOURCE_API_VERIFIED / 0 | 本次实际运行身份、API 权限及挑战都通过；只有真正运行记录才算真实联调 |

## fixture 与真实验收分开

交付套件如需单独运行受控协议自测，可在 `Developer/BackendIdentity/client/` 使用 `./AutumnOS.NativeIdentity.Sample.exe --self-test <不存在的新报告绝对路径>`。这是显式 fixture，不能代替真实登录；执行前须确认没有其他测试占用回环端口。本次文档修订没有执行此自测。下列命令与计数是保留的历史工作区记录。

```powershell
& $Dotnet ./samples/native-identity-client/bin/x64/Release/AutumnOS.NativeIdentity.Sample.dll --self-test artifacts/reports/t03-foundation/native-client-tests-new.json
```

报告路径必须新建，不覆盖旧失败。该开关显式使用合成 HTTPS/API handlers、临时 RSA、**真实 TCP 回调**，不打开浏览器、不联系 Logto、不改宿主账号。本轮 `native-client-tests-01.json` 为17/17：完整PKCE/resource/client字段、错误state后正常回调、nonce/签名、拒绝/invalid_grant、错误资源对象/过期/scope、API主体不符/重定向/超限、端口占用、取消释放、禁止复用宿主登记、配置与原宿主端口约束。后续改用实际 BrandInfo 构建身份，由最终测试报告关联快照。

真实独立登记与用户认证仍 blocked/not_run。实测报告需含公开配置核对、命令、构建/源哈希、固定结果、取消及第二账号情况；不记录 token、授权码、完整回调或敏感浏览器画面。不能以 health 或 fixture 成功冒充真实登记成功。

冻结前兼容复核补入真实 ECDSA P-384 fixtures：ES384 元数据/JWKS/ID token/资源 token 完整成功、同 keyId 的错误 ID 签名及错误资源签名均拒绝。另补唯一宿主公开配置缺失时 fail-closed；`native-client-es384-tests-01.json` 实际 **21/21 passed**，旧17项报告保留。MSBuild将唯一 `autumnos-spec/config/logto.public.json` 链接复制到输出 `config/logto.public.json`，实际字节哈希一致；源文件和Schema不硬编码第二份真实宿主ClientId。

核验资料：[Logto opaque token 与 UserInfo](https://docs.logto.io/concepts/opaque-token)、[Logto 资源 access token](https://docs.logto.io/quick-starts/react)。固定 NuGet 7.1.0 XML 的 ProcessResponseAsync backChannelParameters 用于资源代码交换；没有操作源码远端。
