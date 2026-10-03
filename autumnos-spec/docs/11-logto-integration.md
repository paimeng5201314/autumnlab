# 11 · Logto 实际配置与桌面登录接入

**Lab Chronicles AutumnOS · 制作人：派蒙 · 配置补充版本：0.2**

本页在原有功能范围上补入用户提供的公开配置。它是待实现的集成合同，不是已经完成的登录实现。截图没有展示应用类型、回调登记或一次真实登录的结果。

## 1. 来自截图的公开配置

| 字段 | 实际值 |
|---|---|
| Logto Endpoint | `https://account.labchronicles.cn/` |
| Issuer / OIDC Authority | `https://account.labchronicles.cn/oidc` |
| Application ID / Client ID | `tjck5m8ohjkw272y42adv` |
| Discovery / MetadataAddress | `https://account.labchronicles.cn/oidc/.well-known/openid-configuration` |
| JWKS URI | `https://account.labchronicles.cn/oidc/jwks` |
| Authorization endpoint | `https://account.labchronicles.cn/oidc/auth` |
| Token endpoint | `https://account.labchronicles.cn/oidc/token` |
| UserInfo endpoint | `https://account.labchronicles.cn/oidc/me` |

客户端ID共21个字符；`8` 后是小写字母 `o`。这些值可以放入客户端公开配置，不是密码或令牌。不得因界面写了“端点和凭据”就要求用户把账户密码、client secret 或管理API凭据提交进仓库。

**Endpoint 和 Authority 不可混用。** 本方案按截图固定预期 issuer；OIDC库使用明确的 MetadataAddress，不能把 `/oidc` 重复拼接，也不能为解决 issuer mismatch 关闭 issuer 校验。读取可信发现文档后使用其中的授权、token、UserInfo、JWKS和退出端点，核对 issuer 与端点信任边界，避免在各处散落硬编码URL。[S7][S10]

本次仅尝试公开发现地址的只读GET；执行环境发生DNS解析失败，没有得到线上发现文档。没有验证该App ID的类型或已登记回调。让实际开发环境重新读取，不把本次环境问题描述为服务器故障。

## 2. Logto控制台需要核对的项目

这是供AutumnOS自身使用的 **Native原生 / public client**。它不是一个能在服务器安全保管共享密钥的Traditional Web客户端。采用授权码+PKCE，不嵌入client secret。[S7][S10]

确认截图里的应用是Native。若当前类型不符合且不能直接调整，应在控制台建立正确类型，并把新的公开Client ID同步到配置和文档；不要通过把Web客户端密钥写进EXE来“修好”。用户是否把它归为第一方或第三方，是另一项管理属性，不应因为引用了第三方接入文档就强迫重建为第三方应用。

本开工包新设以下回调配置，**它们不是从截图读取到的已登记值**：

| Logto字段 | 本方案登记值 |
|---|---|
| Redirect URIs / 重定向URI | `http://127.0.0.1:17853/callback/` |
| Post sign-out redirect URIs / 退出后重定向URI | `http://127.0.0.1:17853/logout-callback/` |

必须保留完整路径和末尾斜杠，并让控制台与代码一致。不要把它们设置成授权端点、Issuer或Token端点。回调位于用户自己的电脑，不需要在公网服务器部署这两个页面，也不需要向外网开放17853端口。它不涉及将GitHub代理转发到登录平台。[S10]

先使用上述固定端口作为可复现联调基线。开始登录前独占绑定回环监听；端口被占用时返回明确错误，不结束其他程序、不提权、不在未验证登记匹配规则时偷偷换端口。后续若启用动态端口，要按RFC 8252与当前Logto部署验证匹配行为，形成ADR；不能仅凭另一类Logto客户端的文档推断本应用已支持。多实例登录必须串行协调。

控制台确认记录保存在 `config/logto.registration-status.json`。仅在用户确认或真实测试有证据后更新状态。不能把“配置文件已经填写”写成“登录验收通过”。

## 3. 配置文件与库适配

`config/logto.public.json` 是AutumnOS拟定的配置模型，不是声称某个现成SDK原生支持这些字段。实现有类型的Options、启动校验和库适配层；将必要公开值并入应用实际配置，保持单一来源。不得把“登记状态JSON”当成运行时身份认证证明。

优先采用仍维护、许可可接受的桌面OIDC公共客户端库，核验当前官方用法与目标框架兼容性并固定版本。不要把ASP.NET的服务端cookie认证示例不加修改地搬进WinUI桌面程序，也不要自己编写一套省略验证的JWT解析器。

默认scope为 `openid profile`。用户明确选择保持登录时，再请求 `offline_access` 并按服务端实际授权处理；它不是保证一定拿到刷新令牌的开关。不默认请求邮箱、手机号、管理API或所有资源权限。没有声明业务API资源时，不杜撰 audience、resource或业务后端地址。

## 4. 正常登录流程

由用户操作触发登录，游客和离线使用不被循环弹浏览器打断。每次登录有独立请求ID、不可预测的state、nonce与PKCE verifier；使用S256。不得复用其他登录请求的参数。[S10]

先建立临时回环监听，再通过系统默认浏览器打开经校验的授权URL。登录不放在承载第三方应用的WebView2里，不读取用户密码。监听仅绑定127.0.0.1，并在需要支持IPv6时单独验证对应注册方式；绝不监听0.0.0.0、局域网地址或通配符。

回调校验HTTP方法、精确路径、参数数量、长度、state与当前等待中的请求，拒绝过期、重复和不匹配的回调。只有通过验证的回调能推进登录；异常请求不能用来覆盖当前请求的状态。约定超时、取消和异常请求限流，并可靠释放监听器。Windows socket实现应评估独占地址绑定。[S10]

令牌交换由宿主使用HTTPS与PKCE verifier完成。不用client secret，不关TLS证书验证。由合规库验证ID token的签名、issuer、audience、时效、nonce及适用的其他约束；不要把“JWT能解码”当作登录成功。读取UserInfo时校验其sub与已验证身份一致。

回调页不显示授权码、令牌或完整查询串；不加载外部脚本、图片、分析SDK，不把认证资料写入日志。验证完成后仅呈现“可以返回AutumnOS”等状态，再关闭临时监听。不得声称能在所有浏览器自动关掉用户的页面。

## 5. 会话、凭据、离线和退出

凭据只由宿主身份模块保管，禁止进入第三方页面、SDK返回值、localStorage、普通配置、剪贴板、崩溃报告和明文日志。需要落盘时使用Windows凭据保护方案，如CurrentUser范围DPAPI，并设置适当访问控制。此类静态加密不等于对同一Windows用户下任意恶意程序形成完整沙箱。[S11]

便携迁移的游戏和存档可以保留；不要承诺登录凭据可随ZIP在任意电脑自动使用。凭据无法解密时安全清理或隔离凭据并要求重新登录，不删除游戏和存档，不降级成明文。

刷新操作串行协调；支持服务端实际刷新策略与原子替换。会话失效、invalid_grant和离线要区分。网络失败时可展示明确标记的缓存资料，但不能向业务服务器声称已重新验证身份。

账户切换增加sessionEpoch，废弃旧请求及订阅，切换应用与用户数据空间。不能让旧账号稍后返回的异步结果覆盖新账号。无法安全切换某运行实例时先按保存流程关闭，不能混用WebView2的持久化状态。

“退出本客户端”和“退出Logto浏览器会话”分开描述。本地退出立即停止刷新、清理本地会话与受控缓存，不删除存档。需要服务端退出时读取发现文档的end_session_endpoint，使用正确参数、已登记的PostLogoutRedirectUri和退出state。支持的撤销流程以实际服务端能力为准。离线或退出失败时如实说明浏览器会话可能仍有效，不假称所有设备均已退出。

## 6. 给第三方应用的资料接口

沿用 `docs/03-interfaces.md`：应用必须声明并获得identity.profile授权，宿主基于真实运行实例识别应用，而不是相信消息中的appId。默认仅提供应用需要的用户标识、昵称和头像；不把完整claims或任何宿主令牌传出。

Logto对客户端的登录授权与AutumnOS对内部应用的资料授权是两层机制，前者成功不自动批准后者。第三方自己的服务器验证身份属于独立协议，不能拿这里的昵称、客户端ID或裸userId当服务器登录证明。

## 7. 错误合同

沿用现有错误码并补充明确诊断：AUTH_NOT_CONFIGURED、AUTH_CONFIGURATION_ERROR、AUTH_REQUIRED、USER_CANCELLED、AUTH_TIMEOUT、AUTH_CALLBACK_PORT_IN_USE、AUTH_CALLBACK_INVALID、AUTH_METADATA_UNAVAILABLE、AUTH_TOKEN_INVALID、SESSION_EXPIRED、OFFLINE。

敏感验证错误在用户界面给出安全摘要和关联ID；脱敏诊断区可报告失败环节，不含密码、授权码、PKCE verifier、令牌或完整回调URL。没有截图以外的配置时不能生成假用户。

## 8. 验收与交付

A030必须覆盖真实登录与返回、取消、拒绝、超时、回调路径错误、端口占用、错误state/nonce、错误issuer/audience/签名、重复回调、元数据不可达、过期会话、记住登录与刷新失效。A033覆盖切换账号的迟到响应与数据隔离。日志扫描确认认证材料不泄露。

同时提供登录模块配置说明、控制台登记步骤、身份/授权时序图、错误码、SDK资料读取示例、账号切换示例及Windows复现步骤。使用测试身份替身是单元测试，不算真实Logto验收。

公开配置检查脚本只校验本包字段与预设合同，不连接Logto，不证明App ID存在，也不验证回调登记。真实登录仍需在Windows上由用户完成系统浏览器往返测试。
