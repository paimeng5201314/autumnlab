# 网络与受控运行 · 0.5.1

AutumnOS 0.5.1 的 `platform.getCapabilities` 明确返回 `networkSandboxVerified:false`。现有 WebView CSP、导航检查、浏览器能力限制和资源过滤不能证明所有原生网络通道均已隔离。普通社区包可被发现、下载或安装，与其获得运行许可是不同阶段；Shell 保留 `CanRunControlledPackage` 门禁。不可通过删除门禁、伪装内置 appId 或弱化安全设置把未验证能力变成“可运行”。

开发预览是用户确认的受控入口，绑定实际包摘要和独立来源，不继承内置应用身份。CLI 的 `offline`/`online` 仅改变这一预览的模拟状态；它们不关闭 Windows 网络，也不是物理网络沙箱测试。模拟账号不会产生真实 token，网页权限声明亦不授予任意外联能力。应用应读取实际能力，对不可用方法处理 `CAPABILITY_UNAVAILABLE`。

完整原生验收须为每个通道建立允许目标与禁止目标，记录实际服务端是否收到请求，而不是只看 JavaScript 报错。矩阵至少包括 fetch、XHR、WebSocket、EventSource、WebRTC、顶层导航和重定向、frame、worker/service worker、wasm、图片/样式/字体资源、弹窗、下载、外部协议及宿主桥接消息来源。还应在后台/挂起、页面重载、取消和实例关闭后检查迟到行为。

宿主自身联网用途应单独核查：依赖恢复使用官方源，商店走受支持 GitHub API 和附件，账号走配置中的可信 OIDC metadata/JWKS/提供方端点，更新走固定来源与签名验证。应用不能把这些宿主通道当作开放代理。网络失败保留失败原因与原数据，不能关闭 TLS、摘要或签名验证继续。

独立业务服务示例仅在本机回环通信，并要求真实资源 token；它不证明内部 `.autumn` 已拥有服务器委托接口，详见 [服务器身份](server.md)。任何“完整社区应用支持”结论必须附精确构建、WebView 版本、各通道正负例、真实网络证据和剩余未执行项。目前本页给出验收要求，未将该矩阵标为通过。
