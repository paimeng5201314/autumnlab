# SDK 0.3.0 完整接口参考

适用 AutumnOS **0.5.1** 系列，SDK **0.3.0**，消息协议 **1**；制作人：派蒙。以下26方法与6事件均对应交付SDK/autumn-sdk.d.ts、message.schema.json及宿主已接通方法。类型方法从SDK0.3.0可用；平台仍应先检测实际capabilities。内部C#管理接口不在本页导出，identity.beginAppSession/network/任意命令不支持，返回CAPABILITY_UNAVAILABLE。

## 所有方法共同合同

只在宿主绑定的内部WebView调用 `window.autumn.request(method, params, options?)`。消息严格包含protocolVersion=1、requestId、method和对象params，拒绝额外身份字段；应用/来源/账号/实例/代次由宿主绑定。所有下表参数对象拒绝未知字段。结果只来自原实例的最终响应门禁。资料和存档不共享跨应用缓存，读取不存在分别返回exists=false、null；重复通知/静音有显式非成功分支。

输入最多32768 UTF-8字节、宿主JSON深度32；SDK限制28层参数容器、有限非循环JSON，无undefined/NaN/Infinity/函数/自定义toJSON。每实例16个pending、滚动60秒120请求，响应32768字节。宿主默认30秒，允许配置上限120秒；SDK options.timeoutMs整数1–120000、默认30000。signal=AbortSignal只停止客户端等待；已提交的宿主写入可能完成。收到TIMEOUT/USER_CANCELLED后先读回，再决定重试，不能承诺磁盘回滚。

requestId单调唯一；宿主保留最多2048个待处理及近期完成ID，完成后去重10分钟，过期后没有exactly-once保证。读取可重试；写入不自动重试，应序列化同一业务对象的改动并读回确认。普通已授权调用可在background完成；suspended只允许lifecycle.getState。closing/closed/crashed或账号切换撤销实例/订阅，最终提交及UI投递重新核对sessionEpoch与权限revision，旧结果不能进入新账号。

权限状态为prompt/granted/denied/revoked。prompt的新授权弹窗要求前台原生输入，两秒内一次消费；拒绝/撤销持久化，脚本不循环弹窗。文件选择和内部链接另需新的真实手势，授权之后再次点击。已有授权不意味着申请者可以把任意appId或userId放params。每个方法的数据归属、错误、取消、并发和生命周期均同时遵守本节；细节见[身份权限](identity-permissions.md)及[数据](data.md)。

## 平台、生命周期、权限与资料

| 方法 / 目的 | params → result | 权限 / 空值 / 特定失败 / 重试 |
|---|---|---|
| platform.getCapabilities：协商支持 | `{}` → protocolVersion、capabilities、accountMode、networkSandboxVerified=false及limits | 无；只读。capabilities只列已接线服务，缺项不要调用 |
| lifecycle.getState：同步真实实例 | `{}` → `{state,blocksMaintenance}` | 无；挂起时仍可读，七状态为starting/foreground/background/suspended/closing/closed/crashed |
| permissions.query：查决定 | `{name}` → `{name,state}` | 名称必须清单声明；PERMISSION_NOT_DECLARED。只读，不弹窗 |
| permissions.request：请求决定 | `{name}` → 同上 | prompt要求手势；USER_GESTURE_REQUIRED/USER_CANCELLED，denied/revoked不自动重新询问。不自动重试 |
| identity.getProfile：读已授权资料 | `{}` → `{appScopedUserId,displayName,avatarUrl,isCached}` | identity.profile且真实登录；AUTH_REQUIRED/AUTH_NOT_CONFIGURED/PERMISSION_DENIED/REVOKED/OFFLINE/PROFILE_INVALID。只读，不登录、不申请 |
| identity.requestProfile：授权后读资料 | `{}` → 同上 | 登录与profile同意分开；需要时请求权限，不自动弹登录浏览器。不自动重试 |

权限名仅saves、identity.profile、storage、files.open、files.save、notifications、shortcuts、widgets、links。appScopedUserId形如au1_加64位摘要，不是JWT或服务器凭证；昵称最大256字符，avatarUrl仅HTTPS或null，isCached是宿主真实离线缓存状态。不给邮箱、电话、完整claims或token，不加载远程头像。读取返回资料已披露，后续撤销不能远程擦除恶意应用内存。

## 存档和私有数据

| 方法 / 目的 | params → result | 权限 / 限制 / 失败和重试 |
|---|---|---|
| saves.list：列槽位 | `{}` → `{slots:[{slot,formatVersion,revision,bytes,hasBackup}]}` | saves；空数组是没有档案，只读 |
| saves.read：读一个槽位 | `{slot}` → `{exists,value}` | saves；不存在value=null，不清空游戏；SAVE_CORRUPT/SAVE_FORMAT_UNSUPPORTED保留原文件 |
| saves.write：原子保存 | `{slot,value,formatVersion?}` → `{saved:true}` | saves；默认格式1，范围1–1000000；同内容同格式可复用，不能并发假定业务幂等。失败读回再试 |
| saves.restore：恢复备份 | `{slot}` → `{restored:true}` | saves；只处理本账号本应用上一份有效备份，缺/坏备份报真实存储码。不要自动重试恢复 |
| storage.read：读私有字节 | `{key}` → `{exists,data,encoding:'base64'}` | storage；不存在data=null；SDK读取20KiB上限 |
| storage.write：写私有字节 | `{key,data}` → `{written:true}` | storage；data严格base64、总信封限额；INVALID_REQUEST/QUOTA_EXCEEDED。替换当前键，读回后再重试 |
| storage.delete：删指定私有键 | `{key}` → `{deleted:boolean}` | storage；未存在false，不递归删除账号或其他键 |
| preferences.get：读私有JSON | `{key}` → `{exists,value}` | storage；不存在null，与storage逻辑空间分开 |
| preferences.set：写私有JSON | `{key,value}` → `{saved:true}` | storage；键最大59。最后提交值覆盖同键，不自动做并发业务合并 |

saves/storage的slot/key为1–64英数/下划线/连字符、拒Windows设备名；preferences最大59且宿主前缀pref_，所以其逻辑键CON本身不映射设备名。所有数据属于当前账号+应用真实来源，游客不自动迁入账号，注销和卸载默认不删除。内部配额不扩大SDK信封：1MiB私有单文件/16MiB总量/128文件、32存档槽位/128KiB存档。原子写入及迁移持有维护关键操作租约和最终权限/身份门禁。详细存储失败包括INVALID_PARAMS、STORAGE_IO_ERROR、SAVE_BUSY、SAVE_CORRUPT、SAVE_FORMAT_UNSUPPORTED、QUOTA_EXCEEDED；不要从错误猜测用户文件路径。

## 系统文件能力

| 方法 / 目的 | params → result | 权限、生命周期和副作用 |
|---|---|---|
| files.pickOpen：选读取文件 | `{}` → `{handle,name,bytes,expiresUtc}` | files.open＋新的原生输入＋系统选择，取消USER_CANCELLED |
| files.pickSave：选导出位置 | `{}` → 同上 | files.save＋新的原生输入；选择本身不截断已有文件 |
| files.read：读授权文件 | `{handle}` → `{data,encoding:'base64'}` | files.open；读取20KiB上限，句柄所属来源/账号/代次/实例和时间都匹配 |
| files.write：提交授权导出 | `{handle,data}` → `{bytes,backupRetained}` | files.save；一次消费写句柄，原子替换并保留已有目标备份，超时先核对，不能直接复用重试 |
| files.close：释放能力 | `{handle}` → `{closed:boolean}` | 句柄对应files.open或files.save；释放后不能读/写，false不代表可重新获得权限 |

handle为不透明64位小写hex，不是路径；最多64个、最长15分钟，不可转给另一个应用。请求额外path/appId字段无效，读写模式也不能交换。撤销、关闭、账号切换后失效；错误区分FILE_HANDLE_INVALID/FILE_HANDLE_EXPIRED/FILE_HANDLE_WRONG_OWNER/FILE_HANDLE_ACCESS_DENIED/FILE_CHANGED_SINCE_PICK/FILE_TOO_LARGE/USER_CANCELLED/权限或存储码，以实际固定code为准。文件内容不写日志，不自动解析成HTML或执行。SDK本地取消不会撤销已完成导出。

## 外观、通知、桌面和内部动作

| 方法 / 目的 | params → result | 权限与限制 / 幂等和特定失败 |
|---|---|---|
| appearance.get：外观快照 | `{}` → `{theme,language,scale,reduceMotion}` | 无；theme light/dark/system，scale0.5–8。只读，无远端缓存 |
| notifications.show：站内通知 | `{id,title,body,action}` → `{shown:true,id}`或`{shown:false,reason:'muted'/'duplicate'}` | notifications；title100/body500字符、无控制符，action=null或自有声明link。按ID有限去重，不自动更名重发 |
| notifications.setBadge：角标 | `{count}` → `{count}` | notifications；整数0–999，0清除，静音返回0。同值可重复 |
| shortcuts.register：长按动作 | `{ids:string[]}` → `{shortcuts:[{id,title,action}]}` | shortcuts；每次替换集合，最多4且全部在清单。空数组撤下；ACTION_NOT_DECLARED |
| widgets.update：纯文本组件 | `{id,lines:string[]}` → `{updated:true,id}` | widgets；清单已有id，最多4行各160字符，无控制符；替换相同组件，不执行HTML |
| links.openInternal：自有动作 | `{action,arguments:Record<string,string>}` → `{delivered:true,action}` | links＋新原生手势；最多8参数、每值256字符；不接受目标appId/外部URI，ACTION_NOT_DECLARED。事件可能已发出，不自动重试 |

通知id1–64英数/点/下划线/连字符且首位英数；声明动作id小写字母起、最多40。通知每实例20/全局100，256个10分钟去重ID；尚存在ID持续去重。关闭清通知/角标/快捷操作/小组件。通知静音和权限撤销立即生效。小组件属于本账号实例，不作为持久存档；希望重开恢复必须自己读取私有数据后重新申请/发布。详见[桌面扩展](desktop-extension.md)。

## 六类事件和兼容生命周期接口

`autumn.onEvent(name, listener)`和`autumn.onLifecycle(listener)`返回取消订阅函数，共用64监听器上限。未知事件抛CAPABILITY_UNAVAILABLE，超限SUBSCRIPTION_LIMIT；listener异常由应用负责，不终止其他监听器。事件没有请求返回值，不保证永久排队或恰好一次；先订阅再读初始快照，使用本地revision防迟到覆盖，回到前台重同步。

| 事件 | 输入data / 接收资格 / 生命周期 |
|---|---|
| appearance.changed | 完整Appearance；所有当前实例，变化后替换快照，无敏感资料 |
| permissions.changed | `{name,state}`；当前账号来源，收到后清理对应缓存/句柄引用；拒绝不自动重申 |
| identity.changed | `{state:'signed_in'|'offline_cached'}`；仅profile仍已授权，不含ID/claims；清显示并由用户自主重读 |
| links.opened | `{action,arguments}`；links仍有权限、当前声明动作、同实例；不是外部路由 |
| shortcuts.invoked | `{id,action}`；宿主真实长按控件激活已登记操作，shortcuts仍有效 |
| lifecycle.changed | `{state,blocksMaintenance}`；当前实例，关闭/崩溃撤订阅、取消等待 |

旧onLifecycle使用`{event:'lifecycle.stateChanged',state}`，与新事件兼容但不要同时重复执行业务副作用。新信封严格`{event,data}`。宿主投递持有账号/实例租约以及对应权限修订，撤销再授权也不能让旧排队资料复活。pagehide/closed/crashed后SDK清pending和监听器；账号切换由宿主关闭旧实例。

## 错误、示例和证据

宿主错误为`{code,message,retryable,correlationId}`，不含堆栈、路径、token或数据内容。SDK本地错误correlationId=null。通用码：INVALID_REQUEST、SOURCE_REJECTED、CAPABILITY_UNAVAILABLE、DUPLICATE_REQUEST、REQUEST_BUSY、RATE_LIMITED、REQUEST_TIMEOUT、RESPONSE_TOO_LARGE、SESSION_SUSPENDED、SESSION_EXPIRED；权限及数据细分见上文。SDK本地另有HOST_UNAVAILABLE、INVALID_ARGUMENT、MESSAGE_TOO_LARGE、TOO_MANY_REQUESTS、TIMEOUT、TRANSPORT_ERROR、INVALID_RESPONSE。retryable仅提示条件可能恢复，不等于可安全重放写入。

~~~typescript
const state = await window.autumn.request('lifecycle.getState', {});
const save = await window.autumn.request('saves.read', { slot: 'game' });
if (save.exists) console.log('已有存档'); // 不输出用户内容。
const stop = window.autumn.onEvent('permissions.changed', value => {
  if (value.name === 'saves' && value.state !== 'granted') console.log('停止自动保存');
});
stop();
~~~

失败示例：identity.getProfile传userId、files.read传path、saves.write漏value、setBadge传字符串均被真实tsc负例拒绝，绕过类型也由宿主/Schema拒绝。Tests/contracts/sdk-types.ts覆盖全部方法/事件与11类类型反例，method-cases.json覆盖每方法请求/结果、额外身份字段、输出泄露字段；合同工具实际运行TypeScript/Ajv/OpenAPI。Runtime/Storage/Permission的C#测试另覆盖代次、提交和句柄安全，SDK长会话测试另覆盖12000请求；这些不是当前版本原生UI/真实账号验收。运行证据以kit.json对应报告为准。

SDK方法和事件不会新增账号模拟字段。显式开发预览的权限/账号/离线模拟由宿主专用开发管道控制，机器合同SDK/developer.schema.json和CLI命令见[第一个应用](first-app.md)。测试资料只在被确认的预览实例提供，固定显示测试账号、无凭据；普通会话不自动模拟。应用收到的Profile仍使用本页同一类型，isCached的受控测试含义要结合宿主simulation标记记录。应用不能自行请求account-a、修改userId或绕过权限；旧预览重建后请求、订阅和文件句柄失效。模拟证据不证明真实Logto或网络沙箱完整。
