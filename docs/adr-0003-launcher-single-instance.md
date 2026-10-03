# ADR 0003：跨发行目录的启动单实例与现窗召回

Lab Chronicles AutumnOS · 制作人：派蒙。

状态：本轮已采用并接入源码；实际构建及 Windows 验证结果以 `AUTUMNOS_PROGRESS.md` 和对应构建目录报告为准。本 ADR 不代表测试已经通过，不改变 T01/T02 的未完成验收或 T03–T06 范围。

## 背景与固定工程基线

AutumnOS 当前为 `.NET 10`、WinUI 3、`win-x64` 的非 MSIX、自包含 Windows 客户端。`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`.NET SelfContained=true`、`PublishSingleFile=false`、`PublishTrimmed=false` 保持不变。

当前依赖锁固定为 Microsoft.WindowsAppSDK **1.8.260921001**，其中 Foundation 为 **1.8.260803002**、WinUI 为 **1.8.260803003**；Windows SDK BuildTools **10.0.26100.9169**，WebView2 SDK **1.0.4258.31**。本决策不升级依赖，不注册 MSIX，不安装全局启动服务，不修改系统 PATH。

一个工作区可以保留多个本地构建和完整发行目录。重复点击任意参与本协议的 AutumnOS.exe 应召回当前已有窗口，保留原来的页面、窗口状态和内部游戏实例。第二进程不创建另一个 MainWindow，也不初始化自己发行目录下的 AutumnOS_Data。

## 对 Windows App SDK AppInstance 的评估

固定 Foundation 包已经包含 `Microsoft.Windows.AppLifecycle.AppInstance` 的 `FindOrRegisterForKey`、`RedirectActivationToAsync` 和 `Activated`。Microsoft 的 1.8 API 文档说明：注册键返回持有该键的实例；`Activated` 只接收重定向的激活，不能代替首次启动处理。[FindOrRegisterForKey](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.applifecycle.appinstance.findorregisterforkey?view=windows-app-sdk-1.8)、[Activated](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.applifecycle.appinstance.activated?view=windows-app-sdk-1.8)

官方实例生命周期文档明确不同应用版本、不同用户使用不同实例列表，没有承诺非打包应用不同 EXE 路径间共享身份，也没有在该合同中规定路径归一化算法。因此不能仅给所有副本设置相同的 AppInstance key，就声称满足跨发行目录、跨版本的产品级单实例要求。[App instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing)

如果以后使用 AppInstance 重定向，必须在窗口创建前完成决策，并等异步重定向完成后才退出转发进程。STA 不能直接阻塞等待该 WinRT 异步操作；官方 WinUI 示例采用 worker 与 COM 等待。本轮采用下述纯本地启动协调，不调用该重定向 API，也不引入这一 STA 等待路径。[WinUI 自定义单实例入口](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)

## 决策与身份范围

增加宿主模块 `AutumnOS.Launcher`，以命名互斥量作为主实例所有权依据，以本地命名管道传递受限的现窗召回请求。协调名由固定产品键 `LabChronicles.AutumnOS.Launcher.v1`、当前 Windows 用户 SID 的 SHA-256 和当前 SessionId 组成，**不包含发行目录、应用版本或 BuildId**。

互斥量使用 .NET 10 `NamedWaitHandleOptions` 的 `CurrentUserOnly=true`、`CurrentSessionOnly=true`。服务端由 `LocalPipeServer` 调用原生 `CreateNamedPipeW`：设置受保护 DACL，仅允许当前用户 SID；设置 `PIPE_REJECT_REMOTE_CLIENTS`，明确拒绝远程 SMB 连接；第一个监听端设置 `FILE_FLAG_FIRST_PIPE_INSTANCE`，发现同名管道已经存在则失败。全部 4 个监听端在所有权生命周期内持续保留，关闭连接后复用。[CreateNamedPipe 的安全描述符和创建标志](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)

客户端仍使用 `PipeOptions.CurrentUserOnly`，只连接本机 `.`；双方均通过 Windows 返回的对端 PID 检查 SessionId。管道服务端的原生 DACL 不应被描述成托管 `CurrentUserOnly`。其协调范围为**同一个 Windows 用户、同一个登录会话内，参与此启动协议且权限可互通的所有发行副本**；不同用户或会话互不召回。[Windows 内核对象会话范围](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)

产品键中的 `v1` 是已经采用的协调命名空间，不能在普通应用升级时更换。以后扩展协议须保留主所有权竞争范围并设计兼容协商；随协议版本更换互斥量名称会让新旧版本各自取得主所有权，不能作为无缝升级实现。

## 入口、所有权与数据

Shell 在项目中追加 `DISABLE_XAML_GENERATED_MAIN`，使用 `Program.Main`，保留生成入口原有的 `[STAThread]`、`WinRT.ComWrappersSupport.InitializeComWrappers()`、`Application.Start` 和 `DispatcherQueueSynchronizationContext`。固定 Windows App SDK 包对自包含模式使用的注册自由 WinRT module initializer 继续生效，不额外调用面向框架依赖部署的 bootstrap。[自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

`LauncherCoordinator.Enter` 在当前入口线程取得互斥量后，创建主进程管道监听端，随后才允许 `new App()` 与 `new MainWindow()`。未取得所有权的进程只能尝试转发或退出；连接失败、超时、访问拒绝和未知协议都不能降级为创建第二窗口。

互斥量一直由取得它的入口线程持有，直到 WinUI 消息循环结束后才释放。`BeginStop` 先标记关闭并取消/关闭 IPC，避免已进入关闭过程的窗口继续处理召回；不能仅因为窗口开始关闭就提前释放主所有权。崩溃后的操作系统互斥量弃置按“已获得所有权”处理，新进程仍必须实际取得所有权后才能恢复为主进程，不依赖 PID 文件、超时后强占或结束其他进程。

主实例继续按原有 `InstallationRoot` 使用它自身真实 AutumnOS.exe 旁的数据目录。转发进程不会合并、迁移或写入另一份发行目录的数据。跨目录召回意味着显示的是先启动副本及其已有数据，不能把后来双击副本的 BuildId、配置或样例覆盖进去。

选举主实例后、进入 XAML 前检查随包必要的 `AutumnOS.pri`、`App.xbf` 和 `MainWindow.xbf` 是否存在；缺失时按入口错误退出并释放所有权。资源故障演练必须同时隔离 PRI 与独立 XBF，单独改名 XBF 不能保证 WinUI 不再从 PRI 加载。次实例不做这一步业务资源检查。此门禁不是完整资源真实性验证；不可恢复的原生崩溃仍依赖内核释放互斥，完整发行资源与哈希在本地打包门禁中核对。

## 有界启动协议

`LauncherProtocol` 是固定大小的二进制协议，只定义“召回当前窗口”。请求为 **24 字节**：magic `AOSL`、版本 `1`、操作码 `1`、保留零位与非空随机 nonce。响应为 **40 字节**：magic `AOSA`、协议版本、结果、服务端 PID/SessionId、原 nonce 和窗口句柄。客户端将响应 PID/会话与操作系统查询到的实际管道服务端比较，并验证 nonce、长度、保留位及结果范围。

没有文件路径、原始命令行、任意 URI、执行命令、游戏控制、账号内容或 Logto token。当前入口不因命令行附加内容注册协议处理器或执行额外操作；后续深链接需要单独的权限与输入合同，不能把启动管道扩大为通用命令通道。

当前实现保持 **4 个**有限监听槽，请求的管道缓冲区大小为 **512 字节**（Windows 可按内核分配粒度调整），实际协议读取分别限于上述 24/40 字节，拒绝超长消息；单个请求包含 UI ready 等待在内最多 **12 秒**，响应写入最多 **1 秒**；转发总预算不超过 **15 秒**，单次连接尝试 **500 毫秒**。未就绪主窗口由 ready task 延后处理，最多仅占用这些监听槽，不创建无限请求队列。

尚未发送请求时的短连接失败可以在总预算内重试。请求一旦可能已经发送，IO 失败意味着结果不确定，返回超时；不能因为确认响应丢失就重复发送召回、持续抢焦点。

转发成功返回 `0`，同时区分实际置前 `Foreground` 与只请求提醒 `AttentionRequested`；超时返回 `20`，拒绝返回 `21`，入口异常返回 `22`。固定的退出诊断可以由本地启动器或测试捕获，任意异常文本和原始启动参数不进入日志。窗口 ready 表示窗口已经可以接收请求，不能自行等同于已成功抢到前台。

## 窗口与游戏状态

主实例通过窗口的 `DispatcherQueue` 处理召回，检查取消/关闭状态及窗口句柄。仅在最小化时执行恢复，随后激活已有窗口，不创建 MainWindow，不调用 `ShowPage`，不启动、结束或切换内部 WebView 游戏实例。窗口的普通/最大化布局以及未保存输入由原对象继续持有。

转发进程只向实际管道服务端 PID 调用 `AllowSetForegroundWindow`，不会授权 `ASFW_ANY`。主窗口比较 `GetForegroundWindow` 判断实际结果，失败时请求任务栏提醒；不能把 Windows 拒绝置前写成成功，也不修改系统前台超时、模拟 Alt 或改变窗口置顶策略绕过限制。[AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)、[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)

召回异常应在单次请求范围内完成失败结果；不得让 UI 线程未处理异常导致已有游戏丢失，也不得让一个失败请求永久终止监听 worker。主实例日志是有容量限制的固定事件证据，必须沿用现有数据路径的链接/重解析点拒绝策略，不能借诊断写入绕过数据目录约束。

实际挂起/恢复测试发现：客户端超时断开后，响应写入可令 `PipeStream` 进入 `Broken`，此时 `IsConnected=false` 仍需要 `Disconnect()` 才能重新监听。服务端在 finally 无条件尝试断开并容忍已断开/已销毁状态；传输异常加入 50 ms 异步退避，避免错误热循环。此路径由真实断管客户端回归和恢复后日志增长检查覆盖，失败原始报告保留在本地。

并发回归另发现，写入完成后立刻调用 `DisconnectNamedPipe` 会丢弃客户端尚未读取的缓冲响应。当前服务端在同一个 1 秒响应预算内异步等候客户端读完回复并关闭连接；没有增加任意消息，也不用无界 `WaitForPipeDrain`/`FlushFileBuffers`。延迟读取 ACK 的真实管道测试覆盖这一交付边界。[DisconnectNamedPipe 的未读数据语义](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-disconnectnamedpipe)

## 已知边界与未完成范围

客户端使用的 `PipeOptions.CurrentUserOnly` 在 Windows 上同时检查服务端用户和提权级别；服务端原生 DACL 限制 SID，不单独声明实现托管同提权级别检查。普通用户与“以管理员身份运行”的副本可能无法互通；本程序保持 `asInvoker`，遇到权限不匹配必须失败并保持已有主实例，不退回开放管道 ACL、不提权、不另开主窗口。[CurrentUserOnly 合同](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)

该 IPC 是同一用户会话内的本地协调边界，不宣称抵抗已经能以同一用户运行任意代码的程序。对端用户检查不等同于发布者签名认证，因此请求能力严格限制为现窗召回，不授予敏感数据或任意宿主功能。

没有集成本协议的历史开发包不参与此互斥量。新版本无法单方面让它们遵守单实例规则；不枚举或结束同名进程，不删除旧包或用户数据。使用多个历史包时需要用户正常关闭不再使用的旧窗口，不能在报告中宣称所有旧版本已自动纳入。

这个启动互斥量**不是 T05 维护锁**。它只决定谁拥有桌面主进程，不代表“没有游戏实例、没有存档关键写入、没有安装/迁移”，也不授权更新器替换文件。T05 仍需独立的共享维护协调和更新健康/失败回退门禁。

本轮验收应包括：同目录重复启动、跨发行副本、中文/空格路径、不同工作目录、启动并发、窗口未就绪/关闭竞态、最小化及最大化恢复、游戏页面/实例与未保存输入保持、未知/超长协议拒绝、超时不新建窗口、主进程异常退出后重新取得所有权。未实际执行的矩阵保持 `not_run`，与正式发行、Windows 10、真实账号及 T05 更新验收分开记录。
