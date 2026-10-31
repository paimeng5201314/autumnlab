# 远端投递排序内核 · G2-B1

制作人：派蒙。本增量在 `src/AutumnOS.Runtime/RuntimeDeliveryCoordinator.cs`，尚未接生产远端宿主、权限提交或账号提交；第三方启动门禁保持拒绝。它提供进程内排序和容量控制，不产生原生 Post、权限授权或操作系统退出证明。

## 投递与所有权

可信宿主登记初始 identity authority、账号/epoch、应用 binding、实例/epoch、文档/代次和 channel 代次。endpoint、ticket、evidence port、fence 和 mutation permit 是带私有 issuer 的对象能力；数字 ID、复制 DTO 或另一 coordinator 的对象不能代替它们。此边界不防御可信父进程内任意反射或 unsafe 代码。

`TryReserve` 先占有有界资源槽。`QueueValidated` 只检查长度和 SHA-256；调用方以后还必须在真实权威租约内完成权限及当前代次校验。每个 ticket 在锁内只领取一次复制权，复制和 hash 在锁外执行；取消、重复调用和已结算 ticket 不得再次读取外部 MemoryManager。原 payload 的副本归 coordinator，底层 driver 的完成表示写入结束并归还内存使用权，不得在返回后保留该内存。

coordinator 管理每个 endpoint 的单一出站队列并在锁外调用 driver。写入完成不等于 WebView2 Post。只有可信原生 peer decoder 持有的 evidence port 才能提交 binding、ID、摘要完全匹配的实际 Post 或明确未 Post 回执。未来 IPC 还必须核验原 peer 身份和消息序号；DeliveryId 在 reservation 时产生，可能有空洞，不能代替发送序号。

回执丢失进入 `Unknown`，保持容量占用并隔离 endpoint；取消等待、超时、断管或窗口关闭请求均不产生未送达或退休事实。迟到且尚未被屏障结算的准确 Post 回执可以完成该 ticket。屏障后或原组退休后结算的未知项分别为 `FencedUnknown`、`RetiredUnknown`，不改写成 `Posted`。

## 变更前屏障

`BeginFenceAsync` 登记 writer 意图，立即阻止相关 scope 的新 reservation，并取消尚未开始发送的工作。identity 覆盖其应用与实例，application scope 包含账号及应用 binding；重叠 writer 按登记顺序推进，不相交的 scope 可独立推进。

屏障进入同一出站队列，携带原 binding、cutoff 和下一 channel 代次。必须先发送屏障，再等待旧未知投递结算；先等待 Unknown 会使丢失 ACK 的路径无法前进。准确原生屏障 ACK 表示旧 cutoff 之后不会再 Post，不能由父端写入完成、JSON 中的 bool 或计时器代替。断管后无法完成屏障时，只能继续等待原进程组的真实退休证据。

全部相关 leg 完成后产生一次 mutation permit。调用方在 `Commit` 内执行短暂的权威状态提交，不得 await、运行 driver 或发布 Changed/Cancel/Publish；这些通知应在 Commit 返回或抛出、屏障释放后另行发送。取消 permit 等待不会撤销 writer 意图；调用方必须继续观察并完成既有操作。同步存档租约和现有同步原生 Post API 不因此改变语义。

## 容量与退休

默认上限为 32 endpoint、每 endpoint 16 ticket、每 payload 32768 字节、32 fence；可信 composition root 可显式调整，参数仍受单 payload 16 MiB、总槽位 payload 64 MiB 和有限 endpoint/fence 的硬上界约束。此未接线默认值不是公开 SDK 响应大小的新限制，生产接线必须按实际协议单独配置及验证。

槽位覆盖复制、排队、写入及 terminal 结果但 buffer 尚未归还的整个期间。早到 Post ACK 不提前返还 driver 仍占用的 buffer；复制中取消也要等复制返回。原组已退休的 endpoint 仍占 endpoint 额度，直到所有 buffer 释放且泵已 idle，防止以反复登记/退休绕过总内存界限。取消排队项同时移除其队列引用，不保留无界历史。

原 lifetime registration 绑定初始 endpoint，且跨 coordinator 只能原子登记一次。它只由可信宿主持有；本阶段没有实现 OS witness。未来 witness 必须来自原进程句柄及原 Job 等实际退出判据，不能重新按 PID 寻找替代对象。Task 失败或取消意味着不可用，永远不算退休。

## 当前验证范围与下一步

`tests/AutumnOS.Tests/RuntimeDeliveryCoordinatorTests.cs` 使用编译后的 Runtime 内核和显式 synthetic driver/lifetime；内部访问只授予 AutumnOS.Tests。测试以受控 TCS、通道和 MemoryManager 组织交错，不启动 OS 隔离、浏览器、LSA 或文件系统探针。外层超时只令测试失败，不生成结算证据。实际构建和测试结果写入对应新 build ID 报告，未执行的原生路径仍为 not_run。

G2-B2 必须把屏障放到所有真实权威提交之前：权限设置及 prompt 后提交、实例关闭/故障、账号切换/退出/刷新失效（包括同 epoch 的 SessionExpired）、guest/preview 状态和 shutdown。不能通过 Changed 事件事后补屏障，不能持现有 Monitor 跨 await，也不能同步阻塞 UI 等待远端 ACK。随后还需真实原生 STA Post receipt、限长 IPC、独立宿主和完整验收，才能替换第三方统一拒绝。
