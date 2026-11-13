# QA04 冻结原生组件限定验证

制作人：派蒙。实际环境：Windows Server 20348、普通Medium用户；不是Win10/11矩阵。产品`T06-20261113-release-v1-qa04`、1.0.0-rc.3、临时platform-test.4。source `sha256:275de93a89c3fda35c779b05a80d071f2b1c6118f741c27e40e9a1c8d1fa2a8d`，native table `3dfd828f582afb4264285aa6fd4a226dbc2bfc8ad11f951c49e5ce7b219ec322`。driver `qa04-negative-01-close-diagnostic`，assembly SHA256 `4182687413b941a82be0e806772f69070edb25bb80b26a9e43cd5aaf2d53208a`。

最终Prepare报告及完整交付文件哈希重新核对后运行；4个引用DLL与最终包逐个相符。先完成唯一normal，按root后续明确指令最多追加3个normal，再按后续明确指令完成3个既定负例；每次全新owner/stage，前项退出和强退休后才运行下一项。没有UI、全量测试或产品构建并行；没有操作既有用户进程。

| Case | 次数 | 秒 | 结果与边界 |
| --- | --- | --- | --- |
| normal-close | 首次 | 40.63 | passed；Closed、强退休、独立原句柄/Job0、stage清理；两个恢复均false。 |
| normal-close | 追加1 | 36.37 | 同上，passed。 |
| normal-close | 追加2 | 38.67 | 同上，passed。 |
| normal-close | 追加3 | 45.66 | 同上，passed；预先限定次数已用完，没有继续重复。 |
| browser-failure | 1 | 36.45 | 仅新原boundBrowser同内核对象TerminateProcess；故障被识别，原Job/站恢复均false；强退休及独立Job0/清理通过。 |
| root-stall | 1 | 62.87 | 仅新原restrictedRoot同内核对象暂停（含STA，并非COM Post调用内部死锁）；真实I/O deadline取消，queued fence无确认；仅产品原Job请求恢复；强退休及独立Job0/清理通过。 |
| pipe-close | 1 | 48.90 | 关闭新owner原pipe；没有把断管当Closed。仅产品原Job请求恢复，最终强退休及独立Job0/清理通过。 |

7次各自都先有真实parser gate、完整PNG解码、STA Posted receipt、页面消息ACK及同序fence正控。所有case的harness救援均false，原station强制恢复均false；产品回收请求本身未当作退出证明。全部owner结束，最后无NativeHost残留。

这些结果不解释qa03 normal-close的真实失败。qa04仅添加脱敏诊断/Failed回执，没有有证据的根因修复；4次正控未复现，未获得NBC1。qa02/qa03全部失败报告保留。精确diff和时间核对见qa03-qa04-diff；所核qa03打包/源码归档写入在失败之前已结束，没有同期CPU/磁盘利用率记录，不作压力因果结论。在上述7项结束时BOUNDED-LOAD-PLAN.md尚为未执行方案；随后明确授权的CPU两项结果见本文后续段落，磁盘方案仍未执行。

不覆盖Shell权限/账号提交及运行/维护租约、真正STA内部死锁、renderer-only故障、真实文件/网络/剪贴板/账号A/B/输入硬件、Win10/11或干净系统。不能据本组件矩阵宣称完整隔离或放行普通第三方。

机器可读总表：qa04-summary.json、qa04-normal-repeat-series.json、qa04-negative-series.json。每次完整report位于这些总表列出的runs路径，未覆写旧结果。

## 后续明确授权的CPU A/B（同一冻结产品，额外2次）

root随后明确授权CPU方案，磁盘方案仍未实现/未运行。新artifact driver/helper均独立编译、零警告零错误并完成只读独立审查。没有改产品源码、deadline、原Job额度或系统全局设置。每项先有原生frame/Post/page ACK/fence，再创建自身新BelowNormal helper；新helper原子进入另一个活动进程1、128MiB、CPU硬限25%的新Job，32MiB缓冲，实际1 worker，共同20秒工作窗口。输入四个文件及manifest以固定SHA编译绑定并持pin，未接受任意路径、PID或EXE。

| 对照 | helper实际工作 | Close实际时间 | Close间原helper句柄CPU增量 |
| --- | --- | --- | --- |
| A control | 20.010秒，仅等待，CPU0.09375秒、0轮 | 8.642567秒 | 0秒 |
| B load | 20.027秒，CPU16.71875秒、78轮SHA-256 | 0.898552秒 | 0.703125秒 |

两项均Closed正常确认、产品强退休和独立原句柄/Job0核验、stage清理通过，产品两个恢复标志false。helper也分别从原Create句柄确认进程/线程退出、独立Job0、exit0、无helper恢复；整个harness没有救援。时间窗口完整覆盖Close，单调耗时和原helper句柄GetProcessTimes增量同时记录。峰值工作集约59MiB，未把配置上限或helper实际CPU冒充宿主整体CPU利用率。

先A后B，各一次，所有owner/helper结束后结束本阶段，没有继续增加轮次。结论仅为本限定CPU负载没有复现qa03失败；A比B慢不能推出压力改善关闭或历史根因。NBC1仍未取得，旧qa03失败和root_cause_fixed=false保持。总表qa04-cpu-ab-series.json、qa04-summary.json；driver/helper源及编译身份位于qa04-load-harness和qa04-cpu-helper，完整两次报告位于cpu-runs。
