# AutumnOS 0.5.1 开发者文档

制作人：派蒙。本文档对应内部兼容版本 0.5.1、SDK 0.3.0、消息协议 1；显示版本可能为 `meta0.0.1-20261001`。目录编号不代表生产发行状态，也不表示文档中的验收步骤已经执行。当前包含原生 Windows 宿主、受控内部应用、开发工具和独立身份示例；普通社区应用的全面网络隔离、生产更新信任根及多项真实环境验收仍未完成。

先按 [产品与交付形态](product.md) 找到当前运行目录，再按 [创建第一个应用](first-app.md) 创建、打包并确认预览。单文件交付的 `Developer` 位于展开后的 `AutumnOS_Data/System/Product-<摘要>/Developer`，普通目录版位于程序目录的 `Developer`。`--host` 始终指向实际 `AutumnOS.Client.exe` 所在目录；不能在单文件版中直接使用外层 EXE 所在目录。示例会检查当前进程，避免误选旧资源。

| 目标 | 阅读入口 |
|---|---|
| 了解运行边界和模块 | [产品](product.md)、[架构](architecture.md)、[界面与交互](design.md) |
| 修改宿主、复现构建 | [核心开发](core-development.md)、[协作与证据](collaboration.md) |
| 创建与校验应用 | [第一个应用](first-app.md)、[包格式](packages.md)、[应用发布](app-publishing.md) |
| 调用内部服务 | [SDK 参考](sdk-reference.md)、[身份与权限](identity-permissions.md)、[数据](data.md) |
| 接入桌面或独立 API | [桌面扩展](desktop-extension.md)、[服务器身份](server.md)、[网络边界](network.md) |
| 分发宿主与定位失败 | [宿主发布](host-publishing.md)、[排错](troubleshooting.md) |

这些页面随开发者套件整体复制，因此页间链接只引用本目录。源码位置与套件资源路径使用文字分别标明；源码工作区脚本不能直接在交付套件中运行。机器合同检查可验证 TypeScript、Schema、OpenAPI 及本组文档链接，不能代替 Windows 原生交互、真实登录、更新回退或外部新人验收。
