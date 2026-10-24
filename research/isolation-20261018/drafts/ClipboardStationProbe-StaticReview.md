# 私有 station 剪贴板探针 · 独立静态复核

制作人：派蒙。2026-10-18。复核对象：`ClipboardStationProbe/Program.cs`，SHA256 `836b56ca8ca25e9146846bac7dbd131fec9a60e599e72039841889105e3bd640`。这份记录不是剪贴板隔离验收通过。

## 已复核的实际证据

- `clipboard-station-uac-01/report.json.lease.json`：新建私有非交互 station、显式 ACL/Medium 标签回读、双向 pipe 身份校验及 release 成功；probe 退出码为 1。
- `clipboard-station-uac-01/report.json`：在 `SetThreadDesktop` 返回 Win32 170 时停止，没有执行 clipboard 探针。
- `desktop-bootstrap-01/report.json`：MTA 切换新 desktop 后创建 STA 线程，STA 实际仍在 Default，故拒绝。未把推断当成继承成功。
- `desktop-bootstrap-02/report.json`：普通 `CreateProcess` 显式 `STARTUPINFO.lpDesktop` 后，MTA入口、STA入口、Form和WebView均实际绑定目标新desktop；browser HWND归属通过，正常退出。无clipboard/UAC。
- `desktop-bootstrap-03/report.json`：同样的普通新desktop流程，受控页面实际收到 trusted/active/focused 点击和普通 x 键，浏览器正常退出。没有clipboard命令、快捷键或访问。

## 当前设计检查

1. 租约入口接受明确 station nonce/helper PID；以活进程句柄、同 SID/session、完整性、kernel station owner/DACL/nonvisible 校验。读取 JSON 不作为授权证据。
2. MTA bootstrap 创建目标 desktop，普通同 token 子进程显式设置 `lpDesktop=station\\desktop`；子进程再次核验真实启动绑定，STA入口先验绑定才建GUI。不切换用户可见桌面。
3. WebView 完成初始化后，核对固定引擎版本、普通 Medium 身份及 browser 实际属于目标 desktop 的 HWND；B 在页面导航前施加并回读自己拥有的 Job UI flags=6。任何必要检查失败均失败关闭。
4. copy/cut/paste 各有独立 A/B browser、profile/origin；每项只写一次 native 合成 baseline。在该 browser 正常退出前不写下一项 baseline。异常强制失败；未正常退出则停止后续项，不强杀。
5. copy/cut 要求真实 trusted 点击、激活与文档焦点。A 必须 native 接收端观察到页面 marker 和序号变化；cut 还必须实际删除选择文本。B 退出后的值与序号都必须保持原 baseline，迟写或先写后恢复不能被下一项重写掩盖。
6. paste baseline 只来自 native 私有 clipboard，从未嵌入页面/传入 CDP。仅使用 Ctrl+V 键事件刺激自己的页面，禁止用 `Input.insertText` 替代读取。要求可信键盘、焦点、本地完成握手；paste事件立即报告并收集重复快照，A实际读到marker，B所有观察均不得读到。超时和无页面响应不是通过。
7. 最终 native 读取在最终序号查询之前，包含 delayed rendering 导致的序号变化；读取后正常退出；没有清理真实用户剪贴板或真实用户存档。

当前支持范围限于所测 Windows、固定 WebView2、生产响应头/BrowserArguments、实际创建的 Job 和受控浏览器通道。尚未执行的新私有 station 六项 A/B 保持 **not_run**；无clipboard的普通desktop正控不替代它们，也不代表 ChemLeap 或最终 EXE/ZIP 验收。

## 官方依据

- [进程连接 station 的规则与句柄继承](https://learn.microsoft.com/en-us/windows/win32/winstation/process-connection-to-a-window-station)
- [STARTUPINFO.lpDesktop](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow)
- [SetThreadDesktop 在已有窗口或 hook 时失败](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)
- [clipboard 序号属于当前 window station，包含 delayed rendering 条件](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)
- [Job UI 读写 clipboard 限制](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_ui_restrictions)
- [CDP 输入协议：键、修饰键与编辑命令](https://raw.githubusercontent.com/ChromeDevTools/devtools-protocol/master/pdl/domains/Input.pdl)

CDP只是测试输入工具，隔离依赖实际原生 Job/策略，而不是输入模拟。
