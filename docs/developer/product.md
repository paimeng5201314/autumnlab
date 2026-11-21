> 开发者工具包的 kit.json 记录源快照、产品与 SDK 版本。

# 产品范围与版本

适用 AutumnOS 和 SDK v1.0.1，应用消息协议 1。制作人：派蒙。Lab Chronicles AutumnOS 是Windows原生桌面及内部应用宿主；用户入口是 `AutumnOS.exe`，业务进程为同根 `AutumnOS.Client.exe`，安全更新使用独立 `AutumnOS.Updater.exe`。完整目录是运行单位，单拷EXE不构成交付。

应用有独立的清单版本与存档格式。随附官方应用和 SDK 使用宿主的产品版本；第三方应用由作者自行编号。应用消息协议描述宿主与应用之间的通信格式。

Windows Server 2022（x64，内部版本 20348）使用“桌面体验”安装选项。用标准用户登录图形桌面，将单文件版 `AutumnOS.exe` 放进该账户可写的目录后直接打开。自带元素配对与商店或本地安装的应用走不同的运行路径。后者启动时会校验随包提供的固定版浏览器、独立进程和应用实例的隔离状态。系统兼容性不改变应用的授权或存档归属。

用户在同一目录保留 `AutumnOS_Data`。安装资源、账号凭据、桌面设置、应用私有数据和存档有不同归属；卸载应用默认保留数据，退出账号不删除存档。只读安装位置要明确报错，不建议日常管理员运行，不静默搬迁或合并数据。

当前提供hello-app、identity-app、save-game、desktop-extension四个内部模板，以及复用独立服务端/客户端的backend-identity入口。完整网络隔离尚未证明，不应借模板成功开放任意社区应用。PM/SQ是仓库自标分类，不是安全认证或额外权限。实际兼容性以目标构建报告为准；Windows10、干净机器、触屏/多屏/DPI和真实身份收尾不得从当前Windows11测试推导为通过。

用户通过 AutumnOS.exe 启动程序。签名更新和恢复流程见[主程序发布](host-publishing.md)，网络能力和故障排查见[网络](network.md)及[排错](troubleshooting.md)。
