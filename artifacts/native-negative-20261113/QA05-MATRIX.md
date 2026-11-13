# QA05 冻结原生组件回归

制作人：派蒙。目标`T06-20261113-release-v1-qa05` / `1.0.0-rc.4`。先只准备模板；实际source/table及目录字节身份须在成功Build/Test/最终Prepare后读取并固定。root明确通知完整Test/Prepare结束前，不编译最终包引用、不启动owner。

最终状态：已在root确认全量Test/Prepare及单EXE打包结束后执行限定4项，各一次均passed，全部原组强退休、独立原句柄/Job0和stage删除，无harness救援/站进程恢复；正常关闭不使用Job恢复。最后核无NativeHost或CPU helper残留，已交回root开始真实UI。完整身份、范围与结果见QA05-RESULTS.md和qa05-series.json。下文保留预先声明的执行合同，不将旧qa03失败覆盖。

本次只执行预先限定的4项，各一次，严格串行且首个失败立即停止：normal-close、browser-failure、root-stall、pipe-close。每项先验证真实parser gate、完整PNG、STA Posted receipt、页面ACK、fence；之后才执行动作。下一项须等当前产品强退休、独立复制原句柄退出/Job0、stage清理完成。

normal必须Closed且产品两个恢复标志false；负例不能把断管、deadline、入队或回收请求当成ACK/退出。只针对本次新owner的原内核对象，升级句柄必须CompareObjectHandles确认同对象，无PID猜测或任意目标参数。helper/harness救援不能使case通过。

不复制/执行CPU helper，不重复qa04反复normal实验。qa02/qa03/qa04原材料全部保留；qa03正常关闭失败仍未定位，不能由新版本通过覆盖。qa05原生行为据root变更范围与qa04相同，新的版本、更新迁移及Bootstrap变化仍须以qa05实际构建字节验证，不继承旧分数。

所有owner真实退出、资源与stage全部退休后通知root才开始UI；运行期间不与UI、全量测试或产品构建并行。仅Windows Server20348组件级证据，不冒充Win10/11、完整权限/账号提交、Shell租约或完整隔离矩阵。
