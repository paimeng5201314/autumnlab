# 09 · 技术依据与仓库核对

本包大部分内容为 AutumnOS 的工程设计要求，而非对已有实现的描述。以下是用于核验技术前提的官方资料；实现时还需核验当时所固定版本的行为。

[S1] Microsoft：Windows App SDK 自包含部署。 .NET 与 Windows App SDK 的依赖分别处理；WinUI 3 原生依赖不是一个普通单文件 EXE。
`https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps`

[S2] Microsoft：.NET 支持政策。本次核验 .NET 10 为 LTS，依赖补丁版本由工程实际锁定，不把运行时版本号当 SDK 版本号。
`https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core`

[S3] Microsoft：WebView2 Runtime 发行。必须部署运行环境；固定版本的更新由应用维护者负责。
`https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution`

[S4] Microsoft：WebView2 安全。验证来源与消息、限制原生能力，不把 Web 内容视为可信宿主代码。
`https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security`

[S5] GitHub：搜索仓库，支持 topic: 查询。
`https://docs.github.com/en/search-github/searching-on-github/searching-for-repositories`

[S6] GitHub：仓库管理员能够自行设置主题标签，因此它不能证明平台审核。
`https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics`

[S7] Logto：第三方 OIDC/OAuth，Native 应用的 PKCE 集成。
`https://docs.logto.io/quick-starts/third-party-oidc`

[S8] GitHub：Release 列表与版本接口。
`https://docs.github.com/en/rest/releases/releases`

[S9] OpenAPI：HTTP API 的机器可读描述标准。
`https://spec.openapis.org/oas/latest.html`

v0.1的历史仓库核对通过用户连接的 GitHub 只读接口完成：默认分支 main，树 SHA `45a26d4e43f01170f7af89fb57386c4bf2c3a6a7`，文件为 LICENSE 与 README.md，读取 Releases 返回空列表。此快照仅作为历史档案，不描述当前本地工作区；v0.3开工检查本地文件，不因此读取远端源码。

本包没有复制原仓库许可证文本，没有更改许可证，也未向仓库提交内容。

[S10] IETF RFC 8252：原生应用授权、系统浏览器、PKCE、回环重定向和Windows绑定注意事项。
`https://www.rfc-editor.org/rfc/rfc8252.html`

[S11] Microsoft：ProtectedData与Windows DPAPI。用户/机器范围需要实际测试，不能将凭据明文迁移。
`https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata`

[S12] OpenAI：AGENTS.md项目规则的读取范围。工程根目录规则与规范文件夹内的规则作用域不同。
`https://developers.openai.com/codex/guides/agents-md`

[S13] OpenAI：Windows原生Codex环境与沙箱。保留权限边界，不能为构建无条件关闭保护。
`https://developers.openai.com/codex/windows`

v0.2补充：用户截图提供Logto公开配置；本次只读探测未得到发现文档（执行环境DNS失败）。客户端注册和真实登录仍未验证。本轮没有重新读取远端GitHub内容。


## v0.3 本地Codex工作方式依据

[S15] OpenAI官方：Codex IDE extension，本地编辑与云端委派是可选择的工作方式。本项目选择本地。
`https://developers.openai.com/codex/ide`（读取时重定向到官方ChatGPT Learn文档）

[S16] OpenAI官方：Codex CLI，从项目目录启动codex并操作本机文件和工具。
`https://developers.openai.com/codex/cli`（读取时重定向到官方ChatGPT Learn文档）

[S17] OpenAI官方：Windows sandbox，Windows原生工作流与权限限制。本机开发不要求关闭保护。
`https://developers.openai.com/codex/windows`（读取时重定向到官方ChatGPT Learn文档）

本地工作流是用户本轮修订；包内v0.1/v0.2的核对记录仅为历史，并非本次重新验证。
