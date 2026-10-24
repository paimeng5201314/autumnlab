# 本轮共享浏览器参数复测交接

制作人：派蒙。此文件仅准备运行命令；本次编写没有运行 GUI、UAC 或 probe，没有改 tracked 源码或构建输出。集成负责人在当前 UAC02、产品 UI 操作结束后串行执行。工程已有 probe-build03.log 的正式编译结果，运行器不重复编译。

## 源码核查结果

当前 `RuntimeIsolationPolicy.BrowserArguments` 为 `--force-renderer-accessibility --disable-features=WebRtcHideLocalIpsWithMdns`。

| 模式 | 真实传给 CreateAsync 的参数 | 报告证据含义 |
| --- | --- | --- |
| basic / top / offer | 完整共享常量 | `browser_arguments` 是配置给 API 的附加参数；这几个旧诊断模式只有 observed，不纳入正式 matrix pass |
| runtime / runtime-ab | 完整共享常量 | `browser_arguments` 是配置给 API 的附加参数；未记录最终完整进程命令行 |
| resources-storage | 完整共享常量 | `additional_browser_arguments` 是配置给 API 的附加参数；未记录最终完整进程命令行 |
| network | 共享常量 + 每例 NetLog 路径 + Default capture + no-proxy-server | 顶层字段只用占位描述额外参数；每例 NetLog 的 `constants.clientInfo.command_line` 才是实际完整浏览器命令行 |
| same-origin | 同 network | 顶层字段仍是占位描述；汇总器独立读取每例新 NetLog 核查实际 flags |

源码位置：Program.cs:100/173；RuntimePolicyWindow.cs:58/156；ResourceStorageWindow.cs:101/137；NetworkPolicyWindow.cs:102/247；SameOriginWindow.cs:27/36。每处均在 `tools/AutumnOS.Isolation.Probe/` 下。共享 flags 是辅助兼容配置；正式边界仍是生产响应策略及运行时拦截，不把 flags 单独当网络隔离保证。

## 唯一新目录运行

在工程根 PowerShell 执行，将 `$actualBuildId` 设为当前 Prepare02/Build02 的真实构建 ID，不能使用示例值冒充。

```powershell
$probeRunRoot = Join-Path (Resolve-Path 'artifacts/isolation-20261018').Path ('probe-sharedargs-final-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
& 'artifacts/isolation-20261018/drafts/Invoke-FinalProbeMatrix.ps1' -RunRoot $probeRunRoot -BuildId $actualBuildId
```

顺序为 `runtime-ab`、`resources-storage`、`network`、`same-origin`。运行器仅用当前已编译工具，设置当前进程局部 DOTNET_ROOT 并恢复；不改 PATH。它先记录来源与 EXE/DLL/Runtime 哈希，再为每项创建独立目录、stdout/stderr 与 invocation.json。预先存在的 RunRoot 被拒绝。工具自己的报告覆盖保护继续有效；超期不会 kill，而是停止 matrix 留给集成负责人检查该自有 PID。已有 WebView 环境覆盖存在时拒绝执行，只记录变量名，不输出其值或自动清除。

只运行某一组时可传 `-Modes network`；最终 summary 中其他项目仍为 not_run。缺失报告不可用历史报告补齐。因真实工具 `runtime-ab` 使用普通 Form，运行前仍须由集成负责人协调当前桌面；其余三个模式是非激活屏幕外窗口。

复核已有本轮新目录时，输出另一个不存在的文件名：

```powershell
& 'artifacts/isolation-20261018/drafts/Summarize-FinalProbeMatrix.ps1' -RunRoot $probeRunRoot -OutputPath (Join-Path $probeRunRoot 'summary-review.json')
```

## 必须实际出现的正反例

| 组 | 正向证据 | 拒绝或隔离证据 |
| --- | --- | --- |
| runtime-ab | B 在实例及 origin 绑定下产出 native proof | A 缺少 Connection-Allowlist 时拒绝产出 proof，实际原生挑战已执行 |
| resources-storage | 外置本地 script、内嵌 WASM=42；五类存储自身写入与读回 | 本地 fetch/eval 被拒绝；同 origin 的其他账号/应用/来源绑定无数据；原绑定保持、InPrivate 关闭重开清空；浏览器独立且正常退出 |
| network | 自有 TCP/UDP 接收器前后对照；A 的 15 个通道均有接收端记录；worker/SW 实际执行；DNS 和 preconnect 各自可观察 | B 有已执行的 26+ 通道尝试，接收端 TCP/UDP 增量零，DNS/preconnect 事件零，每通道无接收；正常退出且 NetLog 完整 |
| same-origin | 仅测试 A 把 connect-none 改为 self 后，WSS 和 WT `/owned` 网络服务请求实际建立 | 生产 B 相同脚本确实执行，精确 `/owned` 请求为零；不把初始宿主合成导航的 DNS 计作脚本泄漏，也不宣称总 DNS 为零 |

汇总器在新报告外复核：来源/二进制哈希未改变、每份 report 与 invocation 时间属于本轮、新目录实际 NetLog 中共享 flags 生效、未见 no-sandbox/disable-web-security/ignore-certificate-errors。网络和同源完整实际命令行来自它们自己的 NetLog；资源和启动挑战只证明配置参数与实际引擎运行，不伪称已抓到完整命令行。

## 固定不扩大的结论

独立 matrix 不覆盖最终 EXE/ZIP 产品 UI、ChemLeap 普通模式、SDK 授权存档、真实 Logto 双账号或真实 clipboard A/B。可信证书 HTTP/3 WebTransport 成功握手、外部权威 DNS 接收端仍 not_run。剪贴板使用 UAC02 单独报告；不得拿本工具的异步 clipboard policy/query 只读结果代替真实 copy/cut/paste 验收。

两脚本已做 PowerShell 语法检查。汇总器自检确认：缺少新报告全部 not_run；即使模拟文件写成 passed，只要 timestamp 属旧运行仍拒绝。自检证据位于 `summary-script-selfcheck-adefd67606614c18ac60df5a58936727/selfcheck.json`，明确是脚本自检，不是产品隔离证据。
