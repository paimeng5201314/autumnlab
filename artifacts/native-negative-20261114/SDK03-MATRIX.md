# SDK03 冻结原生组件回归

制作人：派蒙。目标 `T06-20261114-sdk-runtime-03` / `1.0.0-rc.7` / `1.0.0-platform-test.8`。
源 `sha256:b776cc0ec00fca576a075e5d80be09749fdf6b5139d0515c973661a9241fd536`，原生表 SHA256 `da12bf0e85d40ea4221d806b0d0ddcdb6275ae2a6817439e2c3c21ce6bce3aed`。初始 not_run。

最终：root READY 后先完成指定生成暂存树全 SHA 归档，再核全部 1997 delivery 文件、独立编译并执行四项各一次均 passed；全部原组强退休、独立原句柄/Job0/staging 确认，无 harness 救援，06:00:34Z 交回 root 可 GUI。详见 SDK03-RESULTS.md、sdk03-series.json；下文保留预声明约束。

先仅执行 `Prepare-Sdk03Harness.ps1 -TemplateOnly`，生成新 `sdk03-harness` 源码与未验证模板收据，不读取尚在制作的 SDK03 prepared 文件、不编译或启动 owner。模板为已审 SDK02 Program（SHA `d8cf15021817a6e3795fb373af0bf4339ca2b13631d90866d4b9c40ada03efdb`），只换本次身份，源文件如存在必须逐字节一致，禁止覆盖变化。

必须等 root 明确完整 Build/Test 九组/Prepare/SingleFile/源 ZIP 全部完成且无 GUI/native 竞争，才无参数执行该脚本核最终 Build/Prepare/table/prepared 绑定及全部 delivery 长度/SHA，生成独立最终 preparation 收据，保留先前模板收据。之后才能编译/直接启动 plain apphost `AutumnOS.Sdk03.NativeNegative.exe`，需核所有路径祖先无 reparse 和 driver/apphost SHA；DOTNET_ROOT 仅本次 PowerShell 子进程环境定位运行时，禁止 `.tools/dotnet/dotnet.exe driver.dll`。

固定四项各一次、严格串行、首个失败停止保留：normal-close、browser-failure、root-stall、pipe-close。每项先真实 parser gate、800×600 PNG、STA Posted receipt、页面 ACK、同序 fence，再针对新建 owner 原对象注入计划故障。升级句柄经 KernelBase.CompareObjectHandles 核对，不接受任意 PID。不放宽资源额度、deadline、正常 Closed 三项谓词、原句柄/Job0/强退休或 staging 释放要求；harness 救援不能使失败转通过，断管/超时/回收请求本身不能算退出或 Post。

下一项及 GUI 都必须等待前组产品强退休、独立原句柄全部 signaled、Job0 和 staging 删除。正常两个恢复标志均须 false；停滞须实际 I/O deadline、后继 fence 未确认和产品仅原 owned Job 回收。禁止与 GUI、产品构建、全套测试或压力 helper 并行。SDK01/02及旧 qa03 失败材料保留，不覆盖历史成绩。

本次只是新冻结字节的组件级证据，不代替两行应用名称显示、桌面拖放/DPI/窄宽屏、权限/账号真实提交、Win10/11和完整隔离矩阵。客户端行高改动需由 root 用 SDK03 真实 GUI 另验。
