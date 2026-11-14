# Lab Chronicles AutumnOS

**制作人：派蒙。用户入口：AutumnOS.exe。**

AutumnOS 是 C# / .NET 10 / WinUI 3 原生 Windows 工程，以内部 Web/WASM `.autumn` 应用、iPad 式桌面、账号权限与数据、安全更新和 GitHub 商店为产品范围，不提供任意 EXE 应用导入。

2026-11-14 当前产品源码为 `sha256:b776cc0ec00fca576a075e5d80be09749fdf6b5139d0515c973661a9241fd536`。SDK 0.4 / 协议 1 已补齐 `network.getStatus/request` 及域名授权、有界网络与预览策略校验；原生关闭清理、资源/Job 与投递证据、桌面窄屏和两行名称也已修复。完整范围与证据见 [发布状态](PUBLICATION_STATUS.md)。

SDK03 `1.0.0-rc.7 / 1.0.0-platform-test.8` 已通过九组自动检查、Server 20348 普通 Medium 合成游客上的四项原生组件和限定 GUI：两行名称/滚动可达、拖放保存与重启保持、游戏旧存档读写及新实例读回、网络预览授权/真实 HTTP200/拒绝与离线切换。它使用临时平台验收配置，证据不转记给另一二进制。

同一源码的正式配置 `T06-20261114-sdk-prod-01`，版本 `1.0.0`、标签 `plus-v1.0.0`，未启用临时平台开关，Release 构建及九组与源码绑定的自动报告已全部通过，含 1295 项 C#。构建为 0 错误、29 条既有 CA1416 测试平台标注警告。完整单文件已打包，504,264,933 字节，SHA256 `5061691ba87c157c6e6bde1fce3eb398266816548d6f325d3fbccc3f7294bbed`；正式配置原生/GUI为 `not_run`，Release 附件由后续实际上传结果说明。

完整隔离未完成；Win10/11 验证由用户暂停，真实 Logto、物理输入/IME、完整 DPI/一二列/宽屏、普通已安装网络应用的持久许可 GUI、外部开发者与完整安全更新/失败回退验收也未宣称通过。普通第三方应用继续受权限及 required_boundaries 门禁约束。旧 qa03 关闭失败根因仍未证明。公开摘要：[正式构建/自动检查](artifacts/publication-sdk03-20261114/PUBLIC-PROD01-BUILD-AND-TESTS.json)、[SDK03限定结果](artifacts/publication-sdk03-20261114/PUBLIC-SDK03-RESULTS.json)。

## 本地构建与运行

在工程根使用 PowerShell 7。工具链为项目内 `.tools/dotnet`，不修改系统 PATH；依赖固定版本及锁文件，从微软/NuGet 官方源恢复。首次缺少工具时按 [本地工作方式](LOCAL_WORKFLOW.md) 检查环境并使用现有安装脚本。

```powershell
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Test-Source.ps1
$buildId = 'T06-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
./scripts/Build.ps1 -BuildId $buildId -BuildVersion '1.0.0' -ReleaseLabel 'plus-v1.0.0' -ProductionUpdateTrustFile (Resolve-Path './build/update-trust.production.json').Path
./scripts/Test.ps1
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Package-SingleFile.ps1 -BuildId $buildId
```

每次构建使用全新 ID，旧证据不覆盖；Test 和 Package 读取当前 `artifacts/latest-build.txt`。实际原生回归另按门禁在合适 Windows 环境执行，构建与自动检查不代替 GUI/隔离验收。上例使用正式配置及现有验证公钥，不需要签名私钥进入工程。

完整单文件入口在 `artifacts/single-file/<build-id>/AutumnOS.exe`；首次运行在入口旁展开 `AutumnOS_Data`。搬迁存档时同时保留该数据目录。`artifacts/packages/` 目录版必须连同 DLL/资源保留，不能仅复制其小入口。安装包、便携 ZIP、SDK 独立包和具体 Release 资产以实际制作记录为准。

## SDK 与应用

SDK JS/类型/Schema 位于 [sdk](sdk)，开发 CLI 源码位于 [tools](tools)，示例位于 [samples](samples)。从 [SDK网络接口](docs/sdk-network.md)、[接口合同](autumnos-spec/docs/03-interfaces.md) 和 [开发文档](autumnos-spec/docs/06-developer-docs.md) 开始。应用网络请求按完整声明域集合授权，HTTPS 和二进制/头/超时有界；宿主凭据、Cookie 与代理认证不传给应用。

应用打开、返回桌面、后台继续和结束保留生命周期及保存语义；空白桌面双击或 F6 打开多任务，图标可拖动排序，600ms 长按/右键/Shift+F10 菜单与键盘等价入口保留。桌面按实际视口布局，持续保留 Dock、主题、壁纸、数据及权限要求。

公开 Logto 配置来自 `autumnos-spec/config/logto.public.json`；不需要 Client Secret。类型/回调登记和真实认证必须核对实际报告，配置发现不等于登录成功。源码及源归档不包含用户 Data、令牌、私钥或原始个人日志。

## 更新、许可与范围

商店只读发现 GitHub `topic:autumnos-app` 及 Releases；仓库自标分类不授予安全信任，不建设认可名单。主程序更新来源为 `paimeng5201314/autumnlab`；公开 Release 附件和可信自动更新是分别核验的交付步骤。旧客户端信任根、数据/安全账本、签名和失败回退必须保留；不启用未签名无人值守更新，未声明 Authenticode 已完成。

完整需求与任务状态在 [autumnos-spec](autumnos-spec)，实现入口见 [架构](docs/architecture.md)、[数据基础](docs/data-foundation.md)、[依赖与许可](docs/dependencies.md)、[工程记录](AUTUMNOS_PROGRESS.md)。[迁移说明](docs/release-v1-migration.md) 等旧冻结文档中的“网络未实现”属于历史构件状态，当前状态以本 README、PUBLICATION_STATUS 和 SDK0.4 合同为准；原历史失败与未执行项保留。

本地构建不要求 Git；源码提交和公开发布按项目所有者明确授权执行。源码许可见 [LICENSE](LICENSE)，保留已有第三方版权和许可。完整 T01–T06、目标系统及完整隔离验收不因版本号自动完成。