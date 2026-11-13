# QA03 原生关闭与负面矩阵

制作人：派蒙。仅测试新冻结的 `T06-20261113-release-v1-qa03` / `1.0.0-rc.2`；不转记 QA02 成绩。QA02 报告原样保留，其中第二次 normal-close 失败属于真实历史失败。

当前状态：已读取成功 Build 及九组全通过报告并在准备目录产生后编译 harness，Release 零警告零错误；四个冻结引用 DLL 逐个 hash 相符。最终 package-prepared.json 和完整交付 hash 再核后，**第1次 normal-close 失败并已停止，后续2次 normal 和3项负例全部 not_run**。失败报告 runs/normal-close-9d730f88128b4cfebf2bdb421616186b/result.json：frame、STA Post、页面 ACK、fence 均通过，Close 得到 EndOfStreamException、没有 Closed；两个 recovery 标志 false、helper 救援 false，最终原 station process/thread、Job0、ResourcesRetired 及 stage 清理均 true。不能据最终退休改记正常关闭通过；本次仍未证实子端 clean/Closed 失败的具体内部条件。Prepare-Qa03Harness.ps1 只在成功 build-result、source/table 一致且完整准备目录存在后，从已审阅 QA02 driver 翻译出固定 QA03 身份；脚本本身不运行构建或 owner。实际 source/table/hash 见 qa03-harness/preparation.json 与 compile-only.json。旧入口 preflight 对话框已由 root 正常关闭；未创建 owner 时的预检停止记录保留，不当 native case 失败。

本次产品变更：Close 在途保留有界 15 秒原 Exchange 期限；真实完成、失败或显式 EOF 后才开始独立 12 秒原 Job 回收宽限。迟到 continuation 不能延后原始 Close+15 秒边界。Shell 已由 root 移除独立 12 秒强断管 callback。预算和回收请求不证明 Closed、退出或清理。

每 case 都新建 synthetic package、封存 stage 和 owner，先完成真实 parser gate、完整 PNG 解码 800×600、真实 STA Posted receipt、页面消息 ACK 和同序 fence，再进行动作。

| 顺序 | Case | 动作和通过条件 |
| --- | --- | --- |
| 1 | normal-close | Close 必须返回 Closed；原 station process/thread、复制的原 root/browser 句柄均退出，原 Job Active0/PID空、ResourcesRetired、封存清理完成；两个 recovery 标志均 false。 |
| 2 | browser-failure | 仅终止本次新 owner 原 boundBrowser 同一内核对象；等待同一强退休和封存清理。 |
| 3 | root-stall | 仅暂停本次原 restrictedRoot（含 STA）；Delivery 与 queued fence 不得获得确认，实际 I/O deadline 后产品仅向原 Job 请求回收，再观察强退休和清理。 |
| 4 | pipe-close | 仅对本次原 owner 调 RequestCloseAsync；断管本身不当 Closed/退出，等待强退休与清理。 |

normal-close 可按 root 指令重复正控；每项一旦失败就保留报告并停止后续项。helper 救援只能作用于本次复制的原 Job，任何救援均使 case failed。未能证明原组/上下文退出时保留 owner/pins 和 PENDING 记录，不释放 stage，也不运行下一项。

驱动保留 KernelBase.dll 的 CompareObjectHandles 导出预检，升级原进程权限后必须与原复制句柄比较同一内核对象，无 PID/创建时间降级；没有现有用户 PID 或任意程序目标参数。StartAsync 完成后才 Capture，并在原 peer lock 内再次要求 live original station、原 root/browser、原 Query Job。无参数运行 inert。

本矩阵仅为冻结原生组件验证，不观察 Shell 运行/维护租约或真实权限/账号提交，不证明硬件输入、IME、Post 调用内部死锁、renderer-only 故障或 Win10/11 全矩阵；实际环境 Server20348 不改称 Win10/11。完整隔离仍未完成。

root 审阅并确认全量自动测试结束、无 UI/其他 owner 后，才能编译并逐项执行：

```powershell
. ./scripts/Common.ps1
$probe = Join-Path $ProjectRoot 'artifacts/native-negative-20261113/qa03-harness/bin/Release/net10.0-windows/win-x64/AutumnOS.Qa03.NativeNegative.exe'
$prepared = Join-Path $ProjectRoot 'artifacts/packages/T06-20261113-release-v1-qa03/AutumnOS-1.0.0-rc.2-win-x64-development'
& $probe --execute normal-close --prepared $prepared --workspace $ProjectRoot --reviewed-qa03-owned-only
```

报告输出到新 `artifacts/native-negative-20261113/runs/<case>-<guid>/result.json`，scratch 仅在新 `artifacts/neg-qa03/<guid>`；不操作用户 Data、凭据或既有实例。
