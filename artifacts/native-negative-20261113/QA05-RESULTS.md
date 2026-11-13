# QA05 冻结原生组件回归结果

制作人：派蒙。实际环境Windows Server20348、普通Medium用户；不是Win10/11验收。产品`T06-20261113-release-v1-qa05`、1.0.0-rc.4；source `sha256:2ef7fc047da53d825194e7453a2bd3fa2291d5b6e42dff9687e01f4e4cddd300`，native table SHA256 `6980bed97c2b84a40beac0b9bf9a61394e6b41e874fa5fe02872105823ca6f49`。

成功Build/Test/Prepare之后新生成和编译qa05 driver，零警告零错误，四个引用DLL与最终prepared目录逐个相符。最终1987项目录字节按delivery-hashes再次核对。root明确确认单EXE打包也已结束后才启动owner，期间无UI、构建或全量测试并行。没有复制或重跑CPU helper、没有重复normal或改产品源码。

按预先声明顺序各一次，只有前项退出、强退休及清理完成后才运行下一项：

| Case | 整个case秒数 | 结果 |
| --- | --- | --- |
| normal-close | 81.22 | Closed=true，两个恢复标志false。 |
| browser-failure | 38.36 | 仅本次原boundBrowser同内核对象故障；NATIVE_ORIGINAL_BROWSER_EXITED，两个恢复false。 |
| root-stall | 64.92 | 仅本次原restrictedRoot同内核对象暂停（含STA）；真实I/O期限触发、排队fence未确认；仅产品原Job请求恢复。 |
| pipe-close | 38.96 | 本次原pipe关闭后原组自然退休；没有Closed冒充，两个恢复false。 |

秒数包含stage拷贝、启动、原生正控、动作和退休，不是单独Close RPC延迟。各项都先完成真实parser gate、完整PNG解码、STA Posted receipt、页面ACK和同序fence正控。各项最终均确认原station进程/线程退出、原Job0、全部已观察句柄信号、上下文消失、产品ResourcesRetired；独立复制的原root/browser句柄和原Job Active0/PID空再次核验，stage也已删除。harness救援全部false，原station强制恢复全部false；回收请求本身没有当成退出证明。

最后核对NativeHost/CPU helper残留均0，已通知root可以开始真实UI。机器可读逐项路径与结果为qa05-series.json，driver源码/编译身份为qa05-harness/preparation.json与compile-only.json，预检为qa05-preflight.json。

qa03正常关闭失败仍保留且未解释；本次通过不覆盖历史失败，也不证明更新迁移、Bootstrap错误展示或新UI功能已验证。本组件矩阵不观察Shell权限/账号最终提交或运行/维护租约，不覆盖真实Post内部死锁、renderer-only故障、真实文件/网络/剪贴板/账号A/B、硬件/IME、Win10/11或干净系统。完整隔离仍未完成。
