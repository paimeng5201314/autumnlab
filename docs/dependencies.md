# 固定依赖、环境与许可

制作人：派蒙。本地核验日期：2026-10-01。具体传递版本与内容哈希以每个项目 packages.lock.json 为准。

| 组件 | 固定版本 / 实际版本 | 用途 |
|---|---|---|
| .NET SDK | 10.0.401 | 项目内编译工具，含 MSBuild 18.9.11 |
| .NET Runtime | 10.0.12 | SDK 实际携带；客户端自包含 |
| Windows App SDK | 1.8.260921001（1.8.12） | WinUI 3 与 Windows App Runtime，自包含 |
| Windows SDK BuildTools | 10.0.26100.9169 | NuGet 构建工具；不同于机器安装的 SDK |
| WebView2 SDK | 1.0.4258.31 | 固定开发接口 |
| WebView2 Runtime | 本机 154.0.4258.48 | 独立运行环境；本机存在不等于离线发行策略完成 |
| Duende.IdentityModel.OidcClient | 7.1.0 | Native 授权码 / PKCE 参数、协议处理；显式接入签名验证器 |
| Microsoft.IdentityModel.JsonWebTokens | 8.23.0 | 宿主和独立服务器样例 JWT 验证 |
| System.Security.Cryptography.ProtectedData | 10.0.0 | 当前 Windows 用户范围 DPAPI 加密 |

T03 新增依赖的实际官方包与传递版本保存在各项目 packages.lock.json；未升级已有 .NET/Windows App SDK/WinUI/WebView2 基线。身份实现不使用 OIDC 库默认的无签名验证路径，实际验证器见 `src/AutumnOS.Identity/SignedIdentityTokenValidator.cs`。官方用法见 [Duende 手动协议流程](https://docs.duendesoftware.com/identitymodel-oidcclient/manual/)。新增库许可原文由同一打包脚本保留，服务器样例采用独立开发者 Logto 配置。

官方依据：[.NET 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)、[Windows App SDK 1.8 发布说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8?pivots=stable)、[自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)、[Windows SDK BuildTools 包](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.9169)、[WebView2 包](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31)。实际组合经本机 Release 编译检验；不能只凭文档宣称另一台系统也已验证。

SDK 安装脚本锁定官方 ZIP SHA-512。NuGet 固定直接依赖并保存传递依赖锁；后续恢复使用 --locked-mode。可选官方缓存脚本使用 dotnet nuget verify 校验包签名和锁定内容哈希。签名包的 contentHash 排除签名元数据，不等于整个 ZIP 的 SHA-512；参见 [NuGet 验证命令](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify)。

Windows App SDK、Windows SDK BuildTools、WebView2 按包内微软软件许可条款使用，不能把它们统一标作本项目 MIT。SDK ZIP 内 LICENSE.txt / ThirdPartyNotices.txt，以及 NuGet 包内 license / notices 保留在工具缓存；Package.ps1 将可用原文复制到开发包 ThirdPartyLicenses。NuGet 顶层聚合包还包含 AI/ML 等传递依赖，未使用这些能力不代表能删除其许可说明。完整分发物许可清点仍须在 T06 复核。

原始开工包未附项目 LICENSE，本轮没有从历史 GitHub 仓库取回许可或代替所有者指定新许可。没有借用 Apple 图片、字体或声音。当前原生窗口使用 Windows 系统字体与 WinUI 控件。署名“派蒙”是制作人信息，不代表 Authenticode 证书身份；开发 EXE 未签名。
