# 隔离修复与本地测试入口（2026-11-03）

制作人：派蒙。本轮在当前工作区继续本地开发，不上传、不发布。

## 2026-11-04 最新实测增量

本节更新后面的初轮历史状态。受限进程初始化问题已有真实修复证据，完整第三方隔离仍未完成。

- 合成 LUA 副本保留了原 High 的新对象默认 DACL（Admins/System GA、logon RX），但 Admins 已变 deny-only，导致 `CreatePipe` 拒绝及原路径 `0xC0000142`。现在仅给独立新 token 副本设置 user+System 两条 GA 的 DefaultDacl，精确回读并核对原 token 不变；原有带管道启动路径已进入 .NET 并正常回传。
- 可信暂存 apphost 设置 Low 标签会导致 `.NET CurrentHostFindFailure (0x80008085)`。两个独立对照证明，只把新 apphost 改为 Medium/NW、保持代码及 DACL 不变，即可进入 .NET；单独添加 System RX 无效，没有集成该权限试探。新工具保留可信代码/运行时及目录 Medium/NW，独立 writable temp Low/NW。
- AppContainer 创建所需 Windows 环境已使用明确白名单补齐；不复制凭据、代理、CLR profiler 或 startup hook。固定 token 类直接按实际缓冲查询，不适用/不支持的 API 结果明确记录。
- `lpac-native-query-01` 的 A 真实正控通过；B/C 均已成功创建、进入 .NET、自然退出并回传实际 Low AppContainer 身份和精确 capability。当前 Windows Server 10.0.20348 对 class46 的 Win32 查询返回87，native 查询返回 `STATUS_INVALID_INFO_CLASS (0xC0000003)`，因此 `is_lpac=null`，两例仍失败。B/C 的剪贴板站句柄正控也未成立，不能据 API 拒绝宣称完整剪贴板隔离。

逐次实际报告在 `artifacts/isolation-20261103/`；旧失败均保留。统一汇总为该目录的 `HANDOFF.json`，最新进度以工程根 `AUTUMNOS_PROGRESS.md` 头部为准。合成基线不代表真实 UAC Limited 用户或产品 GUI 验收。

接续仍需在目标 Windows 10/11 普通用户环境验证 LPAC 状态与能力，再实现并实测浏览器宿主、所有实际子进程 token、内部呈现/输入/SDK、独立合成剪贴板的 copy/cut/paste 正反对照及网络回归。当前 Shell 的外部应用放行条件保持失败关闭。

## 本地源码与工具

按本轮派蒙明确要求，已从 GitHub 固定提交 `22937b3ad217d6024ae9de38f7e85ea5b5f5b051` 导入完整工作树归档。原归档、SHA-256 和导入记录在 `artifacts/repository-import/`；没有建立 Git 历史或覆盖原有工程文件。

项目内 `.tools/dotnet` 已安装固定 SDK 10.0.401，SHA-512 校验通过；NuGet 按既有锁恢复，合同工具使用锁定 npm/TypeScript。固定 WebView2 154.0.4258.53 已下载、解包并验证微软 Authenticode 与实际版本，来源记录在 `.tools/webview2/154.0.4258.53/provenance.json`。大文件直连失败保留；分段传输后的归档仍通过同一正式缓存脚本验签，不关闭 TLS 或签名检查。不修改系统 PATH。

## 产品自动测试

在**普通、非提升且没有额外限制 SID 的 Windows 用户会话**，从工程根执行：

```powershell
./scripts/Test-ExecutionContext.ps1 -ReportPath artifacts/my-new-context.json -RequireStandardUser
./scripts/Build.ps1 -BuildId T06-my-unique-build -BuildVersion 0.5.2-local.10 -ReleaseLabel 0.0.6-isolation-dev
./scripts/Test.ps1
```

报告文件和构建 ID 必须未使用过。`Test.ps1` 现在先核验实际 elevation、integrity、限制 SID 和默认对象 owner，再启动测试。它不创建账号、降权或修改调用者 token。当前代理环境是 Windows 10.0.20348 的 High 管理员，elevation type 为 Default，没有可用 linked Medium；直接执行的结果不能冒充普通用户验收。

初次跟踪构建 `T06-20261103-isolation-01` Release 为 0 warnings / 0 errors。其管理员环境 C# 回归实际 **869/890**，21 项失败包含私有签名目录 ACL、当前用户管道与 owner 夹具；完整原始结果保留在该构建 `test-runs/`。这些失败不删除，正式打包门禁未放宽。后续前置拦截保持旧失败报告，生成新的 `test-context-*.json`。

## 独立隔离机制测试

统一入口：

```powershell
# 只编译，不启动机制探针。
./scripts/Test-IsolationMechanisms.ps1 -BuildOnly

# 普通用户基线：自有 LPAC A/B/C 元数据试验。
./scripts/Test-IsolationMechanisms.ps1

# 可选：同时运行新私有 desktop 的控制台机制诊断。
./scripts/Test-IsolationMechanisms.ps1 -IncludeRestrictedDesktop
```

每次在 `artifacts/isolation-mechanisms/<unique>/` 保存新报告。LPAC A/B/C 分别为普通进程、无剪贴板 capability 的 LPAC、仅授予 `lpacClipboard` 的 LPAC；校验真实 token、nonce/PID、自然退出和 WinSta0 句柄权限。这些工具不访问剪贴板内容、不加载第三方内容、不启动 WebView，不形成产品放行证明。

`research/isolation-20261103/` 还保留：

- `RestrictedDesktopLaunchProbe`：修正丢失的历史探针入口，区分 `0xC0000142`、WAIT_FAILED、超时和无元数据；只在新对象创建时配置 DACL。
- `StandardUserLauncher`：只使用现有本人 linked Medium 或当前普通 token；没有 linked token 时明确失败，无自动 fallback。
- `LuaTokenProbe`：只创建自身 flags5 token 副本、仅给副本设置 Medium、回读并关闭，不启动进程。实际 High 句柄诊断已证明原 token 事实不变。
- `SyntheticLuaLauncher`：显式合成 LUA 实验基线；它不是实际 UAC Limited 用户，也不能验收产品 GUI。用途和目标限制见其 `REVIEW.md`。

需要在上述特殊管理员实验环境复现时，工程人员可显式选择：

```powershell
./scripts/Test-IsolationMechanisms.ps1 -SyntheticLuaBaseline
```

该参数永远保留 `real_user_desktop_acceptance=not_run`。初轮已重现新 Medium 子进程在 CLR 前以 `0xC0000142` 退出，不能将“进程创建成功”写成机制通过。最新逐次实际结果见 `artifacts/isolation-20261103/`，失败目录不复用。

## 当前安全状态与接续

外部包和开发预览仍在 WebView 创建前返回 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`；ChemLeap 普通模式尚未放行。已有 Job UI=6 的 copy/cut 失败结论不变。

微软 WebView2 团队说明普通 restricted-token 宿主可能妨碍 renderer sandbox 初始化；Chromium 设计说明 LPAC 有独立 `lpacClipboard` capability。这只是下一条待实证路线：先证明受限宿主能初始化，再核验实际浏览器/renderer/GPU token、正常 JS/WASM/WebGL 和输入，完成真实 legacy/async clipboard A/B 及网络回归后，才能接入 Shell 并改变放行逻辑。不能只修改能力布尔值、报告或开发模式。

依据：[微软 WebView2 #4850](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4850)、[Chromium LPAC 设计](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/docs/design/sandbox.md#Less-Privileged-App-Container_LPAC)。
