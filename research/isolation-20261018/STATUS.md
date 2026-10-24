# 2026-10-18 本地隔离验收当前记录

制作人：派蒙。当前尚不是最终交付。任务文件 `AutumnOS_Isolation_Implementation_Codex_Prompt.md` 缺失；按用户本轮完整指令执行。没有 Git、上传、Release、代理修改全局安全策略或真实存档清理。所有原始失败报告保留，以下按最新已落盘证据归纳。

**最新已完成构建为 Build03；主产品完整验收仍未通过。** native-09 的输入护栏中止、native-10 的文档失效关闭与旧脚本期望不一致、剪贴板真实反例、受限令牌子进程初始化失败是四种不同结果，不能混为“尚未测试”。Build04 与待授权响应失效场景正准备，验收 **not_run**。ChemLeap 普通模式、另一来源本地包、最终 EXE/ZIP 与新快捷方式仍未完成。

## 已实际实现的边界

- `RuntimeIsolationPolicy.cs`：策略 `autumn-local-web-v2`，固定引擎 154.0.4258.53，宿主响应 CSP/Connection-Allowlist/Permissions-Policy。包内脚本字节 WASM 支持；JS eval、全部 fetch/XHR 含同源继续拒绝。同源 connect-self 的真实 WS/WT 旁路反例保留。
- `WebAppHost.cs`：第三方字节前核验实际固定引擎、浏览器路径、InPrivate 和独占账号/来源/应用 profile；拒绝环境及适用注册表覆盖；先设置事件与策略、先完成宿主原生挑战，再提供包资源。共享 mDNS 禁用 flag 只消除可选入站监听副作用，不是正式网络边界。
- 当前普通 github/local 应用使用统一已安装应用启动链；开发者模式、自报 sandboxVerified、PM/SQ 分类均不提供安全放行。文件与 SDK 仍受实例、账号、权限、来源和当前文档绑定。
- Build03 已在真实主产品中观测到 `document_binding_lost → closed`，当前文档失效会终止实例。待授权响应期间失效及其完整回归仍需 Build04 实测；旧服务路径迟到响应检查已有实现，不能用源码代替该用例。

## 当前构建与实际测试

[Build03 构建报告](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/build-result.json)：`T06-20261018-isolation-03`，版本 `0.5.2-local.4`，源码 `sha256:2580ee29f8e8672f02a2f7a51e49367d5b25133ed6ef01323bd7f440cd71e610`，Release/win-x64；Windows NT 10.0.26200.0、.NET SDK 10.0.401、WebView2 SDK 1.0.4258.31、固定浏览器 154.0.4258.53。837 C# 及 SDK/样例/模型/身份/HTTP/CLI/机器合同报告 passed。

[目录 Prepare 报告](<WORKSPACE>/artifacts/builds/T06-20261018-isolation-03/package-prepared.json) 为 prepared，ZIP 尚未创建。中间 EXE：[AutumnOS.exe](<WORKSPACE>/artifacts/packages/T06-20261018-isolation-03/AutumnOS-0.5.2-local.4-win-x64-development/AutumnOS.exe)，SHA256 `b9b11dc882d6e65f6ce97a2ef58e6ba37d76daa035c9bbcc158cd4318dc9ea12`。它不是完整验收通过的最终交付。

[probe-matrix-03](<WORKSPACE>/artifacts/isolation-20261018/probe-matrix-03/summary.json) 在 Build02 关联的共享新 mDNS 参数下重跑 runtime-ab、resources-storage、network、same-origin 四组固定引擎探针，均 passed，binding_errors 为空；资源/存储 25 项，网络有自有接收器、15 个逐通道正控和主机 NetLog。网络/同源模式从实际 NetLog 确认命令行共享 flags；另外两模式只记录 API 参数。它不是 Build03/04 主产品全验收。早期 `probe-final2-*` 与 `PROBE_HANDOFF.md` 保留，不能冒充最后构建证据。

[native-08](<WORKSPACE>/artifacts/isolation-20261018/native-08/runtime-isolation-native.json) 对 Build02 记录 13 项 passed，总 failed：禁止导航后仍出现错误页，原观察字段消失；保留当时失败，不能回填。

[native-09](<WORKSPACE>/artifacts/isolation-20261018/native-09/runtime-isolation-native.json) 对 Build03 记录 11 项 passed，总 failed/cleanup passed。窗口最小化后 client origin=(-32000,-32000)、client size=(0,0)，输入稳定性护栏拒绝点击。此为输入前置条件失败，后续未执行测试仍 not_run，不能视作浏览器权限或网络拒绝通过。

[native-10](<WORKSPACE>/artifacts/isolation-20261018/native-10/runtime-isolation-native.json) 对 Build03 记录 14 项 passed，总 failed/cleanup passed。popup、download 拒绝后原页面保持通过；随后记录 `navigation_denied → document_binding_lost(Crashed) → closed(Crashed)`，证明当前文档失效保护实际生效。旧脚本仍期待 `Assert-IsolationPreserved`，等待旧观察字段超时而失败。后续应验证终止、旧 SDK 与待授权响应失效，不能只删除断言或将本报告改成 passed。Build04 与 pending-permission 真实验证目前 not_run。

## 剪贴板：已有失败反例，尚无可交付完整边界

[clipboard-station-uac-02/report.json](<WORKSPACE>/artifacts/isolation-20261018/clipboard-station-uac-02/report.json) 六项全部执行，**4 passed / 2 failed**。A 无浏览器 Job 的 copy/cut/paste 正控三项 passed；B 生产响应头加浏览器 Job UI READCLIPBOARD|WRITECLIPBOARD=6 的 paste passed，但 copy/cut 仍清空合成剪贴板，sequence 分别 22→23 与 40→41，原生接收值 null，均 failed。不能把 Job 设置成功或没有写入目标文本当作拒绝成功，不能集成该候选作为完整边界。

该试验只在经授权创建的新非交互窗口站与自有 desktop 使用合成数据，**WinSta0 剪贴板从未打开、读取、清空或写入**。六个独立浏览器正常退出，无 ProcessFailed；helper 释放 exit 0，probe 因断言失败 exit 1。租约报告 passed 仅证明生命周期，不代表隔离通过。前期窗口站 Win32 5/183/170 失败与 Job 赋值成功报告仍保留，均不推翻这两项真实失败。

[restricted-token-launch-01/report.json](<WORKSPACE>/artifacts/isolation-20261018/restricted-token-launch-01/report.json) 总 failed。A 本人普通令牌子进程正控 exit 0、元数据验证成功；受限主令牌创建成功。B 的 **CreateProcessAsUser 返回成功**，子进程随后自然 exit -1073741502（`0xC0000142` 初始化失败），没有探针 JSON，故不能证明受限子进程元数据或可用隔离宿主。不是 API 权限拒绝。只查询窗口站句柄权限，无剪贴板 API、GUI/WebView 初始化、UAC、ACL/全局策略改变、窗口站关联改变、强杀或 breakaway；WebView 兼容性、剪贴板隔离与不可信页面均 not_run。

## 普通应用和剩余交付

ChemLeap 已装 0.1.1，真实来源 github:1414175562，包 SHA256 `e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2`，8 资源匹配、只声明 saves、没有必需运行远程资源。未修改已装包，未读取真实存档。普通模式游玩、返回继续/结束重开与 SDK 存档仍 **not_run**。不同来源的 `cn.labchronicles.normallocal` 夹具与普通模式驱动已存在，真实 UI 验收 **not_run**，元素配对回归必须保留。

第三方宿主网络 SDK 当前 **unsupported**，授权联网正向验收 **not_run**，需求保留。外部权威 DNS、可信证书 WebTransport 完整握手、真实 Logto 双账号与其他 Windows 环境仍 not_run；受控测试身份不冒充真实双账号。

防火墙历史：[FIREWALL_FINDING.md](<WORKSPACE>/artifacts/isolation-20261018/FIREWALL_FINDING.md) 记录 Windows 安全中心真实截图、派蒙确认手动点击允许、限定工程路径规则只读快照及新路径辅助 flag 对照。代理没有点安全按钮或改规则；不能声称当前系统环境从未变化，也不能以无提示作为无网络证据。

继续完成：Build04 文档终止及待授权响应验证、完整主产品网络/SDK/存储/撤销/关闭用例、完整剪贴板边界、两种普通来源游戏及样例、冻结后最终 EXE/ZIP 解压副本与不覆盖旧入口的新快捷方式。精确支持范围、打包顺序和 single-file ZIP/picker 自动化缺口见 [DELIVERY-DRAFT.md](<WORKSPACE>/artifacts/isolation-20261018/DELIVERY-DRAFT.md)。本项完成也不提升所有历史阶段为通过。
