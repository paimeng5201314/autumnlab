# QA02 原生负面矩阵（待 root 审阅及串行执行）

历史计划说明：本页“尚未执行/待审阅”描述2026-11-12准备时点，不是当前状态。其后实际尝试及失败保留在后续交接记录；新QA03/04/05限定结果见同级后一天目录的PUBLIC-NATIVE-RESULTS.json、QA04-RESULTS.md及QA05-RESULTS.md。旧失败不能由后续通过覆盖，原始进程报告不包含在公开源码补充中。

制作人：派蒙。产品源和共享输出未修改。此目录的 harness 仅引用打包后的 QA02 冻结 DLL，不引用正在被 prod01 构建覆盖的 src/bin。

固定产品：`T06-20261112-release-v1-qa02`，源 `sha256:51f74ba89506bcef946eb9341d0f2c44a5c5bb0aa4ab3f1e21a412dd4ed7aa0b`。固定 native-payload table SHA `c9c15fec51c37d1e2b1de41348cc9991209b12ef5c857038f14a0bb138bcc596`。QA02是临时平台验收字节；Server20348不能当作Win10/11证据。

当前状态：harness Release 编译零警告/错误；**尚未执行任何 owner、LSA、USER/browser 或故障注入**。必须先由 root 审阅，再在 UI 测试结束后串行执行。

## 当前 kernel probe 的边界

`tests/AutumnOS.OwnedJob.Probe` 的四项结果只验证新内核 Job、固定自有子进程和 OwnedJobPolicy。它不调用 QA02 的 NativeProcessOwner、BrowserWindow、STA Post、profile清理或Shell租约。不能重命名成QA02产品隔离通过。重新编译该probe也不会扩大其覆盖范围。

## 此 harness 的正控和负例

每个case全新建立合成 package、封存 stage 和 NativeProcessOwner，输入host/runtime逐文件匹配QA02表。先完成真实parser gate、PNG完整解码800×600、实际STA Posted receipt、页面消息回读和同序fence receipt，然后才故障注入。任一正控失败，本case不能用来证明负例。

| Case | 实际动作 | 必须观察 | 不能推导 |
| --- | --- | --- | --- |
| normal-close | 原owner的Close交换 | Closed ACK，原station进程/线程、原root/browser持有句柄、Job Active0/PID空、产品ResourcesRetired、封存目录清理，并且OwnedJobRecoveryRequested和OriginalStationRecoveryRequested均false | 完整Shell/硬件/跨系统验收 |
| browser-failure | 仅新建实例原boundBrowser内核对象的TerminateProcess | 真实故障引发原管道/组关闭，仍等待同一强退休条件 | 所有renderer-only/utility-only异常 |
| root-stall | 仅新建实例原restrictedRoot进程暂停；发送有效Delivery及排队fence | 产品实际15秒I/O取消，无native receipt/fence ACK；随后产品12秒原Job回收，完整强退休 | COM Post调用内部死锁、真实权限提交行为 |
| pipe-close | 调用该新owner的RequestCloseAsync，关闭其原pipe | 无Close ACK冒充成功，原对象真实退休及清理 | 正在Post时丢ACK/迟到ACK的全部交错 |

root-stall暂停整个原root（包括STA），不是仅STA线程的锁死。NtSuspendProcess是测试专用、非公开稳定合同的原生入口；不支持/拒绝时case不能记passed。它不进入产品源。COM Post内部已开始的死锁需要专门受控hook或另一冻结候选，当前明确not_run。

## 原始对象与注入约束

1. harness不接受PID、process name或任意EXE参数。只对本次Create/Start产生的owner工作。
   StartAsync成功路径先接收BrowserControllerReady，绑定原boundBrowser并执行RequireOriginalPeer后才返回；Capture随后在原peer锁内再次RequireOriginalPeer，并显式要求live station、原Query Job及非空root/browser/witness，缺一就拒绝，不以过早snapshot代替绑定。
2. 在owner的原peer锁内复制其原root/browser/Job句柄，避免退休关闭造成句柄值复用。
3. 故障所需权限若须OpenProcess升级，必须先通过 `CompareObjectHandles` 与原始复制句柄确认**同一内核对象**，然后才操作该升级句柄；只看PID/创建时间不够。权限或比较失败就拒绝，不提权、不改DACL、不启用SeDebug。
4. Job复制件保持原Query|Terminate权限；没有assign/set限额能力。故障和救援都只能指向该新原Job。
5. 如果产品60秒内不能退休，则case失败；helper关闭原pipe后继续等待。额外35秒后的harness救援绝不变成产品passed。仍无法证明清理时写PENDING-OWNER并保留owner/pins等待，不能继续下个case或Dispose stage。

Microsoft 对同对象比较的说明：[CompareObjectHandles](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-compareobjecthandles)。

首轮browser-failure在故障注入前遇到EntryPointNotFoundException，未终止浏览器；原owner随后完整退休并清理，失败报告保留。官方API要求Kernelbase.dll，本机只读导出核对也确认KernelBase存在、kernel32没有该导出。修订driver `qa02-negative-02-kernelbase`仅更正harness DLL声明，并在owner创建前用NativeLibrary.TryGetExport检查；不支持就拒绝，没有PID/时间猜测降级。新driver需从normal-close重新正控，旧分数不转记。

## 最短执行方式（root 审阅后）

在项目根已加载 `scripts/Common.ps1` 的独立普通Medium PowerShell中，保持DOTNET_ROOT指向固定SDK。不要从src/bin取DLL，不与当前用户游戏实例互换，也不要修改QA02完整安装。

```powershell
$probe = Join-Path $ProjectRoot 'artifacts/native-negative-20261112/qa02-harness/bin/Release/net10.0-windows/win-x64/AutumnOS.Qa02.NativeNegative.exe'
$prepared = Join-Path $ProjectRoot 'artifacts/packages/T06-20261112-release-v1-qa02/AutumnOS-1.0.0-rc.1-win-x64-development'
& $probe --execute normal-close --prepared $prepared --workspace $ProjectRoot --reviewed-qa02-owned-only
# 只有前项完整退出后，再逐个执行 browser-failure、root-stall、pipe-close。
```

无参数入口inert；没有定时任务、环境变量放行或现有用户进程目标。每case在 `artifacts/native-negative-20261112/runs/<case>-<guid>/result.json` 输出脱敏结果。scratch在 `artifacts/neg-qa02/<guid>`；仅通过RuntimeHostStaging原owner清理自己的新对象，不递归删除用户目录。故障case保存产品snapshot和独立持有句柄/Job0核验；无用户输入、token、profile路径或nonce写入报告。

结果只覆盖QA02原生组件及其实际字节。Shell权限/账号最终提交、运行/维护租约、UI故障恢复、真实硬件输入及Win10/11需另验；不能把本矩阵和UI正控拼成未执行过的全量隔离成绩。
