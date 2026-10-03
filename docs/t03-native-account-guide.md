# T03 原生账号与会话接口

制作人：派蒙。支持版本：T03 开发增量；保留 T02 桌面与启动器单实例。实现位于 `src/AutumnOS.Identity`，宿主合同位于 `src/AutumnOS.Contracts/IdentityContracts.cs`。

## 派蒙的控制台与 Windows 操作

配置唯一来源为规范目录的 `config/logto.public.json`。核对当前 Client ID 的 Native/public 类型、Redirect URIs 的 `http://127.0.0.1:17853/callback/`、Post sign-out redirect URIs 的 `http://127.0.0.1:17853/logout-callback/`，全部保留尾斜杠。各字段确认状态看独立 registration-status 文件。不要提交密码、验证码、Client Secret、管理密钥或 refresh token。

运行本次检查包的完整 EXE 目录，在账号系统应用或设置账号页点击「登录并保持登录」，浏览器完成个人认证后返回 AutumnOS。这个明确动作选择受保护的会话保存，并按服务端支持请求离线 scope；选择「仅本次登录」则只请求默认 openid profile，不保存凭据。登录后仍可通过「保持登录（关闭后自动恢复）」立即开启或关闭本地保存，无需更换账号或关闭游戏。

关闭整个程序只停止内存任务，不退出已保存的账号。重开同一目录的 EXE 会从 CurrentUser DPAPI 恢复并校验会话；断网显示离线缓存，失效或被撤销则要求重新认证。服务端不授予 refresh token 时仍可保存现有会话，在有效期内通过 UserInfo 检查恢复，但不能延长凭据有效期。登录后开启保持登录不会偷偷重新认证或新增 scope；需要长期刷新时可明确使用「登录并保持登录」再次认证。主动退出账号或取消保持登录会清除本机保存的凭据，游戏存档保留。

浏览器拒绝、取消或等待超时均不是登录成功。公开配置/发现校验不会把账号变为已登录。不同开发包的数据目录独立；打开新包不会静默搬迁旧账号或存档。

验证登录等待期间再双击 EXE：应只唤回同一窗口，原事务不变，没有第二个 listener。取消后可重新登录；其他程序占用端口时显示 `AUTH_PORT_IN_USE`，自行正常关闭占用程序后重试，不杀进程、不改端口。

账号切换前遵守应用保存/关闭确认，取消确认不触发身份变更。首次登录不自动搬走游客存档。退出客户端会话不删除任何存档。退出 Logto 浏览器会话是另一项明确操作，若返回不完整提示，浏览器会话仍可能存在。

## 宿主合同

| 成员 | 输入及结果 | 取消、并发与归属 |
|---|---|---|
| `Snapshot` | 不可变 `IdentitySnapshot`；无任何 token/raw claims | 当前主实例所有；第三方不得直接读宿主命名空间 |
| `InitializeAsync(ct)` | 恢复受 DPAPI 保护的会话，并尝试真实刷新；未完成验证为 OfflineCached | 幂等同一个初始化任务；Shell 完成前暂缓创建应用账号环境 |
| `SignInAsync(remember, reauthenticate, ct)` | 默认 false/false；重新认证使用 prompt=login；返回最新 snapshot | 单事务、3 分钟；重复调用返回正在登录状态；调用前须完成运行应用关闭确认 |
| `SetRememberSignInAsync(remember, ct)` | 已验证会话即时 DPAPI 保存或清除；不新增 scope、无 token 返回 | 宿主专用，取消在提交前检查；同步受会话锁协调、幂等；不变更账号/epoch/应用实例；迟到刷新须使用最新选择，禁止重建已清凭据 |
| `CancelSignIn()` | 无返回值；发出当前事务取消 | 释放回环并保持未登录；不会关其他浏览器窗口 |
| `RefreshAsync(ct)` | 验证/刷新当前身份，原子替换实际取得的新凭据 | 串行；旧 epoch 结果不能提交；单次预算 45 秒，单 HTTP 15 秒 |
| `SignOutAsync(browserSession, ct)` | false 本地退出；true 额外浏览器退出 | 本地取消旧请求并递增 epoch；浏览器退出最多 2 分钟；不删除存档 |
| `Changed` | 最新安全 snapshot | 可在后台线程触发；UI 必须投递 DispatcherQueue 并读取当前 epoch；不直接广播给游戏 |
| `EnterSessionLease(namespace, epoch)` | 宿主最终提交租约；不匹配抛 SESSION_EXPIRED | 仅同步临界提交，持有期间禁止 await；Dispose 与取得必须同线程；线性化最后写入与账号切换 |
| `Dispose()` | 取消 listener/HTTP/后台刷新，释放宿主引用，保留已保存密文 | 不等于 SignOut；不阻塞 UI，不改变窗口 × 关闭语义 |

`SignedOut / SigningIn / SignedIn / SessionExpired / OfflineCached` 必须显示真实状态。`RememberSignIn` 是用户选择，`CanRefresh` 是已经取得的能力，两者不能混用。SDK profile 必须经过权限服务并转换成 app-scoped identity，不能用宿主 snapshot 充当授权。

## 错误与故障处理

| 固定码 | 含义 / 操作 |
|---|---|
| `AUTH_NOT_CONFIGURED` / `AUTH_CONFIGURATION_ERROR` | 公开配置缺失或无效；检查原配置，不索取 secret |
| `AUTH_PORT_IN_USE` | 精确回环端口被占；未打开浏览器，不杀占用者 |
| `AUTH_BROWSER_UNAVAILABLE` | 系统浏览器未能启动；检查 Windows 默认浏览器 |
| `USER_CANCELLED` | 用户取消或服务端拒绝；游客功能仍可用 |
| `AUTH_TIMEOUT` | 有界事务/网络等待结束；可由用户重试 |
| `OFFLINE` / `AUTH_METADATA_UNAVAILABLE` | 不能完成可信联网验证；缓存明确标记，不能当新认证 |
| `AUTH_PROVIDER_UNAVAILABLE` | 提供方限流、5xx 或暂时不可用；保留受保护会话并显示缓存状态，不当作用户主动退出 |
| `REQUEST_BUSY` / `AUTH_REQUIRED` | 登录事务进行中或没有可保存的已验证会话；不启动第二次登录，不伪造保存成功 |
| `AUTH_INVALID_CALLBACK` / `AUTH_INVALID_TOKEN` / `AUTH_INVALID_RESPONSE` | 协议/签名/claims 校验失败；不返回远程错误全文或秘密 |
| `SESSION_EXPIRED` | token 过期且不能刷新，或 invalid_grant；重新认证 |
| `AUTH_CREDENTIALS_UNREADABLE` | DPAPI 无法解密/格式不正确；保留游戏存档，重新登录 |
| `AUTH_CREDENTIALS_WRITE_FAILED` / `AUTH_CREDENTIALS_UNSAFE_PATH` | 无法安全保存/清除或发现 reparse path；不要降级明文 |
| `AUTH_BROWSER_LOGOUT_INCOMPLETE` | 本地清理完成后浏览器退出失败/未核验；不宣称所有设备已退出 |
| `CAPABILITY_UNAVAILABLE` | 发现文档没有对应端点/能力；不返回假成功 |

不在日志、截图、ZIP 中保留授权码、token、浏览器完整 URL、电子邮箱/完整 claims。需要登录证据时记录时间、构建 ID、状态、匿名化账号标签与固定错误码，不让用户把浏览器认证凭据粘贴到聊天。

## 正反例与验收界限

正确：用户确认关闭运行游戏 → `SignInAsync(false)` → 签名与 UserInfo sub 均验证 → `SignedIn` → 游戏另行请求 `identity.profile`。

错误：发现文档可读就显示“已登录”；把一次授权延伸为所有应用都能读取 profile；将裸昵称或宿主账号 namespace 发给服务器当登录凭据；退出时删除 Saves；请求 offline_access 后无条件显示“持久登录成功”。

`IdentitySessionTests` 使用合成身份检查回环、DPAPI、刷新并发和 epoch；`IdentityProtocolTests` 使用内存 HTTPS transport、临时 RSA 签名和真实本机回环连接执行 Duende 协议适配。自动 fixture 的成功不代替本机真实 Logto 往返、真实刷新、第二账号切换、换机 DPAPI 或生产后台登记。各项真实结果以本轮报告与 AUTUMNOS_PROGRESS.md 为准。

`IdentityPersistenceTests` 专门覆盖关闭/新建身份服务后恢复、无刷新令牌时的有效期限制、登录后开关保持、DPAPI、磁盘故障、迟到刷新与显式退出。刷新已验证但本地写入失败时保留有效内存身份并显示 `AUTH_CREDENTIALS_WRITE_FAILED`，不能谎称已成功持久化。旧文件无法清除时也保留失败提示；后续刷新不得擅自恢复用户已关闭的持久化选择。
