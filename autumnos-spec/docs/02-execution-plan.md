# 02 · 开发顺序与多人协作

制作人：派蒙。以下负责人均为角色分工，尚未分配真实人员或自动代理。

## 分工

| 工作组 | 主责 | 边界 |
|---|---|---|
| 集成与协议负责人 | 解决方案、依赖、公共协议、架构决策、合并 | 共享协议和发布版本由其统一协调；不包办所有 UI |
| 桌面与设计组 | 设计系统、首启、桌面、系统应用 UI、个性化 | 调用服务，不自行解压、刷新令牌或修改他组数据库 |
| 运行环境与 SDK 组 | WebView2、实例生命周期、隔离、网关、JS/TS SDK | 不让应用直接进入内部管理服务 |
| 账号与数据组 | Logto、权限、会话、存档、文件、设置 | 提供真实身份和数据接口；不向应用泄露令牌 |
| 商店与分发组 | GitHub、版本、代理、下载、安装事务 | 不把来源标签当作安全认证；不覆盖运行中的程序 |
| 构建与质量组 | 本机 Windows 自动化、测试、打包、独立更新器、恢复演练 | 不自行缩小范围或批准失败的质量门禁 |

小团队可由一人承担多个角色。并行不等于同一时间让多个人改同一个共享文件。

## 依赖顺序

T00 基线和可构建工程 → T01 代表性内部应用与协议验证。

T01 通过后：T02 桌面与系统体验、T03 账号/权限/数据、T04 商店/下载/安装可按已冻结协议并行。

T05 更新与发行：工程脚本可从 T00 起准备；真实安全更新必须等待 T01 的运行状态以及 T03/T04 的关键操作锁。

T06 全量联调、文档验收、双发行包和发布证据：依赖 T02/T03/T04/T05。

这是一套依赖关系，不是承诺某个时间点自动完成。每阶段都需要可检查的产物。

## 第一条完整路径

真实 .autumn 包 → 安装检查 → 安装事务提交 → 注册表记录 → 桌面图标 → 内部运行 → 权限请求 → 保存 → 回到桌面仍保持游戏状态 → 真正退出 → 安全更新。

第一条路径打通时不代表全部 UI 和功能已经完成；它用于尽早暴露接口错误，不替代全量验收。

## 本地项目结构建议

```text
src/
  AutumnOS.App/                 WinUI 外壳和组合根
  AutumnOS.Contracts/           主程序内部接口与模型
  AutumnOS.Domain/              与 UI 无关的领域规则
  AutumnOS.Infrastructure/      GitHub、Logto、文件等适配
  AutumnOS.Runtime/             第三方应用实例与网关
  AutumnOS.SystemApps/          设置、商店、下载、账号等
  AutumnOS.Updater/             独立更新进程
sdk/
  autumn-sdk/                  公开 JS/TS SDK
  schemas/                     应用包、发布、消息 schema
samples/
  hello-app/
  identity-app/
  save-game/
  desktop-extension/
  backend-identity/
tools/
  AutumnOS.DevCli/
tests/
  Unit/
  Contract/
  Integration/
  Security/
  WindowsSmoke/
docs/
  product/ architecture/ sdk/ publishing/ operations/
```

此树为待建设目标，不能把目录的存在当成对应功能完成。

## 协议优先

各组共享 AppIdentity、AppInstanceId、PermissionGrant、SessionSnapshot、InstallRecord、TaskProgress、UpdateCandidate 和 ErrorEnvelope。共享模型先进入 Contracts/schema，由真实测试锁定；工作组只能在模块边界内自由实现。

不把每个服务拆成远程微服务。桌面、商店和下载多数为客户端内的模块；确实需要远程信任的身份签发或云数据另设服务端边界。

## 合并规则

按本地工作目录隔离；用户允许使用本地Git时可选模块分支。公共 schema、依赖锁、解决方案、主窗口组合根、数据库迁移和发布工作流由集成负责人串行审核。本地交接说明应包含需求 ID、测试 ID、接口变更、数据兼容影响、截图或运行证据。

破坏兼容的协议变更需升级协议版本并更新迁移文档。不要在各组私有分支里各自发明另一套用户 ID、应用 ID 或版本格式。

每次集成后执行合同测试和最小真实路径。使用模拟服务并行开发是允许的，但发布配置不能启用模拟服务，最后必须跑真实集成。

## 阶段报告模板

```text
阶段与任务 ID：
源文件快照ID/内容哈希（commit可选）：
实际执行环境与工具版本：
本次实现：
涉及需求 ID：
涉及公开接口：
实际执行命令：
测试结果与证据路径：
Windows 运行证据：
未通过/未执行项：
外部配置阻塞：
下一阶段交接：
```

## 本地构建优先

开发根是用户当前本地文件夹。所有检查可由本地scripts/入口执行，构建与候选放在artifacts/；不要求远端仓库或GitHub Actions。只有用户明确授权后才额外接入远程协作或发布。
