# 派蒙 · 原生 WinUI 会话画面原型

2026-10-31最新增量（尚未构建/运行）：4724已真实闭合中文IME/native结算/ACK/final DOM/同包A模式和全部清理，仅缺raw键盘；用户确认本轮没有按方向键，所以原partial不归因程序路由。另按微软[原生键盘路由说明](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-events)，Button的Space/Enter、TextBox的方向键可由控件默认处理，不能只依赖普通冒泡订阅。新Viewer在自身root使用PreviewKeyDown/Up routed AddHandler(handledEventsToo:true)，从FocusManager.GetFocusedElement(XamlRoot)取得exact surface/proxy/other角色，不接系统全局hook或其他窗口。

实际NativeKeyDispatchGate只在Ready且未关闭时派发固定映射白名单：surface普通键，proxy仅Idle导航键，Active/PendingEnd阻止新raw down以免把IME字母/候选再注入；只有有界Enqueue成功的down才持有，repeat允许，真实keyup仅配对此前持键一次，焦点切到同root其它控件或开始IME后仍可释放原持键。剪贴板Preview先取消默认动作；被拦新down不得派发，但不能吞掉此前已接受的同键真实up。原surface/proxy普通KeyDown/Up订阅移除，避免重复发送；不合成keyup来满足native计数。关闭仍可立即进入原正常清理，不等待键盘完成。

界面第1步先确认当前自有textarea/document焦点、实际native down/up、至少2个真实key ACK、最新真实呈现frame的DOM keydown及已无持键，再启用第2步中文代理；正在进行或PendingEnd事务不会被此新步骤禁用。原IME/helper/NativeEvidence/实际A证明与报告pin/权限/终态hash和清理条件不降低。报告只加有界焦点角色/Handled/组合状态/Focus返回值的计数，不记录按键名称、顺序、候选或正文，不把这些计数当投递证据。至少6个新真实helper纯例及原29纯+8文件IO必须新build全跑（计划43项），它们不冒充真实按键/UI验证。

焦点/效果候选：原生代理处于Idle时，只有最新真正呈现的自有frame同时证明textarea active、document.hasFocus和文档摘要，才启用输入并准入新Started；画面更新不会禁用Active/PendingEnd的原生控件。BrowserHost在实际动作前重新读取同文档/真实焦点；非空preedit必须有实际update增量、本事务真实start及event.data长度/摘要吻合，commit/cancel必须实际end增量并退出composing，commit还核end.data。固定只读预算只会失败，不用CDP返回代替效果，不合成事件或强制focus；空update保守failclosed。28项协议/事务/效果纯例外另增1项真实原生对照模式比较逻辑，共29项；具体实跑/源码绑定以Build报告为准，原生A/B复验仍not_run。

本目录是独立诊断项目，不修改生产Shell、解决方案或外部.autumn门禁。完整入口为 `AutumnOS.SessionBrowserUI.exe --execute <manifest绝对路径> <native-ime-control.json绝对路径> <报告SHA256> <control-binding.json的SHA256>`，先验证同包真实原生A对照及其原进程退出绑定，再启动WinUI/会话。无对照、零end、缺失或混合模式不默认接受false。真正B结束事件的raw trust计数须与同引擎A实测模式一致，并保留实际end/data、原生事务、trusted start/update/input和所有清理验收；不修改DOM属性、不以JS事件替代。具体假设纠正与三步流程见docs/adr-0010-native-ime-control.md。无参数仍返回0且不创建窗口或会话。固定依赖为WindowsAppSDK1.8.260921001、WinUI1.8.260803003、SDK BuildTools10.0.26100.9169，实际.NET10.0.12由构建回读。

首次冻结包 `probe-a9e8dcaf04114fb79b60ce6bb59ff7a3` 的授权启动在创建 session case 前以 C000027B 失败；没有进入 NETONLY。只读比较确认该 code-only App 缺自己的 App.xbf/PRI 和 XamlTypeInfo.g.cs，而生产 App.xaml 基线具有编译器生成的 IXamlMetadataProvider。新源码改为标准 `App.xaml` / partial ViewerApp / InitializeComponent；这是待验证的修复候选，不覆盖旧输出/ZIP，也不把缺 metadata 与本次异常的因果关系冒充已实测确认。

新 `--ui-startup-check <manifestAbs> <freshReportAbs>` 是独立 UI-only 模式：在进入 Application.Start 前 CreateNew 并落盘固定阶段报告，WinUI UnhandledException 仅记录阶段/type/HResult，不标 Handled。沿同一 ViewerWindow 构造，Loaded 分支不运行 SessionClient、Core、NETONLY、profile 或 browser；核对生成资源、RootLoaded、Rendering、自身 HWND PID/可见性，实际 Closed 后才可报告 `completed_native_WinUI_startup_only`。5秒 Rendering 预算只会失败。root 的新冻结执行链必须获得相应明确授权；仅该检查实际成功且原进程退出0后才允许同包的一次完整执行，失败不重试，不复用旧包执行授权。构建本身不打开窗口。

UI 复用同一 manifest 验证器，以 `LoadForViewer` 核对自身入口；manifest、core、UI、固定 WebView2 完整文件集均读锁定/哈希验证。只写全新的 `case_root/session-browser-case-<nonce>/ui-report.json` 及尚未转发任何输入、DOM 为空/未组合时的两张不同初始自有 PNG。后者固定 `frame-initial-1.png` / `frame-initial-2.png`，`CreateNew`，报告记录字节数、SHA256、frame_id；不保存输入后的画面或原始输入文本。

Core 以原始 `Process.Start` 句柄持有；kernel named-pipe PID、实际本机进程 token/image 验证后，UI 从该原句柄表复制 QUERY(4) Job witness，并复制 QUERY_LIMITED_INFORMATION|SYNCHRONIZE(0x101000) 原 NETONLY StationHost 句柄。真正 flags0/初始 Job0 和原 Host 身份通过后才发成功 `job_bound`。关闭先停收新输入并有限排完已接受队列，正常 `close`；等待原 Core 退出、原 StationHost 退出，再验证该 Job0。计时器不作退出/归零证据，不重开 PID、不杀进程。Core crash 后未知 Host 仍保持 pending；只有 nonce/source/manifest 全绑定的最终 core 报告明确证明未创建 Host、无 Job ACK、无 station 的窄失败分支才允许失败清理结束。

每通道最多一个请求，输入队列32项、pointer-move 合并、捕获最多10fps。帧是固定800×600、PNG最多2MiB；共享 codec 核实际 IHDR/CRC/块顺序、固定小元数据白名单及逐行解压的最大1,920,600字节。UI 解码/赋 Image.Source 后还需观察一次真正 `CompositionTarget.Rendering`，5秒预算只会失败。`displayed_frames` 表示该回调已观察，不代表独立读取 DWM/桌面像素。

原生 TextBox 仅存当前输入/组合缓冲，不镜像 DOM。Started/Changed 检查真实活动范围及相对 caret，不 clamp 越界。历史 a37a 运行已证实 End 通知可为 StartIndex0/Length0、caret7；旧活动范围门禁错误拒绝这一结束事件，不能归因用户操作。新纯 helper 将 End 通知范围与最终文字分开：先核实际空初始缓冲，End 只核整体有界范围与已开始的事务，不以 Length0 判断取消。进入 PendingEnd 后抑制 TextChanged 的额外 insert_text，必须观察同一控件 End 后真实同步 TextChanging revision，再由同代低优先回调读取与该 revision 长度/hash 一致的缓冲；非空才 commit，空才 cancel。不把一次 Low 回调或5秒预算称作 TSF 最终提交屏障；未见真实后续 revision 只会失败，快速相邻新 Started 也 failclosed，不清未知新事务缓冲。这些 provider 顺序仍须新的物理操作验证。

commit/cancel 成功进入有界队列后，才清本次缓冲并解除 PendingEnd。正常关闭先停止新输入、等待既有结算，再 RequestClose 排完队列；未 End 的活动事务标失败并取消，不能成功。报告只保留原生结束范围、通知/结算长度与 SHA256、真实后续 revision、被抑制的通知数、结算判定及真实 commit ACK，不保存原始文本。旧16个 codec 纯回归保留，新增7个调用实际事务 helper 的纯回归覆盖零范围/caret7、空取消、End 前后重复通知、无 revision/未观察版本、重复/未开始/重叠事务与关闭排队顺序；纯逻辑不冒充原生 IME 通过。组合外文本以 insert_text 输入，surface 字母 raw-key 不作为文本键入证明。成功还必须实际原生鼠标/键盘/组合 trace、提交 hash 与真实 ACK/关闭最终 DOM 一致、WASM42、RAF≥60、不同帧/像素哨兵和全部资源清理证据；缺项标 partial/failed，不能宣称完整 TSF/键盘/生产 G1 通过。

Paste/CopyingToClipboard/CuttingToClipboard 被 Handled；Preview Ctrl-C/V/X、Shift-Insert、Ctrl-Insert、Shift-Delete、可路由的 Win-V 被拦；context/drag/drop 不读取 DataView。Ctrl-V/Shift-Insert 仅转固定私有 CDP paste 动作，C/X 纯拦截；不从 WinSta0 读取文本。OS 全局热键、IME/第三方 provider、UIA 等平台绕行没有实测，不能由应用层事件取消宣称这些系统边缘已完全封闭。

真实结果仍以 root 的源码快照、构建报告和另行明确授权的原生运行报告为准；本源码包不代表 UI、LSA、浏览器或隔离验收通过。NETONLY/LSA 清理不宣称，生产运行门禁保持阻断。

API 核对：本地固定 WinUI XML 包含 TextCompositionStarted/Changed/Ended 及各事件参数 StartIndex/Length，亦包含 TextBox 三项剪贴板取消事件。[TextChanging 官方说明](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textchanging?view=windows-app-sdk-1.8)描述其同步更新边界；微软公开 main 分支 [TextBox.cpp](https://raw.githubusercontent.com/microsoft/microsoft-ui-xaml/main/dxaml/xcp/core/native/text/Controls/TextBox.cpp) 说明组合结束后的 final EN_CHANGE，[TextBoxBase.cpp](https://raw.githubusercontent.com/microsoft/microsoft-ui-xaml/main/dxaml/xcp/core/native/text/Controls/TextBoxBase.cpp) 从实际组合范围形成 End 事件。main 分支是实现参考，不冒充本机固定1.8二进制逐字对应。[PNG 第三版规范](https://www.w3.org/TR/png-3/)。
