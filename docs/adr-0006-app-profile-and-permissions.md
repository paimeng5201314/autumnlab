# ADR 0006：应用资料标识、权限与会话隔离

制作人：派蒙。状态：T03 本地实现；真实多账号往返须看构建验收，不能以本 ADR 代替。

## 决定

宿主使用已经通过 OIDC 验证的 issuer/sub 所形成的 `IIdentityService.Snapshot.AccountNamespace`，不会使用昵称、邮箱或消息自报 userId 定位账号。Runtime 接收宿主构造的冻结 `RuntimeAccountContext`：AccountKey、SessionEpoch、取消令牌、当前性检查和同步提交租约。JavaScript 请求只允许原协议四个信封字段；appId、来源、实例和账号代次不能由请求指定。

`RuntimeApplication.BindingKey` 为 SHA-256，输入是固定域 `autumnos.app-binding.v1`、appId、repositoryId（InvariantCulture）、签名指纹和宿主安装来源，各字段用 NUL 分隔。应用范围资料 ID 为 `au1_` 加 SHA-256，输入是固定域 `autumnos.app-user.v1`、AccountNamespace 和 BindingKey，字段以 NUL 分隔。名字不参与两者。

这提供稳定命名空间及应用间不同标识；不是匿名化保证、签名、令牌或服务端凭证。持有原始 subject 并能枚举其他输入的人可能重算散列。应用只获得已授权的 appScopedUserId、displayName、HTTPS avatarUrl 或 null、isCached；不获得原始 subject、完整 claims、邮箱、手机号或任何宿主令牌。服务端认证另见 ADR 0005，不能接收该散列代替认证。

同一真实身份与相同完整应用绑定，在版本变化或数据目录变化时算法结果不变；来源、仓库或签名身份变化产生新授权边界及新资料 ID。当前内置元素配对使用固定 `bundled:cn.labchronicles.elementpairs` 来源，以保留既有数据。明确本地开发预览以包内容哈希区分来源；修改包产生新来源，不能自动继承旧授权。未来 T04 注册来源与签名轮换需要显式迁移协议；本轮没有实现隐式合并、跨包搬迁或可信来源认可名单。

## 登录与授权分离

`IPermissionService` 以账号、完整 BindingKey、具体权限作为主键，在 Config/application-permissions.v1.json 保存决定。文件只有授权元数据，没有宿主凭据。原子替换保留前一版 `.bak`，拒绝重解析目录/文件、重复键、未知版本和超限内容；损坏时保留文件并拒绝继续，不默认全部允许。

只有清单声明的权限可申请。新权限没有旧决定，即使应用原权限已允许也必须独立申请。授权弹窗展示宿主绑定的名称、来源、实际声明用途（最大 160 字符）、平台边界和资料字段。SDK 消息中的“手势”布尔值无效：宿主原生 Pointer/KeyDown 记录一个两秒内、仅当前前台实例、一次消费的手势。运行器独立核验；未捕获的 WebView 原生输入会返回 USER_GESTURE_REQUIRED，不能为了演示把脚本事件当原生输入。设置中的实际系统按钮可直接管理授权。

拒绝或撤销会持久化。应用重复请求只获得 denied/revoked，不反复打开窗口；只有用户在设置显式改为允许或重置 prompt 才改变它。授权处理中的 revision 变化使迟到允许失效。

## 撤销、切换与线性化

Runtime 的等待队列、速率、大小和十分钟有界去重沿用 T02 修复。每个异步返回重验冻结账号、实例生命周期及权限。权限服务的 `EnterUsageLease` 与存档最终提交共用同步边界：权限锁 → Runtime 状态锁 → Identity epoch 锁。若撤销先到，提交失败；若原子提交先到，撤销等待它结束，两者有明确次序。租约不能跨 await。

成功结果还要经过 UI 投递门禁：宿主在开始 HandleMessageAsync 前捕获 `EventRevision`，等待后调用 `DeliverResponse(method, revision, response, nativePost)`。该方法持有相同锁顺序直到原生 PostWebMessageAsJson 返回，重新检查账号、最小资料权限与绑定 revision。revision 在权限提交锁内发布，独立于 Changed 回调；即使事件队列停顿，撤销后再次允许也不能复活旧资料响应。首次 requestProfile 自身的一次 prompt→granted 会被正确识别，避免误拒第一次实际授权。

应用关闭取消等待工作、停止事件、注销桌面扩展与句柄。UI 排队事件携带 Runtime EventRevision，投递前再次检查；撤销、关闭或切换使旧事件无效。已交给应用的资料不能远程收回，这一限制会在授权 UI 说明。

账号切换由 Shell 先征求保存/关闭运行应用的确认，完整关闭 WebView 后才变更身份。持久化浏览器目录按账号和来源划分，当前受控应用还使用 InPrivate 配置；不能只更新头像或仍复用旧 WebView。单实例启动协议不启动第二个身份服务或回环监听。

## 实现与证据边界

合同：`src/AutumnOS.Contracts/PermissionContracts.cs`；实现：Runtime 的 PermissionService、RuntimeSession.Managed 与 DesktopExtensionService。自动测试使用显式测试资料提供器和模拟时钟，不会进入产品配置。源码辅助测试报告和最终 Windows 验收分别保存；不能用辅助测试证明真实 Logto、第二账号、浏览器持久化或任意第三方安全沙箱已经完成。
