# 产品范围与版本

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。Lab Chronicles AutumnOS 是Windows原生桌面及内部应用宿主；用户入口是 `AutumnOS.exe`，业务进程为同根 `AutumnOS.Client.exe`，安全更新使用独立 `AutumnOS.Updater.exe`。完整目录是运行单位，单拷EXE不构成交付。

“应用”是经验证的 `.autumn` Web/WASM包；“应用实例”从Starting到真正释放资源才结束。“后台”仍有活实例并阻止主程序替换。“应用版本”决定包资源，“存档格式”决定内容可读性，“SDK版本”是JS/类型发行，“协议版本”是消息封装。四者不可互换。0.5.1本地产品不意味着SDK自动变成0.5.1，也不意味着所有需求通过。

用户在同一目录保留 `AutumnOS_Data`。安装资源、账号凭据、桌面设置、应用私有数据和存档有不同归属；卸载应用默认保留数据，退出账号不删除存档。只读安装位置要明确报错，不建议日常管理员运行，不静默搬迁或合并数据。

当前提供hello-app、identity-app、save-game、desktop-extension四个内部模板，以及复用独立服务端/客户端的backend-identity入口。完整网络隔离尚未证明，不应借模板成功开放任意社区应用。PM/SQ是仓库自标分类，不是安全认证或额外权限。实际兼容性以目标构建报告为准；Windows10、干净机器、触屏/多屏/DPI和真实身份收尾不得从当前Windows11测试推导为通过。

本轮只交本地便携程序与开发资料，安装器按用户决定暂缓；不执行任何远端发布。生产自动安装缺信任根时保持关闭。参见[主程序发布](host-publishing.md)、[网络](network.md)和[排错](troubleshooting.md)。
