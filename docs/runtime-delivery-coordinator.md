# 远端投递排序内核 · G2-B1

制作人：派蒙。排序内核在 `src/AutumnOS.Runtime/RuntimeDeliveryCoordinator.cs`。2026-11-04 新增宿主专用 `IRuntimeAuthorityMutations` 与 `RuntimeAuthorityMutationCoordinator`，已将权限、身份和实例权威提交接到屏障之前；真实远端宿主和原生 peer decoder 仍未安装，第三方启动门禁保持拒绝。此代码不生成原生 Post 或操作系统退出证明。

## 本地接续与提交接线（2026-11-04）

指定分支 `codex/windows-exe-cloud-20261031` 的实际 head 为 `6f634fa7c3f7457b32de6a5aec061cfddbc5a969`。该提交已含 max-channel sentinel 候选，新的确定性回归覆盖 Max−1 双 writer 与 Max 退休/持有 writer，不向 driver 发送零代次，不将取消等待改为退休。初次运行发现原 OrderedFence 测试漏算已取消 reservation 的编号；保留真实 cutoff，修正精确预期并加强 ACK 前 Unknown 断言。B1 新构建 `T06-20261104-g2b1-02` 已完成1014项C#、9组自动检查、106项静态检查；只属于其冻结源码及显式 synthetic Medium 测试基线，不是普通用户或原生验收。首轮失败保留。

`BeginAsync` 返回即表示 writer intent 已注册，之后 owner 必须保留 operation 并调用一次 `CommitAsync`。等待 permit 不可取消，业务取消由 commit action 内重新检查并显式 no-op/抛出，仍消费许可。短 action 内只持已有权威锁并重验账号/epoch/revision；通知、取消回调和 Dispose 在 permit 释放后执行。旧同步入口只在全局无 endpoint、无 writer 时原子安装并消费本地许可；否则明确报异步必需，不同步等待原生 ACK。

权限 prompt、设置和预览强制拒绝在真实写入前经过 application scope。身份恢复、登录/刷新/记住登录/退出及 shutdown 经过 identity scope；AuthorityRevision 独立于 session epoch，覆盖同 epoch 的 SessionExpired 和 profile 更新。已验证的轮换凭据不因随后等待屏障时 HTTP deadline 或 caller 取消而丢失，但原 session/epoch 已变时不会写回。初始登录也预留可取消 owner，避免等待首个屏障时取消被丢弃。

实例生命周期和 guest 决定使用 instance scope。关闭分为 `BeginCloseAsync` 与幂等 owner 的 `CompleteAsync`：前者安装 intent 并 quarantine，原生 owner 此后可以启动 Close/Detach；后者等真实屏障或原组退休再正式提交终态。Shell 保留关闭、回调和可见性任务，原 backend 资源事实、正式关闭、drain 及 buffer idle 都满足后才释放维护租约。请求关闭、断管或超时不能替代这些事实。

响应通过 `HandleMessageForDeliveryAsync` 获得与实际结果摘要绑定的 `RuntimeResponseStamp`，最终 `DeliverResponse` 在原生调用栈内复核；SDK 事件保留产生时 stamp，生命周期消息也走同一最终 gate。stamp 分开记录权限、生命周期、guest 与 identity snapshot/revision，避免 requestProfile 自己的授权和刷新被简单的单一代次加一规则误判，也避免给旧 payload 换上新 stamp。

live 开发预览和真实账号/权限共享主 coordinator；测试账号的 scope 由宿主创建的 `developer-preview:` 绑定路由到该预览独立 coordinator。旧预览 host 的原对象由退休 drain 持有，资源未释放时不销毁其权限 wrapper，包括切换被取消或 shutdown 的路径。

B2 本轮真实构建/测试以 `artifacts/builds/` 新 ID 和 `AUTUMNOS_PROGRESS.md` 最新记录为准。新纯回归里的 driver、lifetime、gate 都是显式测试替身，不是 OS 或实际 STA receipt。

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

G2-B2 的提交接线不能通过 Changed 事件事后补屏障，不能持现有 Monitor 跨 await，也不能同步阻塞 UI 等待远端 ACK。后续仍需把注册口连到真实原生 STA Post receipt、限长 IPC 和独立宿主，完成实际退出/失败及全部边界矩阵，才能替换第三方统一拒绝。
