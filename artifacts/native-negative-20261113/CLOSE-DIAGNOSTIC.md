# QA04 关闭诊断增量

本页保留QA04构建前的诊断设计及原先一次正控计划；后来经root明确追加的限定重复、负例及CPU A/B实际结果见QA04-RESULTS.md，QA05结果另见QA05-RESULTS.md。下文计划不替代这些实际报告，qa03旧失败仍未解释。

制作人：派蒙。qa03 第1次 normal-close 失败保留；后续测试未运行。本次仅增加可复现诊断，不增加超时，不放宽 Closed 或原组退休条件。产品源冻结后由 root 新建 `T06-20261113-release-v1-qa04` / `1.0.0-rc.3` / `platform-test.4`。

关闭不能确认或发生异常时，原请求只返回一份 Failed。任何 Closed/Failed 写入已经开始后，不追加第二包，以免部分写与重发产生歧义。断管仍不能保证诊断送达，未收到回执仍是未知。

固定诊断为最多128字节的可打印ASCII：

`NBC1|step|reason|operation_hresult|original_browser|sdk_count|sdk_query_hresult|exit_kind`

step仅revoke/delivery_drain/controller/streams/observe/reader/reply/release；reason仅error/timeout/unconfirmed；HRESULT为8位小写十六进制。原browser状态为unknown/live/exited；SDK count为非负十进制或`-`未知；SDK查询未进行用`-`，成功才为00000000，异常保留数值HRESULT且count未知。exit_kind仅none/normal/failed/other，PID仍只用于既有原browser事件过滤，不输出PID、UDF、URL、nonce、异常消息或内容。

例如 `NBC1|observe|timeout|00000000|live|-|-|none` 表示原browser在本地观察期内仍未退出，SDK未查询、退出事件未收到；不能推断SDK为空。`NBC1|observe|error|80004005|exited|-|80004005|normal` 表示原句柄已退出但SDK查询异常，不能回Closed。

原有SDK查询短路保持：原browser signaled后才调用GetProcessInfos，以免仅为诊断添加更早COM调用或异常。Closed仍要求原browser句柄signaled、SDK查询成功且count0、原browser的Normal退出事件，三者同时实际满足。父进程另需原station process/thread、原Job0、全部已观察句柄信号及上下文消失才退休；本诊断不是原组退休证明。

固定SDK1.0.4258.31本地NuGet XML与微软官方文档交叉核对：

- [Controller.Close](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.close?view=webview2-dotnet-1.0.4258.31)：同步关闭控制器并释放WebView关联事件；不意味着所有环境资源立即已退出。
- [Environment.BrowserProcessExited](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.browserprocessexited?view=webview2-dotnet-1.0.4258.31)：相关运行时进程及资源释放后触发，属于Environment事件。
- [Environment.GetProcessInfos](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getprocessinfos?view=webview2-dotnet-1.0.4258.31)：列同UDF进程，排除crashpad；异常不能解释为列表为空。

纯诊断专属测试9/9通过（旧5+新增4），见close-diagnostic-pure-result.json；不运行owner，不能代替qa04真实关闭。qa04成功Build/Test/最终Prepare之后，固定实际source/table/hash新编译harness，只跑1次新normal-close取NBC1。root通知前不启动owner，不与UI或全量测试并行；无论通过/失败均保留报告，不自动重试覆盖。
