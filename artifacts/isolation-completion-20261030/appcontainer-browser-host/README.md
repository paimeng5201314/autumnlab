# AutumnOS 自有浏览器机制子程序

制作人：派蒙。本目录是独立诊断代码，不进入产品解决方案，不解除 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`。旧草案与旧失败证据保持原字节。此子程序不创建 AppContainer profile、不调整 token/ACL、不操作剪贴板、不注入鼠标键盘、不读取实际账户或存档。

普通 A 与 AppContainer B 必须使用同一份发布目录及逐文件摘要。B 的身份必须由普通权限父审计器从原始进程句柄和实际浏览器/renderer/utility 句柄读取；子程序的 stdout、网页消息及 SDK 枚举都不能作为 token 证明。此步骤只检验真实浏览器初始化、受控页面执行与生命周期，不证明剪贴板边界或产品隔离已完成。

## 固定构建输入

- 项目内 SDK：`.tools/dotnet`，由工程 `global.json` 固定 `10.0.401`。
- TFM/RID：`net10.0-windows` / `win-x64`；自包含 `.NET 10.0.12`，无 trimming、无单文件重打包。
- WebView2 SDK：`1.0.4258.31`；固定浏览器：`154.0.4258.53`。
- `FrozenPolicy.cs` 必须与当前生产 `RuntimeIsolationPolicy.cs` 的对应常量逐项比较；保留 Chromium 默认沙箱。
- `Directory.Build.props` 的 `AssemblyMetadata` 使用 `ItemGroup`。此目录不导入或修改根公共构建属性。
- `packages.lock.json` 与构建输入/发布文件摘要由负责构建的集成负责人实际生成。未执行构建时不能填通过。

由根负责人提供的构建脚本负责实际命令、源输入摘要、锁定恢复与发布清单。首次固定依赖恢复生成锁后，必须再次 `--locked-mode`，再 `publish -c Release -r win-x64 --self-contained true --no-restore`。只编译不会打开窗口；不要把此项目的 `dotnet run` 当作纯编译。

## 启动和管道协议

只由经过审阅和相应授权的普通父审计器启动：

```text
AutumnOS.OwnedAppContainerBrowserHost.exe --execute <32位小写hex nonce> <固定Runtime绝对目录> <已存在的新UDF绝对目录>
```

两个路径由父程序提供，均须已存在；子程序不复制引擎、不注册 profile、不修改目录 ACL。运行环境含任意 `WEBVIEW2_` 环境覆盖时拒绝；适用注册表覆盖及实际映像身份仍由父程序独立核对。实际引擎字符串、UDF 与 InPrivate 再由子程序核验。

stdin 是严格 UTF-8 JSONL，每行含终止换行最多1024字节内容，接受 LF 或 CRLF。每行必须恰好三个不重复字段，不能添加新字段：

```json
{"schema_version":1,"nonce":"与启动一致的nonce","command":"load_owned_page"}
{"schema_version":1,"nonce":"与启动一致的nonce","command":"close"}
```

父程序收到 `controller_ready` 后先审计真实进程身份，再发送一次 `load_owned_page`。收到 `owned_page_complete` 及所需进程证据后发送 `close`。无效命令、提早加载、重复加载、绑定错误、过长、EOF、初始化或页面故障均请求正常关闭并使运行失败。已收到关闭请求后的 EOF 不反向改变结果。

stdout 是无 BOM 的 UTF-8 JSONL，每行最多64KiB，公共字段：

```json
{"schema_version":1,"nonce":"...","host_pid":1234,"elapsed_ms":100,"kind":"...","detail":{},"isolation_acceptance":"not_claimed_host_output_only"}
```

父程序必须持续读取 stdout/stderr，不能令管道写满阻塞子程序。重要事件：

| kind | detail |
| --- | --- |
| `host_started` | 输入路径SHA256绑定、策略、上限及明确非产品验收说明；不输出含用户名的绝对路径 |
| `initialization_begin` | 固定 SDK/Runtime |
| `process_inventory` | `cause`、`browser_pid`、`rows:[{pid,kind}]`、`truncated`；SDK不枚举crashpad，父程序还需独立OS后代观察 |
| `controller_ready` | `browser_pid`、实际`browser_version`、`udf_binding_sha256`、`udf_matches_requested`、`in_private`、`policy_id` |
| `owned_navigation_requested` | 父程序许可加载的时点 |
| `owned_resource_served` | 自有HTML/JS资源种类、状态、字节数 |
| `owned_page_complete` | `wasm:42`、`frames:60`、原`browser_pid`；只来自原生网页消息回调 |
| `normal_close_requested` | 原因、是否故障、当前初始化阶段 |
| `controller_disposed` | 控件已释放与原`browser_pid` |
| `browser_group_exit` | 原生`pid`与`kind`，通过要求精确原browser PID的`Normal` |
| `host_complete` | 下述固定完成字段与完整最多128 PID的SDK ledger |

`host_complete.detail` 固定包含 `initialization_succeeded:boolean`、`page_succeeded:boolean`、`normal_browser_exit:boolean`、`environment_process_count:int`（读取失败为-1）、`cleanup_status:"passed"|"unproven"|"failed"`、`overall_status`、`close_reason`、`failure_seen`、`browser_pid`、`controller_disposed`、`initialization_returned`、`parent_close_received`、`ledger:[{pid,kind,first_seen_ms,last_seen_ms}]`、`inventory_truncated`。任何枚举截断或读取失败不能通过。

正常成功退出码0要求全部初始化、页面执行、父程序正常关闭命令、精确Normal事件与SDK0进程；仍只是机制子程序成绩。故障退出1；启动参数不符退出64。父程序另需确认所有原始句柄退出和真实OS后代清零，才能将清理记为完成或删除新对象。

## 有界关闭和证据限制

初始化预算25秒、总观察预算60秒，随后正常关闭观察最多12秒。尚未完成异步初始化或迟迟没有Normal事件时，不能无限保留消息循环。子程序会尝试释放控件、输出 `cleanup_status:unproven` 并自然返回1；不强杀浏览器，不把超时改成通过。父程序必须继续保留profile/UDF/目录并观察真实句柄和后代，直到证实全部退出；不能因host结束就删除。

窗口禁止自动激活且不出现在任务栏，没有输入模拟。页面仅自有HTML、外链内嵌JS资源、canvas 60帧与包内字节WASM返回42；不调用剪贴板、fetch、socket、worker、SDK、设备接口或任意用户文件。浏览器内部辅助进程/系统活动不由这段页面声明排除，父程序必须实际观察。

产品完整RTC挑战、真实剪贴板读写/清空、第三方包、内部嵌入、键鼠/中文IME、账号隔离、SDK权限和生产IPC均不在本次子程序成绩内。以上均保持 `not_run`，不得以本程序成功解除产品门禁。
