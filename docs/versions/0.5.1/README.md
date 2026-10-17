# AutumnOS 0.5.1 开发者交付

制作人：派蒙。适用客户端 **0.5.1 系列本地联调版**、SDK **0.3.0**、应用消息协议 **1**；这三个版本相互独立。精确产品版本、BuildId、源快照和SDK版本读取开发者目录 `kit.json`，不把文档目录名当成某次构建已经测试的证明。当前停止位置是本地审阅，未授权公开发布；生产更新公钥、正式签名、独立后端登记、干净Windows10及完整隔离/输入矩阵按发布就绪报告保留阻塞。安装器及T03收尾仍按制作人决定暂缓。

从完整便携目录打开 `AutumnOS.exe`。开发工具位于同级 `Developer/`；不要单独拷贝EXE。先阅读[第一个应用](first-app.md)，使用真实CLI创建模板、校验、打包，再在开启开发者模式的宿主内预览。前四个模板可以在游客会话验证非身份能力；真实身份成功分支必须使用真实登录。独立开发预览提供显式测试账号A/B、权限拒绝和离线状态模拟，界面及CLI标记测试范围，不改变普通账号或正式数据。第五类[独立后端样例](server.md)需要另外登记资源身份，缺配置时明确退出。

| 专题 | 内容 |
|---|---|
| [产品](product.md) | 范围、术语、兼容与未完成项 |
| [架构](architecture.md) | 三层边界、进程和数据流 |
| [核心开发](core-development.md) | 本地SDK、锁依赖、构建与快照 |
| [设计](design.md) | 生命周期、输入、主题与原生覆盖层 |
| [应用入门](first-app.md) | 空目录到真实内部预览与打包 |
| [应用包](packages.md) | 清单、归档、Schema、来源与兼容 |
| [身份与权限](identity-permissions.md) | 资料、拒绝、撤销、账号代次 |
| [SDK参考](sdk-reference.md) | 全部26方法、6事件、错误与取消 |
| [数据](data.md) | 私有数据、存档、迁移、文件能力与删除 |
| [服务端](server.md) | 独立资源JWT、OpenAPI和本地配置 |
| [桌面扩展](desktop-extension.md) | 通知、角标、长按动作、小组件和链接 |
| [网络](network.md) | 当前支持边界与完整沙箱阻塞 |
| [应用发布](app-publishing.md) | 只读商店合同及本地发布材料 |
| [主程序发布](host-publishing.md) | plus/meta、签名、维护与恢复 |
| [排错](troubleshooting.md) | 用户操作、数据保护、脱敏与证据 |
| [协作](collaboration.md) | 所有权、合同校验、兼容和交接 |

包内 `SDK/` 提供运行JS、TypeScript声明、应用Schema及独立开发管道developer.schema.json，`Schemas/` 是主程序更新Schema，`Templates/` 是四个原生宿主内部应用模板，`BackendIdentity/` 提供真实本机服务和独立客户端。开发管道不是应用SDK能力；应用不可发送模拟命令。`Tools/contract-checks/` 固定工具锁，`Tests/contracts/` 是正反例与公开接口基线。执行合同检查需显式可用的Node.js；它不会自动安装系统组件。

自测和外部新人验收必须分别记录。干净目录复现报告会说明工具、命令、包哈希、真实运行范围和未执行项；本页不是验收成绩单。所有例子均是本地操作，不创建仓库、Release、Tag或公共服务。
