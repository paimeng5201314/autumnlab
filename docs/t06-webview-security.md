# T06 原生 WebView 负面验证

`scripts/Test-T06WebViewSecurity.ps1` 在最终交付完整副本中执行；已有任何 AutumnOS 实例时拒绝开始。它通过随包开发者 CLI 校验、打包一个无声明权限的自建应用，经过真实原生确认进入普通开发预览的 `WebAppHost`。没有测试专用运行权限，没有修改 CSP、宿主资源响应或系统安全设置。维护源码位于 `scripts/security-probes/`，全部辅助路径来自参数或脚本目录。

驱动只绑定自己创建的 `127.0.0.1` 随机端口监听器。独立 `control-before` 与 `control-after` 请求证明监听正常；每次演练另有随机 correlation token，零请求断言仅统计该 token 下的 `/probe/` 路由。应用通过真实点击尝试 fetch、XHR、beacon、WebSocket、img、iframe；保存页面自己观察到的 CSP 事件和动作结果，再检查监听请求表。

新窗口和导航分别通过原生按钮点击，必须捕获相同运行实例的 `new_window_denied`、`navigation_denied` 日志。下载仅使用应用自己创建的无害文本 Blob。如果先被导航层阻止，报告将下载回调标为 `not_run_blocked_by_navigation_first`，不能把它称为 `DownloadStarting` 已验证。没有调用系统自定义协议，也没有请求任意宿主命令；这两个边界明确 `not_run`。测试不操作用户浏览器或其他窗口，不按名称终止进程。

执行须与集成、性能、开发者和故障驱动串行，由本地集成负责人调度：

```powershell
pwsh -NoProfile -File scripts/Test-T06WebViewSecurity.ps1 `
  -ExecutablePath '<最终完整交付>/AutumnOS.exe' `
  -ReportDirectory '<全新证据目录>'
```

报告名为 `webview-security.json`，保留实际构建、包哈希、调用记录、原生拒绝事件、正控制和探测请求表、客户区截图、精确进程与清理结果。失败目录保留。只正常关闭本次拥有的预览和宿主，不强杀。

这些是固定 WebView2 版本、当前机器与列出的 API 的实际负面断言。它们不等于完整第三方网络沙箱验收，不覆盖任意新浏览器能力、跨账号、系统协议、任意代码执行、全部下载来源或外部新人测试，不据此开放一般社区应用运行或把 G09 整项改为通过。
