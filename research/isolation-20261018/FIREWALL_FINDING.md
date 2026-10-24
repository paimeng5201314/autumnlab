# WebView2 初始化防火墙提示：事实、辅助选项对照与上游修复

制作人：派蒙。记录时间：2026-10-18。本记录只补充本轮隔离工作，不表示 T01 或历史阶段全部通过。

## 已定位的阻塞

native-diag-07 已真实截取 Windows 安全中心窗口，内容为是否允许公共和专用网络访问 Microsoft Edge WebView2。证据：[原始截图](<WORKSPACE>/artifacts/isolation-20261018/native-diag-07/security-picker.picker-27212.png)。它是系统网络提示，并非开发工具的文件选择器。

native-diag-04 的同一实例记录为：controller_created 11:36:25.493、isolation_check_started 11:36:25.506、PickerHost 创建 11:36:26.058、isolation_verified 11:36:26.959、第三方 navigation_completed 11:36:27.016（UTC+08）。因此提示出现在宿主 RTC challenge 期间，早于第三方页面执行。预览确认只调用一次 UIA Invoke；确认消失检查只读取 UIA 属性。未发现确认后的重复点击或开发者 picker 快捷键路径。

标题栏输入保护拒绝操作该系统窗口是正确行为。没有将 PickerHost 纳入产品 owned PID 范围，没有点击其允许/取消按钮，没有通过放宽焦点保护继续测试。

## 防火墙规则只读核查

[有效快照](<WORKSPACE>/artifacts/isolation-20261018/drafts/firewall-scoped-readonly-valid-035257.json)于 03:52:57 UTC 查询 ActiveStore，输出严格限于 `<WORKSPACE>\.tools\` 与 `<WORKSPACE>\artifacts\isolation-20261018\` 的程序路径。

该时点匹配 16 条规则：8 个 WebView2 路径各有 TCP/UDP 两条，全部为 Enabled=True、Inbound、Allow、Public、Local。路径分别是 `.tools` 固定 Runtime、native-01、native-02，以及 native-diag-03 至 native-diag-07。没有匹配的 Block 规则。

用户已明确说明此前每次系统提示由其手动点击允许；这解释了此前测试路径的规则来源。本轮没有修改或删除这些用户规则。不要将它们解释为产品隔离策略，也不要把复用已允许路径后的无提示视为修复。

最初的 `firewall-scoped-readonly-035237.json` 使用了错误的 PowerShell 数组表达式，把两个 scope 拼成一个字符串，其零匹配结果无效。该文件保留；有效报告明确记录 supersedes，不能引用错误零结果。

## 真实辅助参数 A/B 对照

使用当时已编译的 Windows WinForms/WebView2 原生探针 `runtime-ab`，每项复制到全新 Runtime 路径，使用各自独立的新浏览器 profile。固定引擎为 154.0.4258.53。每次复制均核对 `msedgewebview2.exe` SHA-256 与 `.tools` 原件一致：

`30285e24a33be3c0b2d0a7c3909fa1cc21d2fc0f0c2ab4eb79e46a9304b4256d`

参数仅通过该探针子进程的 `ProcessStartInfo.Environment` 传入；未改全局环境、PATH、注册表或生产代码。子进程使用项目 `.tools/dotnet`，不安装全局 .NET。

| 对照 | A：原 CSP，无 Connection-Allowlist | B：生产原生 headers | 系统提示与规则结果 |
| --- | --- | --- | --- |
| `--disable-features=WebRtcHideLocalIpsWithMdns` | passed：脚本完成，两个原生 peer 返回结果，ICE 配置未被过滤，validator 不产生 proof | passed：配置过滤、ICE failed、restart 后仍 failed、0 candidate；产生与实例及 origin 绑定的 proof | 78 次 PickerHost 轮询未见新进程，最大间隔 100.365 ms；新 Runtime 路径的显式防火墙规则 before=0、after=0；探针正常 exit 0 |
| `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` | passed：未产生 proof | passed：产生绑定 proof | 观察到新 PickerHost 28756，并截取同类真实防火墙提示；新路径 before=0、after 出现 TCP/UDP Public Inbound Allow；探针正常 exit 0 |
| `--webrtc-ip-handling-policy=disable_non_proxied_udp`（无 force） | not_run | not_run | 官方代码核查后发现这是 Chrome 偏好映射使用的另一个参数；因主构建开始而停止旧探针工作，未执行此变体 |

mDNS 对照证据：[原生 A/B](<WORKSPACE>/artifacts/firewall-quiet-mdns-20261018-040150/runtime-ab.json)、[参数及窗口观测](<WORKSPACE>/artifacts/firewall-quiet-mdns-20261018-040150/auxiliary-observation.json)。force 参数对照证据：[原生 A/B](<WORKSPACE>/artifacts/firewall-quiet-nonproxied-20261018-040213/runtime-ab.json)、[观测](<WORKSPACE>/artifacts/firewall-quiet-nonproxied-20261018-040213/auxiliary-observation.json)、[准确窗口截图](<WORKSPACE>/artifacts/firewall-quiet-nonproxied-20261018-040213/security-picker-28756.png)。force 参数测试中脚本没有操作系统提示；after 规则存在是外部状态观测，不能从该报告单独证明具体创建者。

这两份旧原生报告的 `browser_arguments="none"` 是当时探针的硬编码元数据，与本次子进程覆盖不符。必须与辅助观测报告共同使用，实际参数以 `process_local_additional_browser_arguments` 为准。旧观测字段 `firewall_rules_modified=false` 仅表达驱动没有写规则，不表示外部防火墙状态没有变化；复跑脚本已将其改名为 `firewall_rules_modified_by_driver`。

首次 mDNS 尝试 `firewall-quiet-mdns-20261018-040130` 在进入 GUI 前因全局仅有 .NET 8 而退出，没有原生报告。该失败保留，不能计为无提示通过。随后使用项目 .NET 10 的独立子进程配置完成了上表对照。

此实验只证明：在本机、该固定引擎和该探针中，单一 mDNS 辅助参数减轻了初始化的系统提示，并且没有把无 header 的 A 伪装成有效 proof。它没有设置网络收集器，不能据此推导“没有网络请求/没有 mDNS 数据包”。PickerHost 采用轮询，不能排除短于采样间隔的进程。正式网络结论仍依赖产品强制的原生 header、实例 challenge、实际通道正向对照及接收端记录。

可复跑工具：[Test-FirewallQuietAuxiliary.ps1](<WORKSPACE>/artifacts/isolation-20261018/drafts/Test-FirewallQuietAuxiliary.ps1)，`-Variant mdns` 或 `-Variant nonproxied`；后者特指已测试的 force 参数。原探针二进制已单独保存在 `drafts/firewall-auxiliary-probe-original/`，并固定校验 DLL SHA-256 `2c837da7e37544f2181a33efb70a899f5666104e2fa2817149316727ce5e4269`，避免主构建覆盖后改变旧实验含义。归档未再次执行。[二进制身份](<WORKSPACE>/artifacts/isolation-20261018/drafts/firewall-probe-binary-identities.json)记录当时文件时间和哈希。

## 官方源码依据及适用限制

1. 当前 Chromium 的 [PeerConnectionDependencyFactory](https://chromium.googlesource.com/chromium/src/+/main/third_party/blink/renderer/modules/peerconnection/peer_connection_dependency_factory.cc)在 peer factory 初始化时，受 `WebRtcHideLocalIpsWithMdns` feature 控制创建 MdnsResponderAdapter；这发生在每个连接的路由偏好处理之前。[MdnsResponderAdapter](https://chromium.googlesource.com/chromium/src/+/main/third_party/blink/renderer/platform/p2p/mdns_responder_adapter.cc)构造时绑定 browser interface；[MdnsResponderManager](https://chromium.googlesource.com/chromium/src/+/main/services/network/mdns_responder.cc)启动时创建已绑定的 datagram sockets。这支持“初始化 mDNS 副作用”的定位；本机提示的消失由上表实际对照确认。
2. `disable_non_proxied_udp` 偏好在 [P2PPortAllocator](https://chromium.googlesource.com/chromium/src/+/main/third_party/blink/renderer/platform/p2p/port_allocator.cc)中禁用 UDP、STUN 和 UDP relay 分配，另有禁用 TCP listen 的设置；它不等于关闭所有 WebRTC/TCP，也不直接阻止此前的 mDNS adapter 初始化。`force-webrtc-ip-handling-policy` 明确存在于 content switches，但所核查的显式消费者是 content_shell。Chrome 的 [command-line preference map](https://chromium.googlesource.com/chromium/src/+/main/chrome/browser/prefs/chrome_command_line_pref_store.cc)映射 [chrome_switches.h](https://chromium.googlesource.com/chromium/src/+/main/chrome/common/chrome_switches.h)中的 `webrtc-ip-handling-policy`（无 force）。不能仅凭参数出现在命令行，就宣称 WebView2 已应用该偏好。
3. Microsoft 的 [WebView2 browser flags 文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)将 browser flags 定位于诊断和测试，明确不保证长期支持并反对用于生产依赖。因此 mDNS 开关只能记录为限定引擎的辅助措施，不能成为隔离验收依据；其消失、被忽略或升级变化不能让第三方绕过原生策略与 challenge。

上述官方源码经 HTTPS 只读 GET 缓存于本目录 `drafts/chromium-*.txt`，未执行 Git。引用的是获取时 Chromium main，不能据此声称 Edge 固定 Runtime 内含完全相同的代码。

## 上游针对性修复与可获得版本

已核实 [Chromium CL 8485457](https://chromium-review.googlesource.com/c/chromium/src/+/8485457)，commit `46fcf3faed9db394de7046a100301f64c2842f0e`，2026-10-08 17:30:59 UTC 合入，commit position `1714691`，标题为 Connection-Allowlist 的 WebRTC binder no-op 修复。[完整官方提交元数据](<WORKSPACE>/artifacts/isolation-20261018/drafts/chromium-cl-8485457.json)已保存。

提交说明：旧实现建立 binder map 时可能检查前一文档的 policy；缺少 binder 还会导致嵌套 about:blank/srcdoc 的 renderer 崩溃。修复把判断移到真正绑定时，读取当前文档策略；禁止 WebRTC 时绑定 NoOpP2PSocketManager 与 NoOpMdnsResponder，避免创建 socket、注册或宣告 mDNS 名称，并正常回应失败，防止挂起和崩溃。这与本轮发现的初始化副作用有关，但不能把源码修复替代本机产品测试。

该 commit 的 `chrome/VERSION` 为 **157.0.8094.0**，这是 Chromium 源码树版本，并非首个可下载的 Edge/WebView2 修复版本。2026-10-18 04:03 UTC 查询 [官方 Fixed Runtime 下载页](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)可见最高版本 **155.0.4283.62**，另有 **154.0.4258.62** 等；未见 157 Fixed 包。[已解码的官方下载地址记录](<WORKSPACE>/artifacts/isolation-20261018/drafts/official-fixed-runtime-available-decoded.json)保留页面哈希与查询时间。当前没有充分证据确认 155/154 包已回补该 CL，最早已发布 WebView2 修复版本保持 **unconfirmed**。未下载或安装系统 Runtime，也未替换现有固定引擎。

主产品接入单一 mDNS 辅助参数后的 WinUI、最终 EXE/ZIP、完整网络接收端、ChemLeap 与 normal-local 验收，属于集成负责人的后续独立报告；本记录不提前标为 passed。
