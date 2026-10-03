# ADR 0004 · 原生身份会话与本地凭据

制作人：派蒙。状态：T03 实现决定；真实账号验收与自动 fixture 证据必须分开记录。

AutumnOS 使用现有 `AutumnOS.Identity` 模块，公开配置仍只来自 `autumnos-spec/config/logto.public.json`。不升级 .NET、WinUI 或 WebView2，不新建认证服务器，不以网页 Cookie 或第三方游戏的 WebView 承载平台登录。

固定依赖为 Duende.IdentityModel.OidcClient 7.1.0（Apache-2.0，提供 net10.0 资产）、Microsoft.IdentityModel.JsonWebTokens 8.23.0（MIT）和 System.Security.Cryptography.ProtectedData 10.0.0。依赖锁由本地恢复生成。Duende 提供原生授权码/PKCE 协议，其可插拔 `NoValidationIdentityTokenValidator` 不满足本项目的强制签名要求；本实现明确安装 `SignedIdentityTokenValidator`，使用 Microsoft 的 JWT 验证器，绝不使用不验签适配器。

## 事务与验证

`LogtoIdentityService` 每个宿主一个实例；启动器的既有单实例协议在创建它之前生效。服务内部拒绝创建重叠的登录事务。Shell 在改变账号前取得保存/关闭应用的用户决定，并销毁相关 WebView；身份模块不能越过这个决定。

流程：独占绑定 `127.0.0.1:17853` → 从配置的 MetadataAddress 读取发现文档 → 核验固定 issuer、HTTPS 与端点同源 → 读取受限 JWKS → 为此事务生成新 state、nonce、PKCE verifier/S256 challenge → 系统浏览器 → 严格回环回调 → HTTPS token 交换 → JWT 签名/issuer/audience/有效期/iat/nonce/azp → UserInfo sub 一致 → 提交不可变安全快照。

不请求默认邮箱、手机或业务 resource。默认 `openid profile`；用户明确点击「登录并保持登录」且发现文档声明支持 `offline_access` 才加入该 scope。「仅本次登录」不请求该 scope。2026-10-02 修正原先“仅存在 refresh token 才持久保存”的错误限制：选择保持登录后，实际已验证会话均可用 DPAPI 保存；没有 refresh token 时依旧不能刷新或延长有效期，恢复使用真实 UserInfo 检查。界面必须区分持久化选择与 `CanRefresh`。

登录后 `SetRememberSignInAsync` 可立即保存/忘记当前会话，不新增授权范围、浏览器事务或账号 epoch；任何正在等待的刷新，在持锁提交时读取最新持久化选择，不能用请求发起时的旧选择重新写入已清凭据。正常 Dispose 只停止内存任务；显式退出才清除会话。取消保持登录仅清本地密文，当前内存身份继续有效。

所有反向通信采用 HTTPS、原始系统 TLS 验证、禁止 HTTP 重定向、同源边界、15 秒 HTTP 超时及 128 KiB 响应限制，不使用商店代理。JWT 只接受发现文档与本地非对称算法交集，最多 32 个签名密钥，32 KiB token。允许 30 秒时钟偏差。刷新 token 若带新 ID token，必须再次验签并符合原 subject；nonce 若出现必须符合原事务。刷新不带 ID token 时以可信 token 端点结果和 UserInfo 的相同 sub 验证，不把任意裸 sub 当成凭据。

## 回环边界

原生 TCP 监听器只绑定精确 IPv4 回环和固定端口，启用 ExclusiveAddressUse；不需要 URL ACL 管理员登记，不监听公网或 `0.0.0.0`，不杀占端口程序，不自动换端口。

登录预算 3 分钟，单连接读取 3 秒、头部最多 8 KiB、目标最多 6 KiB、最多 8 个唯一参数。只接受 HTTP/1.1 GET、精确 Host 与尾斜杠路径、无正文、预期事务 state，以及一个 code 或 error。错误转义、重复字段、控制字符、错误路径、未知参数或不匹配 state 不消耗事务。多次无效请求后每次限速 250 ms。正确回调仅消费一次；取消/结束/失败均释放监听器。页面只有固定文字、no-store、严格 CSP，无外部资源，无 code/token/完整查询串；收到回调不等于登录成功，页面要求返回客户端查看结果。

这不能阻止同一 Windows 用户下的恶意进程占端口或读取浏览器进程信息；PKCE、state、nonce 与独占端口减少截获影响，但不是同用户恶意软件沙箱。

## 凭据与持久化

2026-10-02 本机真实 WinUI 验证发现 NTFS 工作目录的继承权限允许写入，却不允许冗余重写已属于当前用户的所有者。旧 SetOwner 请求造成 0x80070005，使认证成功后的加密保存失败。现只提交禁止继承、当前用户 FullControl 的 DACL，不请求更改所有者；DPAPI CurrentUser、路径防护与原子替换全部保留。新增 Modify 父目录回归，不以 Temp 目录通常较宽的权限代替发行目录实测。

只有身份模块持有原始 token、issuer 和 subject。DPAPI CurrentUser 加密后的字节写入当前发行目录自己的 `AutumnOS_Data/Identity/session.dpapi`，附加 entropy 绑定产品协议、Authority 和 ClientId。目录/文件 ACL 限制到当前用户；拒绝父目录及目标 reparse point；写入随机临时文件并 Flush(true) 后原子替换。明文只在宿主内存短暂序列化，字节缓冲在加密后清零；托管字符串的生命周期不能保证物理清零。

本地退出先递增 epoch、取消旧操作、清除内存，再写入并 fsync 不含秘密的 `signed-out.marker`，随后删除加密文件。读取阶段见 marker 就禁止恢复，即使上次删除被中断；新的成功凭据替换后才清除此 marker。无法写 marker/清理时报告 `AUTH_CREDENTIALS_WRITE_FAILED` 与会话失效，不声称本地退出完整成功。凭据不可解密时需要重新登录，保留损坏文件供用户决定，绝不删除 Saves、Packages 或普通配置、不回退明文。

刷新通过 SemaphoreSlim 串行，提交前再次核验 epoch 与取消令牌；即使测试替身无视取消，退出后到达的结果也不能恢复旧账号或写回旧 token。每 30 秒检查临近过期的会话，网络失败保持明确 OfflineCached，invalid_grant 等验证失败转 SessionExpired 并清理可刷新凭据。不会把缓存资料冒充新认证。

## 数据身份与生命周期

宿主账号命名空间为 `account-` + lowercase SHA256(精确可信 issuer + LF + 已验证 sub)，游客为 `guest`。这是本地命名空间，不是对外用户标识或服务器凭证；昵称、邮箱不参与。发行目录间不会自动复制数据；同 issuer/sub 在协议 v1 下得到同一名称，但路径仍属于启动主实例自己的 Data 根。改 issuer 或身份迁移需显式迁移流程，不根据昵称合并。

`IdentitySnapshot` 只含状态、命名空间、代次、最小显示名/HTTPS 头像 URL、保持选择、实际刷新能力和固定错误码。应用 SDK 必须再通过逐应用资料授权，并由宿主为应用生成与来源绑定的用户 ID。宿主 snapshot 不直接广播给第三方；头像 URL 也不是可信 HTML。

登录、退出或切换递增 sessionEpoch；取消旧异步操作并拒绝迟到提交。本模块不直接修改 WebView Cookie/localStorage/IndexedDB/缓存；Shell 必须关闭或安全隔离整个旧账号运行环境，并使用新命名空间创建应用。这项集成的验收单列，不能仅凭身份单元测试声称完成。

## 本地退出与浏览器退出

本地退出不删除游戏和存档。浏览器退出在本地清理后，使用发现的 end_session_endpoint、原 ID token hint、随机退出 state 和固定 `/logout-callback/` 回环；退出预算 2 分钟。未登记退出回调、网络失败或用户取消时返回 `AUTH_BROWSER_LOGOUT_INCOMPLETE`，本地仍保持退出，不宣称所有设备退出。登录回调登记、Native 类型、退出回调登记、一次真实登录/刷新/退出各自记录证据。

## 官方资料与核验范围

- [Duende 手动原生协议流程](https://docs.duendesoftware.com/identitymodel-oidcclient/manual/)
- [Duende 原生 OIDC 客户端与 Apache-2.0](https://docs.duendesoftware.com/identitymodel-oidcclient/)
- [Duende 日志字段与 UserInfo sub 检查](https://docs.duendesoftware.com/identitymodel-oidcclient/logging/)
- [固定 Duende 包与 net10.0 依赖](https://www.nuget.org/packages/Duende.IdentityModel.OidcClient/7.1.0)
- [固定 Microsoft JWT 验证库](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens/8.23.0)

本地研究包的 XML API 文档用于核对实际签名，业务使用 NullLoggerFactory，避免库 trace/debug 输出原始 claims/协议对象。自动测试中的 issuer、RSA 密钥、code、token 均为独立 fixture，不连接真实 Logto，不算真实用户登录证据。
