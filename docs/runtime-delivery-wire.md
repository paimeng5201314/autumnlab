# 独立宿主投递协议增量 · G2-C

制作人：派蒙。此增量仍在构建验证阶段；不代表 G2 生产宿主或完整隔离已完成，普通第三方门禁保持拒绝。

`RuntimeDeliveryWireProtocol.cs` 实现双向独立序号的二进制帧：24 字节固定头、最多 4096 字节元数据、最多 32768 字节 payload。长度、类型、版本、保留位和连续序号在读取正文之前验证；元数据要求完整实例/文档/账号/权限通道绑定及随机 nonce，拒绝重复或未知字段。仅 Delivery 可带 payload，严格 UTF-8 和 SHA-256 逐帧验证。DeliveryId 是关联号，可以不连续或不按数值发送；不能代替发送序号。任一方向遇到截断、取消或协议错误后不可重用。读写器不拥有底层 stream；线上没有“进程退休”消息。

`RuntimeDeliveryParentTransport.cs` 只供可信原始进程 owner 组合，持有原 evidence port。写完成只归还 driver 的内存使用权。接收端在阻塞读取前后重验同一原始 peer，只有方向、序号、绑定、原待处理 ticket/fence 完全匹配的回执才能交给 coordinator。正确 fence 回执才推进下一 channel。断管、假回执和身份变化使 endpoint Unknown；stream 和进程句柄仍由原始 owner 保留，实际退休不得来自此协议。其 peer 接口尚未接到生产 OS owner，测试使用明确的替身。

`RuntimeNativeDeliveryActor.cs` 将实际 STA 投递和屏障纳入同一有界 FIFO。原生 Post 调用正常返回后才能形成 Posted 回执；原生调用异常保留 Unknown，不能承诺未投递。屏障必须在原 STA 的旧投递之后生效，再生成准确 ACK。拷贝、排队和未返回的 dispatcher callback 均占额度。执行器关闭或 idle 不是进程组退出证明。

payload 默认最多占 16 槽，唯一 fence 另有 1 个控制槽；消息额度满时仍能按同序排入撤权屏障，不用丢弃旧投递或重发屏障来腾位置。原生文档 target 在原 owner 保存对象后显式安装导航、内容加载及 source 失效事件；检查 owner 准入前后与实际 Post 前均复核原线程、HWND 和 native source。相同 URL 的新导航也会永久使此 target 失效。该适配器的真实 COM/文档重入验收仍为 not_run。

宿主 pin 释放故障保留为永久失败，后续空 Dispose 不能变成成功退休。创建意图与 Close 原子排序；已由 owner 领取的创建调用仍需观察其真实返回，期间不能按“尚无进程”释放资源。Job 审计固定使用 manifest 已 pin 的真实 Host/Runtime 目录，不使用另一 Session 子路径猜测。目录 pin 不阻止额外文件写入；真实受控 staging 写入边界尚未组合，也不得因此启用角色。

`src/AutumnOS.NativeHost.Windows/` 已加入解决方案，独立 WinExe 只链接纯投递代码，不引用 Identity/Storage 服务程序集。严格启动清单固定实际 image、WebView 引擎和完整包文件集合；文件 pin 与原句柄观测不等于已执行原生启动。`NativeWebViewDeliveryTarget` 提供原 Control 的 STA dispatch 和真实 `CoreWebView2.PostWebMessageAsJson` 调用，仍依赖原生文档 owner 的实际准入检查。私有 station、AppContainer browser、受认证 IPC、parser gate、内部画面和输入尚未完整组合，因此角色明确 unsupported，在创建 LSA/USER/profile/browser 前停止。无参数入口直接退出零，不能被记为浏览器运行通过。

接下来的生产组合必须把原始 OS owner 接到 peer 接口、将远端 reserve/冻结响应接入 Session 最终租约、完成私有浏览器角色及内部呈现/输入，然后在目标 Windows 10/11 普通桌面完成真实矩阵。当前组件测试使用合成 stream、dispatcher 和 lifetime；Windows Release 编译不能替代这些验收。所有实际结果只引用对应新 build ID 和源字节摘要。
