# 桌面空白双击与图标排序

Lab Chronicles AutumnOS · 制作人：派蒙。适用版本 `0.4.1-desktop-multitask`，以 T04 商店版本为基础的两个桌面交互增量，不代表完整 T02/T03/T04 验收通过。

## 用户操作

- 单击桌面或 Dock 的应用图标：未运行则启动，已有后台实例则恢复同一实例。Enter/Space 等价。图标双击不再打开后台管理。
- 双击桌面空白处（包含桌面底部提示文字）打开“正在运行”。F6 是键盘等价入口；Esc、完成或外部遮罩返回桌面，不结束应用。图标、Dock、设置/通知按钮、小组件、滚动条的双击不会被当作空白双击。
- 切换器卡片来自真实存活的元素配对、受控安装应用及开发者预览实例；无实例时显示真实空态。卡片使用原创图形标识，不伪造游戏画面截图。继续恢复捕获的宿主与实例；结束需确认，确认返回后再核对实例并释放 WebView/运行租约。取消结束保留原实例与输入。应用在后台仍会阻止替换自己的资源。
- 在桌面图标上按住并拖到另一个图标的位置，松手完成顺序调整，其他图标依次让位。系统入口和真实安装应用均可重排。移出图标网格再松手或按 Esc 取消；窗口失去激活、切页、调整窗口大小也取消当前拖动。
- 原 600ms 长按与右键菜单保留，拖动超过8 DIP取消长按计时，拖动释放不会打开应用或切换器。菜单增加真实“向前移动／向后移动”，可通过键盘 Shift+F10 访问。

本轮移动指当前桌面三列网格内排序；Dock、跨页/文件夹及独立抖动编辑模式保留后续需求，没有用本轮排序冒充全部桌面编辑已完成。当前网格较长时可滚动；未实现拖动到边缘自动滚动。

## 状态与持久化

Shell 的 `DesktopInteractionsUi` 负责输入协调、排序呈现和运行实例卡片；不创建第二套 Runtime。打开切换器不调用 StartAsync，不改变账号、存档或 WebView。显示、恢复、关闭均在 UI 线程，并保留账号切换及单实例召回规则。系统设置和商店是宿主原生页面，不伪造独立游戏进程来填卡片。

拖动保持按钮自身的指针捕获，让 ButtonBase 收到真实松手并恢复键盘状态；位置以固定网格坐标计算。移动及取消后的松手通过宿主抑制 Click，释放事件完成后才清理抑制。不能提前把捕获转移到网格而让按钮漏掉 release；回归包含取消拖动后立即按 Enter 打开设置，以及滚动区图标间隙双击。

`DesktopLayoutStore(InstallationRoot)` 单独管理实际 EXE 旁 `AutumnOS_Data/Config/desktop-layout.json`。`Load(ct)` 返回 `DesktopLayoutResult { Success, State, ErrorCode, RecoveryMessage }`，State 含 schemaVersion=1、orderedIds、revision、updatedUtc。首次缺失仅返回空顺序，不写默认文件。

`Save(IReadOnlyList<string> orderedIds, ct)` 提交完整顺序。最多512个唯一 ASCII 标识，每个1..160字符，仅字母数字及 `._-`；它们是宿主标签，不能作为路径或启动命令。Shell 使用 `system.settings` 等系统键和 `app.<appId>` 应用键，分离命名空间，避免社区应用ID与系统入口冲突。账号、凭据和存档不写入布局文件。

`ResolveOrder(savedIds, availableIds)` 纯函数保留已存ID并追加新注册ID；显示时过滤暂时不可见条目，保存时仅置换可见槽位。因此开发者图标隐藏再显示、应用卸载/重装不会静默丢失原顺序。未注册ID不会变成可运行入口。超过容量时保留原文件，桌面仍显示实际入口并明确禁用排序保存。

保存采用独占锁、WriteThrough临时文件、Flush和原子Replace/Move；相同顺序幂等，失败保留旧文件与视觉位置。取消在提交前有效，提交后返回实际成功，不撤回已经完成的原子写入。并发写入失败返回 `CONFIG_BUSY`（沿用 StorageErrors），UI显示可重试错误；不同包目录各用自己的文件，不静默迁移。

损坏记录、未知/重复字段、未来schema、非法ID、容量超限、只读、链接绕过按存储合同拒绝且保留文件；不为启动而重置已有数据。旧 `desktop-preferences.json` 的严格格式和主题/壁纸不改变。此服务仅供可信宿主UI，不添加第三方SDK文件或排序权限。

`Save` 非法输入返回 `DESKTOP_LAYOUT_INVALID`。读取上限128 KiB、JSON深度8；`ResolveOrder` 不访问磁盘，非法输入或合并超过512个ID抛 `ArgumentException`（null参数为 `ArgumentNullException`），调用方必须处理，不能误判为成功保存。

正例：拖动设置到第一格，成功后重启仍在第一格；开发者模式关闭再打开保留既有顺序。反例：把 `../../other` 作为布局ID、重复ID、直接把其他应用文件路径传入Save都会失败；损坏配置不被默认布局覆盖。Shell在保存失败时仍采用上一次成功排列。

## 验证与范围

沿用本地 `Build.ps1` / `Test.ps1` / `Package.ps1 -Checkpoint T04`。新增布局存储自动测试；原桌面烟测按派蒙新规则替换旧图标双击断言，并增加真指针拖动、取消、文件哈希和重启实际位置验证。商店烟测同时创建两套真实WebView，核对两张运行卡片、取消结束和只结束选中实例；旧长按/右键、存档、后台更新阻止及单实例继续回归。实际结果和构建字节证据以 AUTUMNOS_PROGRESS.md 为准，未执行前均not_run。

WinUI事件依据：[DoubleTapped 路由与第一击行为](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.doubletapped?view=windows-app-sdk-1.8)、[PointerCapture](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.capturepointer?view=windows-app-sdk-1.8)。处理 OriginalSource 祖先，不仅判断事件sender；拖动使用固定网格坐标、同一指针ID和捕获结束路径。切换器有限淡入/位移动画遵从Windows减少动态效果设置。

触控/笔、多DPI/多显示器、Windows10、所有超过一屏应用的大布局和完整T02编辑能力仍需后续专项；本轮鼠标/键盘自动化不冒充物理设备测试。T03收尾仍由用户暂缓，T04原公开Release/通用第三方隔离缺项保留，不进入T05。
