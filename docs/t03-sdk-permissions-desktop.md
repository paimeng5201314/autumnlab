# T03 SDK：资料、权限、数据及桌面扩展

制作人：派蒙。SDK 0.3.0，消息协议仍为 1。T01/T02 的 `request`、`onLifecycle` 与长会话保护兼容；此页对应当前 C# 服务、`sdk/autumn-sdk.d.ts` 和 `sdk/message.schema.json`。Windows 实测状态以当前构建报告为准。

## 所有接口的共同约定

只在内部受控 WebView 中调用 `window.autumn`。每条请求仅包含 protocolVersion/requestId/method/params；实例、账号、代次、安装来源由宿主绑定。禁止 appId/userId/sessionEpoch/path 等额外定位字段。所有列出的参数对象拒绝额外字段。未接入的 `identity.beginAppSession`、网络能力、外部协议等返回 CAPABILITY_UNAVAILABLE，不模拟成功。

最大请求 32768 UTF-8 字节，深度 32；响应也限制 32768 字节，超限 RESPONSE_TOO_LARGE。本次修订的桥接 JSON 使用 UTF-8 友好编码，仅通过 PostWebMessageAsJson 发送，不拼入 HTML/可执行脚本；saves.write 与 preferences.set 在提交前按完整未来读取响应（含最坏 64 字符 requestId）预检预算，拒绝时保留旧数据。每实例最多 16 个挂起请求、滚动一分钟 120 次，默认宿主超时 30 秒；宿主可配置至 120 秒。SDK `timeoutMs` 为 1–120000，只停止调用者本地等待；AbortSignal 同样不撤销已经提交的写入。切换账号、关闭应用、宿主超时取消实际宿主工作。调用者收到 TIMEOUT/USER_CANCELLED 后要读取实际状态，不盲目重试写入。

SDK 单调 BigInt ID 不设累计 4096 限制；宿主保留最多 2048 个待处理及近期已完成 ID，完成后去重十分钟，窗口到期不保证 exactly-once。保存是原子替换，不代表多次业务点击天然幂等；应用应自己保存业务操作 ID，重试前读回确认。通知另有下述有限去重。

权限查询只读。敏感动作按下表检查清单声明和授权；背景应用可使用已授权的普通接口，挂起实例只可读 lifecycle.getState。新弹窗和文件选择/内部链接需要宿主观察到的当前前台原生输入，两秒内一次消费。拒绝持久化；脚本重复申请不能重开弹窗。先读初始快照，再订阅事件；重回前台再次读快照，不假定事件永不丢失。

账号、应用来源与存档迁移规则见 ADR 0006 和 `t03-storage-and-migration.md`。所有数据方法都绑定当前账号；游客不会在首次登录时静默迁入账号。退出不删除存档。开发 ZIP 排除整个 AutumnOS_Data。

## 资料及权限

| 方法 | params | result | 权限与失败 |
|---|---|---|---|
| platform.getCapabilities | `{}` | 实际已接线能力、账号模式及限制 | 无；networkSandboxVerified 仍 false |
| lifecycle.getState | `{}` | `{state,blocksMaintenance}` | 无；与后台继续/结束规则共用真实实例 |
| permissions.query | `{name}` | `{name,state}`，state=prompt/granted/denied/revoked | 权限必须在清单声明 |
| permissions.request | `{name}` | 同上 | prompt 时需要原生输入；展示应用、来源、用途和字段 |
| identity.getProfile | `{}` | `{appScopedUserId,displayName,avatarUrl,isCached}` | 已登录且 identity.profile 已允许 |
| identity.requestProfile | `{}` | 同上 | 登录和资料授权分开；不会自动打开浏览器登录 |

支持的权限名：saves、identity.profile、storage、files.open、files.save、notifications、shortcuts、widgets、links。最小资料不含邮箱、手机号、claims 或令牌；avatarUrl 仅 HTTPS，不能验证为 HTTPS 时返回 null。isCached=true 表示宿主的真实离线缓存状态。appScopedUserId 是本地资料关联标识，绝非服务器登录凭证。正常读取不会触发登录循环。

```js
// 放在实际按钮的 click 回调中；宿主独立观察原生输入，不接受 userGesture: true。
try {
  const profile = await autumn.request('identity.requestProfile', {});
  document.querySelector('#name').textContent = profile.displayName;
} catch (error) {
  if (error.code === 'AUTH_REQUIRED') showMessage('请先在 AutumnOS 账号中登录，仍可继续游客游戏。');
  else if (error.code === 'PERMISSION_DENIED' || error.code === 'PERMISSION_REVOKED') showMessage('资料不可用，继续游客显示。');
  else showMessage(error.code);
}
// 反例：await autumn.request('identity.getProfile', { userId: 'someone-else' }); → INVALID_REQUEST
```

## 存档、私有文件和配置

| 方法 | params | result |
|---|---|---|
| saves.list | `{}` | `{slots:[{slot,formatVersion,revision,bytes,hasBackup}]}` |
| saves.read | `{slot}` | `{exists,value}`；不存在 value=null |
| saves.write | `{slot,value,formatVersion?}` | `{saved:true}`；默认格式 1 |
| saves.restore | `{slot}` | `{restored:true}`；验证并恢复当前账号应用的备份 |
| storage.read | `{key}` | `{exists,data,encoding:'base64'}`；data 可为 null |
| storage.write | `{key,data}` | `{written:true}`；data 为严格 base64 |
| storage.delete | `{key}` | `{deleted:boolean}`；仅指定私有键 |
| preferences.get | `{key}` | `{exists,value}` |
| preferences.set | `{key,value}` | `{saved:true}` |

saves.* 需要 saves；storage.* 与 preferences.* 需要 storage。普通逻辑键/槽位为 1–64 个英数/下划线/连字符，不允许路径或 Windows 设备名；preferences 键上限仍为 59，使用独立物理目录与安全文件名，因此逻辑 CON 等设备名仍可作为偏好键。旧私有存储中的 `pref_` 记录一次性迁移并保留字节，之后原始存储的同名 key 不覆盖偏好；损坏记录返回 PREFERENCE_CORRUPT，迁移冲突返回 PREFERENCE_MIGRATION_CONFLICT。应用内容格式版本为 1–1000000，与宿主记录 schema 版本分开；应用必须检查读出的格式并显式转换，修改版本数字不会自动转换内容。宿主可信迁移 API 与备份/回退见存储文档。

读取文件的 SDK 适配上限为 20 KiB（为 base64 和信封预留空间）；storage.write 同样限制解码后 20 KiB，超出返回 FILE_TOO_LARGE 并保留旧记录。私有存储内部还有 1 MiB/文件、16 MiB/账号应用、128 文件、32 槽位、128 KiB/存档等更高内部限额；这些不是允许通过 32 KiB 网关发送超限消息。大文件/分块协议未接入时明确报错，不返回截断内容。

失败保留旧提交及安全备份。跨账号写入、过期代次、撤销后排队写入在最终提交前重新检查；提交期间持有权限/实例/身份租约及关键写入协调。这个写入协调是未来更新交接输入，不等于 T05 已完成。

```js
await autumn.request('saves.write', { slot: 'game', value: { format: 1, pairs: 4 } });
const save = await autumn.request('saves.read', { slot: 'game' });
// 反例：slot:'../../other-account/game' 或 storage key:'C:\\private' 被拒绝。
```

## 系统文件选择器

| 方法 | params | result | 权限 |
|---|---|---|---|
| files.pickOpen | `{}` | `{handle,name,bytes,expiresUtc}` | files.open＋原生输入＋系统选择 |
| files.pickSave | `{}` | 同上，登记不会先截断目标文件 | files.save＋原生输入＋系统选择 |
| files.read | `{handle}` | `{data,encoding:'base64'}` | files.open，句柄属于当前账号、实例及来源且有效 |
| files.write | `{handle,data}` | `{bytes,backupRetained}` | files.save，写句柄一次消费，原子替换且保留原目标备份 |
| files.close | `{handle}` | `{closed:boolean}` | 句柄对应的 files.open 或 files.save |

句柄是不透明随机值，只能从当前应用的真实选择器获得；不是文件路径。宿主验证所属来源、实例、账号、会话代次、授权、有效期；生命周期最长十五分钟，每 broker 最多 64 个句柄。读取 SDK 限 20 KiB；底层 broker 的 8 MiB 上限不是公开传输承诺。文件选择器取消返回 USER_CANCELLED；撤销、应用关闭或账号切换后不能继续使用。外部链接和任意命令不可通过选择器执行。

## 通知、角标与桌面扩展

这些是 AutumnOS 内部服务，不是 Windows 外部协议注册或全局系统通知。只接受安装清单 `desktop` 中声明的有限动作和数据，不能指向其他应用或宿主命令。

| 方法 | params | result | 条件 |
|---|---|---|---|
| appearance.get | `{}` | `{theme,language,scale,reduceMotion}` | 无敏感权限；取真实宿主初始快照 |
| notifications.show | `{id,title,body,action}` | `{shown:true,id}` 或 `{shown:false,reason:'muted'/'duplicate'}` | notifications；action=null 或本应用声明的 links 动作 |
| notifications.setBadge | `{count}` | `{count}` | notifications；整数 0–999，0 清除；静音时始终 0 |
| shortcuts.register | `{ids:[...]}` | `{shortcuts:[{id,title,action}]}` | shortcuts；只可选清单已有 ID，每次替换当前集合 |
| widgets.update | `{id,lines:[...]}` | `{updated:true,id}` | widgets；清单已有 ID，标题来自清单，最多四行，每行 160 字符 |
| links.openInternal | `{action,arguments}` | `{delivered:true,action}` | links＋原生输入；仅当前应用的已声明动作 |

标识是 1–64 位英数/点/下划线/连字符，首位英数。通知 title≤100、body≤500，不接受控制字符；action 不能是任意 URL 或命令。单实例最多 20 条现存通知、全局 100 条；每实例保留最多 256 条十分钟去重 ID。尚未移除的同 ID 通知始终去重；关闭应用清除其通知、角标、快捷操作和小组件。静音是按账号/来源真实保存的配置，静音/撤销立即移除通知及角标。

清单最多各 4 个快捷操作、内部 link 动作和小组件；声明 ID 为小写字母开头的小写字母/数字/下划线/连字符，最多 40 位，标题最多 64 字符。小组件仅纯文本行，无 HTML/脚本执行。内部 arguments 最多八个字符串参数，每个≤256 字符；不能传 appId 目标、Windows URI 或执行命令。通知点击和长按快捷动作由真实宿主控件产生，重新检查注册实例、权限和声明，再通知同一实例；不会新建第二个游戏。后台普通双击仍打开“继续／结束”面板。

appearance 的 scale 为 0.5–8，有效主题 light/dark/system；Shell 可发布已解析的 light/dark。language 来自当前已支持界面语言（当前 zh-CN，不冒称已有语言设置）；reduceMotion 使用真实系统/宿主设置。每次变化推送快照。

```js
const appearance = await autumn.request('appearance.get', {});
const unsubscribe = autumn.onEvent('appearance.changed', next => applyTheme(next));
await autumn.request('notifications.show', { id:'round-4', title:'配对进度', body:'本轮完成四对', action:'resume' });
await autumn.request('shortcuts.register', { ids:['resume'] });
await autumn.request('widgets.update', { id:'score', lines:['配对 4 / 8'] });
// 反例：{action:'powershell.exe',arguments:{}} 必须预先声明且仍只是发送应用内动作；永远不会启动宿主命令。
// 反例：{action:'file:///C:/private',arguments:{}} → INVALID_REQUEST。
unsubscribe();
```

## 事件、错误及排查

`onEvent(name,handler)` 返回取消订阅函数。与旧 onLifecycle 合计最多 64 个监听器；未知事件立即 CAPABILITY_UNAVAILABLE，超限 SUBSCRIPTION_LIMIT。事件信封严格为 `{event,data}`；旧 `{event:'lifecycle.stateChanged',state}` 继续兼容。

| 事件 | data |
|---|---|
| appearance.changed | 完整 Appearance 快照 |
| permissions.changed | `{name,state}`，当前账号来源范围 |
| identity.changed | `{state:'signed_in'/'offline_cached'}`，仅 identity.profile 已允许，不广播 claims |
| links.opened | `{action,arguments}` |
| shortcuts.invoked | `{id,action}` |
| lifecycle.changed | `{state,blocksMaintenance}` |

关闭、崩溃、账号切换删除订阅及等待请求。权限变化会提升事件 revision，UI 排队旧事件投递前再次核验。实际发送使用 `session.DeliverEvent(name, revision, post)`：identity/links/shortcuts 持有对应权限租约，permissions.changed 持有权限决定租约；所有事件同时持有实例和账号代次租约直到原生发送完成。`CanDeliverEvent` 仅供只读判断，不能代替实际投递门禁。撤销后已收到的资料无法从应用内存收回，不能据此宣称远程抹除完成。

宿主开发者接入必须使用 Runtime 的最终响应门禁，而不只在业务 await 完成时检查一次：进入请求前记录 `session.EventRevision`，返回 UI 线程后使用 `session.DeliverResponse(method, revision, json, core.PostWebMessageAsJson)`。它核验迟到结果并将真实最终 JSON 返回给脱敏审计；原生发送期间保持权限、实例和账号租约。Base64/数值参数解析失败返回 INVALID_REQUEST，不让应用挂起到本地超时。重新授予权限不会使此前排队的资料响应恢复可用。

常见错误：AUTH_NOT_CONFIGURED（宿主配置不可用）、AUTH_REQUIRED（未登录）、USER_CANCELLED、USER_GESTURE_REQUIRED、PERMISSION_NOT_DECLARED、PERMISSION_DENIED、PERMISSION_REVOKED、SESSION_EXPIRED、SESSION_SUSPENDED、OFFLINE、CAPABILITY_UNAVAILABLE、INVALID_REQUEST、PROFILE_INVALID、ACTION_NOT_DECLARED、QUOTA_EXCEEDED、REQUEST_BUSY、RATE_LIMITED、DUPLICATE_REQUEST、REQUEST_TIMEOUT、RESPONSE_TOO_LARGE。Storage/句柄的细分错误见存储文档；SDK 本地还有 HOST_UNAVAILABLE、INVALID_ARGUMENT、TOO_MANY_REQUESTS、TIMEOUT、TRANSPORT_ERROR、INVALID_RESPONSE。

看到 USER_GESTURE_REQUIRED 时确认从真实输入触发，并查看宿主原生事件是否到达；不要给 SDK 添加自报手势字段。PERMISSION_REVOKED 需由用户去设置管理，不循环弹窗。SOURCE_REJECTED 表示消息页面不是宿主绑定入口，不能通过更改 appId 解决。SESSION_EXPIRED 后只可关闭旧实例，由宿主明确重新打开；不会自动继承另一账号状态。

自动验证入口：现有 `scripts/Test.ps1` 调用 `T03RuntimeTests.Cases()` 和 `node --test tests/sdk-tests.cjs`。测试资料、时钟与 transport 替身均显式测试对象；它们不证明真实 Logto 往返、浏览器隔离或完整 T01 网络沙箱。最终桌面控件和真实 Windows 运行结果必须另读构建报告。
