# SDK 参考 · 宿主 0.5.1 / SDK 0.3.0

SDK 在原生内部 WebView 中提供 `window.autumn`。开发套件的 `SDK/autumn-sdk.js`、`autumn-sdk.d.ts`、`message.schema.json` 是交付参考；模板创建会复制所需运行资源。仅支持协议 1 消息，未知能力明确拒绝。不要通过浏览器直接打开 HTML 后把 `HOST_UNAVAILABLE` 误判为应用正常预览。

| 方法组 | 输入与用途 | 权限 |
|---|---|---|
| platform.getCapabilities / lifecycle.getState | `{}`，读取实际能力/生命周期 | 无 |
| permissions.query / permissions.request | `{name}`，查询或请求已声明权限 | 请求 prompt 需原生输入 |
| identity.getProfile / identity.requestProfile | `{}`，最小资料 | identity.profile，且已登录 |
| saves.list / read / write / restore | `{}` 或 slot；write 增加 value 与可选 formatVersion | saves |
| storage.read / write / delete | key；write 增加严格 base64 data | storage |
| preferences.get / set | key；set 增加 JSON value | storage |
| files.pickOpen / pickSave / read / write / close | 系统选择返回不透明 handle；不接受路径 | files.open / files.save |
| appearance.get | `{}`，主题、语言、缩放、减少动态效果 | 无 |
| notifications.show / setBadge | 有限通知或 0–999 角标 | notifications |
| shortcuts.register / widgets.update | 清单已有 ID 与有限纯文本 | shortcuts / widgets |
| links.openInternal | 已声明 action 与有限字符串 arguments | links，需原生输入 |

请求/响应最大 32768 UTF-8 字节，最大 JSON 深度 32；不能用 JavaScript 字符数估计 Unicode 或转义后的消息大小。每实例最多 16 个挂起请求，每分钟 120 次；请求 ID 去重窗口十分钟、最多 2048 个近期/挂起 ID，不提供永久 exactly-once。`timeoutMs` 为 1–120000，默认 30000；本地超时或 AbortSignal 停止等待不等于撤销已提交的写入，重试前读回实际状态。

普通 storage key 与 save slot 限 1–64 位英数、下划线、连字符并拒绝设备名；preferences key 保持 1–59 位并使用安全文件名，允许 CON 等逻辑设备名。TypeScript 的 string 不是运行时范围承诺。saves.write、preferences.set 提交前验证最坏读取信封，storage.write 解码后上限 20 KiB；不能把内部磁盘配额当作可发送字节数。详细边界与恢复见 [数据](data.md)。

`onEvent` 支持 appearance.changed、permissions.changed、identity.changed、links.opened、shortcuts.invoked、lifecycle.changed；旧 `onLifecycle` 仍可用。两类订阅合计最多 64 个，返回函数用于取消订阅。先读快照再订阅，重回前台再次读快照；事件不是可靠持久队列。关闭或账号切换后清理旧等待与订阅。

常见错误有 `AUTH_REQUIRED`、`PERMISSION_DENIED`、`PERMISSION_REVOKED`、`USER_GESTURE_REQUIRED`、`SESSION_EXPIRED`、`SESSION_SUSPENDED`、`REQUEST_TIMEOUT`、`RESPONSE_TOO_LARGE`、`RATE_LIMITED`。错误不能转换成虚假成功；保留用户内容并展示可操作的提示。网络能力仍报告 `networkSandboxVerified:false`，见 [网络边界](network.md)。
