# 剪贴板隔离草案与真实证据

制作人：派蒙。所有文件只位于 artifacts 草案目录，未进入正式产品；不得把编译成功或 Job 兼容性通过标成完整剪贴板隔离。

## 已执行

- `clipboard-station-01/report.json`：普通用户 CreateWindowStation(随机名称) 返回 NULL，Win32=5。微软文档明确只有管理员可指定新窗口站名称。
- `clipboard-station-02/report.json`：CreateWindowStation(NULL,CWF_CREATE_ONLY) 返回 NULL，Win32=183。没有去掉CREATE_ONLY打开已有对象。
- `clipboard-station-02/station-metadata-03.json`：只读证实当前为WinSta0；系统自动站已经存在、owner与当前用户一致，并有多个Chromium样式的沙箱桌面。它是共享现有对象，不能作为安全测试剪贴板。
- 上述两次在创建GUI/WebView或任何clipboard调用之前失败并退出。metadata只有对象名称/owner哈希/桌面名称，没有clipboard访问。metadata初次Add-Type编译失败产生null结果，保留，不能算有效报告。
- `browser-job-01/report.json`：A普通自有WebView加载WASM42；B被原草案IsProcessInJob预判拒绝，未尝试Assign。
- `browser-job-02/report.json`：授权后实验副本去掉保守预拒绝，**实际** AssignProcessToJobObject 新Job(UI READCLIPBOARD|WRITECLIPBOARD=6)成功，specific-job归属和限制回读通过，后续新origin renderer/utility/GPU正常创建，WASM42、零ProcessFailed与正常退出。宿主当前Job UI flags=0；其PID列表包含host/browser，browser父进程为host，说明至少继承工具Job。未开breakaway、未禁Chromium沙箱、未改已有Job。该实验没有任何clipboard API或页面复制调用，不能代替A/B剪贴板验证。02的extended limit metadata因缓冲区长度不精确返回24，03改为Win64结构的144字节后重跑。

## 第一次 UAC 结果与第二次试验准备

派蒙授权的第一次 UAC 已由集成负责人启动。`clipboard-station-uac-01/report.json.lease.json` 的租约握手、全新 station 创建及 helper 正常释放通过；普通 probe 在 `SetThreadDesktop` 返回 Win32 170（BUSY）处失败，未触及任何 clipboard。对应普通 probe 源码已保存在同证据目录。不能复用第一次授权；第二次 UAC 必须另获派蒙授权，并仍由集成负责人唯一启动。授权不扩展到全局策略、提权浏览器、真实 WinSta0 剪贴板、强杀或其他程序。

无 clipboard 的普通预试已执行：`desktop-bootstrap-01/report.json` 证明仅把 MTA 线程换 desktop 后新建 STA 线程仍回到 Default，故拒绝创建 GUI；`desktop-bootstrap-02/report.json` 证明普通 `CreateProcess(STARTUPINFO.lpDesktop=新desktop)` 子进程从 MTA 入口、STA 入口、Form 到实际 WebView HWND 都保持目标 desktop，浏览器自然退出；`desktop-bootstrap-03/report.json` 再证明自有页面的 CDP 鼠标按钮及无修饰普通 x 键具备 trusted、userActivation 与 document.hasFocus。预试没有 clipboard API、copy/cut/paste 命令、Ctrl+V、SendInput、SwitchDesktop 或用户窗口访问。

普通 `ClipboardStationProbe` 修订版已编译，真实私有 station A/B 仍未执行。入口仅接受受限租约：

```text
--runtime <fixed-runtime> --report <new-report.json>
--leased-station AutumnOS_ClipboardProbe_<32lowerhex>
--lease-nonce <same32lowerhex> --lease-helper-pid <authenticated-helper-pid>
```

helper必须只创建全新随机、noninteractive窗口站并保持租约，不接触剪贴板、不启动提权浏览器或改全局策略。普通控制端的管道握手由独立helper草案负责。probe不信可编辑ready.json：核验nonce/精确名称、当前普通Medium非提权token、helper实际进程handle/存活/session/同用户High token、kernel station owner与nonvisible；然后新建随机desktop，再证明实际WebView browser窗口处于该desktop，才进行真实复制探针。运行期每次clipboard访问重新检查当前station/desktop及helper存活；不切换回WinSta0。

probe 同时核验内核 DACL 恰好只给当前 SID 与 SYSTEM 两条无继承的 allow ACE，实际 browser token 与测试宿主同 SID、Medium 且未提权。helper 生命周期或 station/desktop 绑定消失时失败关闭。普通浏览器 Dispose 后等待自然退出，没有 kill-on-close、Process.Kill 或 WinSta0 切回动作。

MTA bootstrap 验证租约并创建 desktop，再用明确 lpDesktop 的普通 child 启动 CLR/COM；child 重新验证 kernel station/desktop，才创建 STA GUI。只将已验证的目标 station 设可继承。真实 probe 的窗体可以在其非交互 station 内正常 Activate/Focus，不切换到该 desktop，也不改变 WinSta0 焦点。

copy、cut、读取/粘贴各自使用独立的 A/B browser，共六个 browser/profile。copy/cut 由 CDP 点击触发可信用户激活，A 需 native receiver 真正收到 marker，cut 还需页面选择内容被移除；B 在页面导航前绑定真实 browser Job UI6。读取 marker 只由 native host 写入私有 station，绝不送入 JS 或 CDP 文本；使用真实 Ctrl+V 键盘刺激，A 必须在 DOM 与即时 pasteEvent.clipboardData 都收到 marker，B 必须在可信键盘及独立完成握手后仍无数据。

每个 browser 从操作前的非零 GetClipboardSequenceNumber 到正常退出后都不再改写 baseline。十次 native/page 观察保留在报告；B 在退出后要求序号和值均不变，因此迟到写入或写回原值也不能通过。paste 事件即时发送并持续收集到关闭。浏览器未退出则禁止下一 case 改写 baseline。超时、脚本未执行、无焦点或正控失败都算失败；CDP 只是刺激测试输入，不是安全边界。真实 clipboard 六项 A/B 仍 not_run。

## 官方依据

- [CreateWindowStationW 名称与CREATE_ONLY规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)
- [进程与窗口站连接/继承规则](https://learn.microsoft.com/en-us/windows/win32/winstation/process-connection-to-a-window-station)
- [SetThreadDesktop 先于窗口/hooks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)
- [窗口站权限与剪贴板权限](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)
- [剪贴板序号以当前window station为范围](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard)

实际当前Windows26200测试证明赋值可成功，不能将通用文档对nested Job的限制理解成所有环境下一定失败；仍必须真实调用、核验失败关闭并验证后续renderer行为。
