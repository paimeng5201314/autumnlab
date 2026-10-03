# T03 受控元素配对样例

制作人：派蒙。样例源码位于 `samples/element-pairs/`，通过现有 `scripts/New-SamplePackage.ps1` 打包为内部 `.autumn`。新增 `t03-features.js`、`t03-features.css` 和默认折叠的「账号、文件与桌面联动」面板；原游戏逻辑、`game.css`、牌面、昵称、保存/读取按钮及 ID 保留。`game.js` 仅将游客/账号与存档授权的状态文案改为实际当前账号语义。样例仅使用宿主 SDK，不发起网页网络请求、不使用浏览器 localStorage、不启动外部命令。

这些按钮实际调用 SDK 0.3.0 / 协议 1；它们不是效果示意。未授权、游客、静音、重复通知和不支持的宿主都会显示真实状态。此文给出运行方式，不代替测试报告；真实 Logto 往返必须在账号系统应用中由用户完成。

## 运行与用户输入

从当前开发包运行 `AutumnOS.exe`，桌面打开「元素配对」，展开「账号、文件与桌面联动」。面板默认关闭，原配对游戏可独立运行。

权限按钮和使用权限的操作按钮分开。先点「申请…权限」，在宿主显示的应用、来源、用途与字段弹窗中选择允许或拒绝；随后**再次点击**具体操作。系统选择器与内部链接必须使用这次新的真实点击，不能在异步授权结束后沿用旧点击。拒绝或撤销后不会自动重复弹窗，可在设置的「应用权限与数据」中改变决定。

| 操作 / 元素 ID | 实际请求 | 输入、结果与范围 |
|---|---|---|
| 申请并读取资料 `t03-profile-request` | `identity.requestProfile {}` | 使用当前用户动作请求 `identity.profile`；游客返回 `AUTH_REQUIRED`，不强开登录 |
| 读取已授权资料 `t03-profile-read` | `identity.getProfile {}` | 显示昵称、应用范围标识及缓存状态；头像只显示有无，不请求远程图片；无邮箱/电话/完整 claims/token |
| 申请通知权限 `t03-notifications-permit` | `permissions.request {name:"notifications"}` | 只申请权限，不发送通知 |
| 发送配对进度 `t03-notify` | `notifications.show` | 随机有限通知 ID、真实当前配对数、声明动作 `resume`；`shown:false` 的静音/去重不显示成功 |
| 重试上一条通知 `t03-notify-duplicate` | 相同 ID 再调用通知接口 | 观察宿主去重；尚未发送过则拒绝操作 |
| 设置/清除角标 `t03-badge` / `t03-badge-clear` | `notifications.setBadge {count}` | 当前配对数 0–6 或 0；显示宿主实际返回计数，0 不显示角标 |
| 更新配对进度小组件 `t03-widget` | `widgets.update {id:"progress",lines:[…]}` | 先点 `t03-widgets-permit`；两行真实配对数与翻牌数，仅数据，无 HTML/脚本 |
| 登记/撤下快捷操作 `t03-shortcut` / `t03-shortcut-clear` | `shortcuts.register {ids:["resume"]}` / 空数组 | 先点 `t03-shortcuts-permit`；桌面长按/右键可见「回到配对」，既有继续/结束保留 |
| 打开内部链接 `t03-link` | `links.openInternal {action:"resume",arguments:{origin:"sample-button"}}` | 先点 `t03-links-permit`，再点操作；只在当前应用投递，不注册 Windows 协议、不创建游戏新实例 |
| 保存/读取笔记设置 `t03-preference-save` / `t03-preference-read` | `preferences.set/get` | 权限 `storage`，键 `field_note`，值 `{formatVersion:1,text}`；不存在时保留当前输入 |
| 私有文件写/读 `t03-private-save` / `t03-private-read` | `storage.write/read` | 同一逻辑键 `field_note`，UTF-8 + base64；与笔记设置实际使用不同的宿主私有键，不改游戏存档 |
| 选择文本 `t03-file-pick` | `files.pickOpen {}` | 先点 `t03-open-permit`；返回不透明句柄、文件名、大小，不接收或显示磁盘路径 |
| 读取/释放 `t03-file-read` / `t03-file-close` | `files.read/close {handle}` | 样例最多 4 KiB 严格 UTF-8，只展示文本，不执行 HTML，不自动覆盖笔记 |
| 选择导出位置 `t03-file-save-pick` | `files.pickSave {}` | 先点 `t03-save-permit`；系统保存选择器确认，登记句柄不清空已有文件 |
| 导出笔记 `t03-file-export` | `files.write {handle,data}` | 另一次明确点击才写入；成功消费句柄，已有目标生成同目录恢复备份，再次导出要重新选择 |

笔记输入 `t03-note` 最多 240 字符；文件预览 `t03-file-preview` 使用 `textContent`，不解析执行内容。base64 总量低于 32 KiB SDK 消息上限；更大文件明确报错，不绕过配额或分配无限内存。拒绝跨应用句柄、错误代次、过期句柄和路径由宿主实际检查，样例不自行声称已获授权。

## 外观、事件与生命周期

启动仅自动进行 `platform.getCapabilities`、`lifecycle.getState`、`appearance.get` 这些只读查询，不自动申请权限、读取个人资料、写文件或通知。

样例先订阅外观变化，再读取初始快照，并用本地修订防止迟到快照覆盖更新。light/dark/system 生效到游戏和扩展面板；system 使用浏览器当前系统主题并监听变化。语言更新根元素 `lang`；当前正文为中文，不假装提供了其他语言译文。宿主缩放与 WebView 已应用的 `devicePixelRatio` 比值作用于内容缩放，避免把 Windows DPI 重复应用两次。减少动态效果会实际关闭游戏卡片和扩展的 CSS 动画/过渡。

`permissions.changed` 刷新真实状态；资料撤销清空样例当前资料显示，外部文件撤销丢弃句柄和预览。已经交给应用的资料无法靠宿主远程收回，清空本样例 UI 不代表其他第三方一定遗忘。`identity.changed` 只提示用户自主读取，不自动请求资料。`links.opened` 与 `shortcuts.invoked` 的 `resume` 动作将焦点移回现有牌局，不重新发牌、不清空昵称。

后台/挂起时扩展操作按钮停用；返回前台恢复对应可用能力。关闭、崩溃或 `pagehide` 取消等待并调用所有取消订阅函数，宿主负责撤销该实例的文件句柄和桌面注册。SDK 的本地取消只停止等待，已提交的写入可能完成，所以超时/取消后应先读取核对，不能把它描述成磁盘回滚。

## 可重复检查

1. 游客点击申请或读取资料，应真实返回 `AUTH_REQUIRED`，不出现模拟头像，也不自动发起登录。可以在设置中将该应用资料权限明确设为拒绝或撤销。成功登录后再通过样例的用户点击检验原生资料授权允许/拒绝；真实认证需派蒙自己的浏览器操作，不提供测试账号或口令。
2. 申请通知、发送一次、重试同一条，观察真实去重；返回桌面打开通知中心，静音后再发送应返回 muted。设置撤销后请求失败。
3. 申请 widgets/shortcuts/links 后分别操作，返回桌面观察真实小组件与长按菜单。双击后台游戏图标仍是继续/结束面板；继续返回同一牌局。
4. 保存笔记设置与私有文件，关闭游戏再打开后点击读取；游客/A/B 默认不同。没有第二真实账号时 A/B 现场检查标 not_run，不用测试替身冒充真实登录。
5. 系统选择器取消应显示 `USER_CANCELLED`；选小文本另点读取，释放后不能再读；选已有导出文件另点写入，核对新内容及恢复备份。首次选择不会静默覆盖目标。
6. 保持游戏后台，设置切换 light/dark/system，再继续游戏观察事件生效且原输入保留。主程序再次启动不得重开游戏或重放首次引导。

服务层和 SDK 的自动化负面测试另外覆盖权限撤销、伪造实例、来源/账号隔离、句柄越权、容量及长会话。GUI 实测应引用该构建的真实截图与报告；没有运行的项目仍是 not_run，不因为文档或按钮存在就通过验收。

自动检查入口为 `scripts/Test-T03FeaturesSmoke.ps1 -ExecutablePath <本地开发包/AutumnOS.exe> -ReportDirectory <新的证据目录>`，使用独立副本、真实窗口与受限原生输入验证授权、通知、小组件、私有数据和开发模式关闭。`scripts/Test-T03RecoveryFilesSmoke.ps1` 另查真实存档/配置恢复及系统选择器；只操作脚本自己启动的客户端和文件。运行前若已有用户启动器，脚本停止并要求正常退出；它不会杀进程。原生输入由测试工具合成，不能计为人工触摸、真实键盘或完整无障碍验证。

`node --test tests/t03-sample-tests.cjs` 是隔离的 DOM/SDK 替身单元测试，用于检查不自动授权、两次明确操作、错误处理和事件清理；它不替代 Windows WebView、系统选择器、真实 Logto 或多账号验收。
