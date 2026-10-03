# backend-identity：复用独立身份服务器

适用 AutumnOS 0.5.1 系列，SDK 0.3.0、应用消息协议 1；制作人：派蒙。这是第五类样例的入口，不复制两套会漂移的身份实现。

实际服务器在 `samples/server-identity/`，机器合同是其中的 `openapi.json`；实际系统浏览器/PKCE调用方在 `samples/native-identity-client/`。开发者包的对应位置为 `BackendIdentity/server/`、`BackendIdentity/client/`，源码位于 `BackendIdentity/Sources/`。它们是独立进程示例，不能装进.autumn、由应用启动EXE或假装SDK已经提供服务端委托凭据。

从交付包使用时，先按 `BackendIdentity/client/README.md` 从空模板建立新公开配置，填入实际独立登记值；然后按 `BackendIdentity/server/README.md` 在第一个PowerShell设置本进程环境并运行 `AutumnOS.ServerIdentity.Sample.exe`。在第二个PowerShell的client目录运行 `./AutumnOS.NativeIdentity.Sample.exe --config ./config.local.json`。保留每个目录的相邻DLL、运行时和client内的宿主公开配置。两个EXE均自包含；正常使用不需要 `scripts/Common.ps1`、源码bin目录或`$Dotnet`。

若要重建而非运行现成程序，在Developer目录调用 `./BackendIdentity/Build-Sources.ps1 -Destination <新的绝对目录> -DotnetPath <已有10.0.401 SDK的dotnet.exe>`。脚本只使用随包Sources、锁文件和官方依赖恢复，不从工作区借文件。旧样例README中的工作区命令明确属于历史开发记录，不作为交付包前置。

独立 Logto 应用、资源 audience/scope 登记仍需外部授权。空配置退出2，不监听、不创建模拟账号。不要把宿主 ClientId、昵称或 appScopedUserId 填成服务器登录凭据；不要将 token 粘贴到参数、日志或聊天。客户端仅在内存持有自己独立获得的资源token。

正例：实际登记完成后，在系统浏览器授权，客户端通过 Authorization 请求 `/v1/me`，然后获取并消费一次挑战。负例由已有 `ServerIdentityTests` 的临时 RSA/ECDSA fixture验证裸ID、伪造签名、过期、错误audience/client、缺scope、跨账号/重复/过期挑战。合同工具另外使用真实 JSON Schema/OpenAPI验证器校验响应正反例。这些是受控测试，不算真实Logto联调或外部新人验收。

完整版本入口位于开发者包 `Docs/0.5.1/README.md`；干净目录复现按其中 `first-app.md` 执行。无独立登记时前四个内部样例仍可使用游客数据运行；identity资料成功支路及真实后端往返分别记 blocked/not_run。
