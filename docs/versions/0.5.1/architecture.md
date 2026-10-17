# 架构与信任边界

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。系统分为可信宿主内部C#服务、受限应用SDK、独立服务端HTTP三层。文档中的 `RuntimeSession`、`AccountDataStore`、`ApplicationInstallService` 等内部类型不导出为任意应用调用权限。

稳定入口协调同一用户业务实例和维护期间的启动请求。WinUI宿主拥有桌面、系统应用、权限弹窗和WebView。每个内部应用由安装来源、appId、实例、当前账号和sessionEpoch绑定；应用发出的消息仅提供protocolVersion/requestId/method/params，不能自行选择账号、磁盘根、其他实例或执行命令。网关在处理开始和最终UI投递时再次检查租约、权限与代次，防止异步结果迟到泄露。

请求路径是应用JS→WebView消息→RuntimeSession→权限/存储/桌面服务→最终响应门禁→原实例。事件路径也经过当前账号、权限修订和实例检查；关闭/切换账号撤销旧订阅，不能仅相信业务任务完成时的一次判断。数据写入、安装提交、身份关键变更和更新维护共享协调；游戏Starting、Foreground、Background、Suspended、Closing均阻止更新提交。

主程序更新先独立验证签名和payload，安全退出后由更新器重新验证交接与根目录，持久事务替换受管程序文件。新版核心初始化健康确认成功才提交；失败恢复旧版及兼容数据，并隔离坏版本。文件哈希用于字节完整性，发布签名用于来源，Windows Authenticode另属一层。

独立后端只接受自己登记的资源JWT，不信任SDK返回的昵称或appScopedUserId。浏览器账号会话、宿主身份会话和开发者资源客户端不是同一凭据容器。参见[身份权限](identity-permissions.md)、[服务端](server.md)、[数据](data.md)。威胁边界仍包括未完整证明的WebView网络通道，见[网络](network.md)。
