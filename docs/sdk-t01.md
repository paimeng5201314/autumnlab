# AutumnOS T01 SDK 0.1.0

制作人：派蒙。适用于当前本地原生 WinUI/WebView2 技术验证版本；协议版本为 `1`。本页描述已经实现的最小接口，完整账户、商店、网络、SDK CLI 和发布能力仍按后续阶段实施。

## 接入与真实样例

把 `sdk/autumn-sdk.js` 放入应用包根目录，在 HTML 使用外部 `<script src="autumn-sdk.js" defer></script>`，再载入自己的外部脚本。不得依赖内联脚本、CDN、远程字体。类型文件为 `sdk/autumn-sdk.d.ts`；清单与消息定义为 `sdk/manifest.schema.json`、`sdk/message.schema.json`。schema 不替代宿主对 ZIP、路径、消息大小、来源和重复 JSON 键的检查。

在工程根执行 `./scripts/New-SamplePackage.ps1`，实际样例输出为 `artifacts/samples/element-pairs.autumn`。启动构建出的 `AutumnOS.exe`，使用内置样例安装/启动按钮。当前仅开放自带样例入口，未声称可安全运行任意第三方包。

`.autumn` 是 ZIP 容器：根目录 `manifest.json`，HTML 入口和本地资源。清单字段严格为 `schemaVersion/appId/name/version/runtime/entry/permissions`，例见 `samples/element-pairs/manifest.json`。版本为 SemVer；runtime 仅 `web`；权限为空或 `["saves"]`。appId 为小写反向域名，最多 80 字符、每段最多 63，首段不得为 Windows 设备名。入口必须为已存在的安全相对 HTML 路径；样例使用 `index.html`。禁止绝对路径、路径穿越、链接、同名大小写冲突、宿主可执行文件。归档最大 20 MiB、最多 128 条目、单文件最大 8 MiB、展开总量最大 16 MiB；安装器还拒绝异常压缩比。

## 请求与方法

```js
const capabilities = await window.autumn.request("platform.getCapabilities");
const state = await window.autumn.request("lifecycle.getState");
const unsubscribe = window.autumn.onLifecycle(state => {
  // background 仍是存活游戏；暂停自己的动画和输入，恢复时重新查询状态。
});
// 以下请求应置于用户点击保存/读档的处理程序中。
let permission = await window.autumn.request("permissions.query", { name: "saves" });
if (permission.state === "prompt") {
  permission = await window.autumn.request("permissions.request", { name: "saves" });
}
if (permission.state === "granted") {
  await window.autumn.request("saves.write", { slot: "game", value: { formatVersion: 1, score: 10 } });
}
```

| 方法 | 精确 params | 成功 result | 权限与行为 |
|---|---|---|---|
| `platform.getCapabilities` | `{}` | `protocolVersion:1, capabilities:["lifecycle","permissions","saves"], accountMode:"guest", networkSandboxVerified:false, limits` | 只读；limits 含消息字节数、并发、每分钟请求数、宿主超时、去重容量和保留时间 |
| `lifecycle.getState` | `{}` | `{state,blocksMaintenance}` | 仅当前实例，挂起期间仍允许 |
| `permissions.query` | `{name:"saves"}` | `{name:"saves",state:"prompt"/"granted"/"denied"/"revoked"}` | 必须已声明；不弹窗 |
| `permissions.request` | `{name:"saves"}` | `{name:"saves",state:"granted"/"denied"}` | 用户动作后申请；拒绝后同一会话不再弹窗；撤销后报错 |
| `saves.read` | `{slot:"game"}` | `{exists:boolean,value:JSON}` | 必须授权；不存在时 `exists:false,value:null` |
| `saves.write` | `{slot:"game",value:JSON}` | `{saved:true}` | 必须授权；宿主成功提交后才返回 |

字段大小写敏感，以上参数不接受额外字段。未知方法返回 `CAPABILITY_UNAVAILABLE`。SDK 原始 `request` 允许未知方法，便于能力探测；不能把无类型调用当作该能力已实现。

## 传输、超时和重试

SDK 经 `window.chrome.webview.postMessage` 发送对象 `{protocolVersion:1,requestId,method,params}`，宿主返回 `{requestId,ok:true,result}` 或 `{requestId,ok:false,error:{code,message,retryable,correlationId}}`。解析失败等无法关联的请求可返回 `requestId:null`。SDK 仅接受匹配待决请求 ID 的响应；来源、appId、实例和会话代次由宿主绑定，不是页面可声明的参数。

宿主上限：请求 32 KiB UTF-8、JSON 深度 32、16 个待处理请求（实际串行执行）、滚动 60 秒最多 120 请求。SDK 独立限制 16 个待决请求和滚动 60 秒 120 次发送；宿主不信任 SDK 检查，会再次执行限制。速率超限返回可重试的 `RATE_LIMITED`，没有自动排队或重试。无效参数不占 SDK 发送额度，已送出但超时/取消的请求仍占额度；宿主对通过格式检查的重复请求也计入频率。

不再设置会话累计 4096 次上限。SDK 使用文档随机前缀和单调 BigInt 序号生成 ID，已完成请求立即释放客户端待决容量，不保留所有历史 ID；不会因为 Number 精度丢失重用序号。协议 ID 最多 64 字符。SDK 本地 JSON 检查更保守，拒绝非有限数、undefined、循环等不能忠实编码的值。

宿主在请求入队前预留 ID，拒绝相同的待处理 ID；待处理 ID 不随时间淘汰。请求结束后（包括错误/超时）保留 ID 10 分钟，之后在新请求或完成请求时回收。`limits.maximumRememberedRequestIds=2048` 表示待处理与近期完成 ID 的总容量，`limits.deduplicationWindowMs=600000` 表示完成后的保留时间，不是应用一生的调用数量。保留未过期记录，达到容量时返回可重试的 `REQUEST_BUSY`，不会为接收新请求提早丢弃旧记录。单调时钟避免系统时间校正提前清空速率或去重记录。

应用调用方始终应使用新 ID。10 分钟去重窗口之外，手动重发旧 ID 可能再次执行，因此不提供永久 exactly-once 或独立幂等键。待处理和窗口内重放返回 `DUPLICATE_REQUEST`，不返回缓存结果；不能据此推断原操作是否提交。更换 ID 也不等于写操作自动具备幂等性。

`request(method, params, {timeoutMs,signal})` 的客户端超时默认 30000 ms，可设整数 1–120000。宿主默认 30000 ms，独立于客户端等待时间；`limits.requestTimeoutMs` 返回当前值。客户端延长等待不会延长宿主操作。已发送请求超时或 AbortSignal 取消，只停止客户端等待；不发送取消命令，不保证保存未发生，不自动重试。`REQUEST_TIMEOUT` 同样不能证明关键提交未发生。写入结果不明时先读档核对，再由用户决定是否重试。

SDK 的本地错误为 `AutumnSdkError`，具有 `code/message/retryable/correlationId`；本地错误 correlationId 为 null，宿主错误为诊断 ID。常见本地错误：`HOST_UNAVAILABLE`、`INVALID_ARGUMENT`、`MESSAGE_TOO_LARGE`、`TOO_MANY_REQUESTS`、`RATE_LIMITED`、`TIMEOUT`、`USER_CANCELLED`、`TRANSPORT_ERROR`、`INVALID_RESPONSE`、`SESSION_EXPIRED`。本地 `TOO_MANY_REQUESTS/RATE_LIMITED` 标记 retryable，其余本地错误不自动推断可安全重试。

宿主错误包括 `SOURCE_REJECTED`、`INVALID_REQUEST`、`PROTOCOL_UNSUPPORTED`、`DUPLICATE_REQUEST`、`REQUEST_BUSY`、`RATE_LIMITED`、`REQUEST_TIMEOUT`、`SESSION_EXPIRED`、`SESSION_SUSPENDED`、`PERMISSION_NOT_DECLARED`、`PERMISSION_DENIED`、`PERMISSION_REVOKED`、`CAPABILITY_UNAVAILABLE`、`SAVE_CORRUPT`、`SAVE_TOO_LARGE`、`SAVE_BUSY`、`STORAGE_UNSAFE_PATH`、`STORAGE_IO_ERROR`。只有 `SAVE_BUSY/STORAGE_IO_ERROR/REQUEST_BUSY/RATE_LIMITED` 标记 retryable；仍不自动重试写入。

## 生命周期、身份与数据

宿主事件为 `{event:"lifecycle.stateChanged",state}`；状态为 starting、foreground、background、suspended、closing、closed、crashed。`onLifecycle` 返回取消订阅函数，订阅时不会合成初始事件；启动和恢复后调用 `lifecycle.getState` 获取快照。后台游戏仍阻塞维护；挂起时除生命周期查询外拒绝请求。关闭/崩溃或页面卸载后 SDK 拒绝待决与新请求；宿主独立拒绝失效会话。挂起或权限撤销使先前授权提示的迟到结果无效。

当前仅游客模式，不提供账户登录、资料、用户令牌、账号切换或第三方服务器凭证。授权仅当前运行会话有效，不代表永久授权。私有存档由宿主放在 `AutumnOS_Data/Saves/<appId>/guest/game.json`，应用只能使用逻辑槽位 `game`，不能访问磁盘路径。宿主记录包含 `schemaVersion/appId/accountMode/slot/value`，原子提交，包安装不删除存档，关闭不删除存档。记录最大 128 KiB；写请求同时受较小的 32 KiB 消息限制。

元素配对的 value 格式是 `{formatVersion:1,nickname,deck,matched,moves}`。载入先验证版本、6 组元素、索引、成对集合和步数，再替换内存状态；保存前也检查已有样例格式，未知/损坏记录不自动覆盖。当前没有自动迁移、备份恢复或清除数据接口。昵称输入不接管 IME 按键；原生输入法的实际验收由窗口测试报告记录，不能由 Node 测试代替。

## 验证与限制

执行 `node --test tests/sdk-tests.cjs` 验证真实 SDK 的关联响应、拒绝、超时、取消、并发、生命周期、无宿主失败及 JSON 边界。长会话用模拟单调时钟连续完成 12000 次真实 SDK 调用，检查 ID 唯一、旧响应隔离和频率窗口恢复；不是持续两小时的 Windows 压力测试。C# `RuntimeTests` 同样用注入的单调时钟完成 12000 次网关调用，检查近期 ID 内存有界、权限/真实存档仍有效、10 分钟过期边界与待处理重放。默认运行时仍使用系统单调时钟，超时继续由真实取消计时器执行。

传输替身仅存在于测试。schema 测试覆盖解析和核心正反例，不是完整 JSON Schema 引擎验收；TypeScript 编译验收未执行时保持 `not_run`。

元素配对完全本地，无外部资源、网络请求、浏览器持久化或内联脚本。Shell 施加 CSP 和资源/导航限制，当前 capabilities 明确返回 `networkSandboxVerified:false`；这不是完整浏览器网络隔离认证。第三方通道、触控硬件、中文 IME、崩溃/缩放等最终结果以本地原生测试记录为准。

自动化目标：昵称 `#nickname`（名称“玩家昵称”）；保存 `#save-button`（“保存进度”）；读取 `#load-button`（“读取存档”）；重开 `#restart-button`（“重新开始 ↻”）；卡片 `#card-0` 至 `#card-11`，具有逐张动态 aria-label；结果 `#save-status` 与 `#round-status`。
