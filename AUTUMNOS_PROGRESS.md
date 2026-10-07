# AutumnOS 公开交付进度摘要

制作人：派蒙。此文件是公开仓库的脱敏交付摘要；完整本机历史、失败记录和测试数据保留在原工作区，没有删除或改成通过。

## 2026-10-07 · 新版 EXE 的 GitHub Actions 构建

用户要求生成新版 EXE，并选择 GitHub Actions 作为 Windows 构建环境。新增 Windows 工作流和 `Build-SingleFile.ps1`，默认内部版本 `0.5.1-local.5`、显示版本 `meta0.0.2-20261007`；通过独立构建分支触发，完整执行非交互测试和单文件打包，将 EXE 与摘要/证据保存为 Actions 产物。原生交互烟测在 CI 明确为 `not_run_ci_no_interactive_desktop`，没有发布公共 Release 或配置生产签名。

本地 Linux 实测原始 Windows 锁文件恢复通过、9 个依赖项目可生成 win-x64 输出；完整 Shell 在微软 XamlCompiler.exe 处报 `Exec format error`，托管任务方式同样不可用。没有以不完整入口冒充新版 EXE；完整生成结果以 Windows Actions 的实际运行与产物记录为准。构建与下载方法见 [新版 EXE 构建](docs/新版EXE构建.md)。

## 2026-10-07 · 问题报告源码修正（未发布）

按本次用户提供的《06-问题与改进报告.md》实施 ISSUE-01/02/04/05/06 修正与 GAP-07 的 StructuredLog 优化。已补写前完整读回预算、独立偏好目录及保留旧字节的迁移、严格探测历史解析、日志轮转和失败状态、桌面说明及 17 篇版本文档。修改、剩余 GAP 和执行范围见 [问题报告后续验证](docs/report-follow-up.md)。没有重新构建或替换历史交付 EXE，本节结果仅关联当前源码。

当前审阅源快照：`sha256:3eb4cd32109eaa522b4ea278d36c3150aee3a64642caa14c006fe4049ddb6077`，清单 `artifacts/onboarding/report-fixes-source/source-snapshot.json`。环境为 Linux x64、.NET SDK 10.0.401、运行时 10.0.12、PowerShell 7.6.6、Node 24.19.0、npm 11.6.1、TypeScript 5.9.3。Linux 库/测试依赖图编译 0 警告、0 错误；专项 C# 71/71、JavaScript 28/28、源码机器合同 352/352、文档/XAML 静态检查 144/144 通过。

完整 C# 套件实际 487/495，8 项文件忙/只读替换断言在 Linux 失败；已用独立文件操作确认与 Windows 文件语义差异有关，原断言和失败结果均保留。报告分别为 `artifacts/onboarding/report-fixes-focused.json`、`report-fixes-csharp.json`、`report-fixes-node-tests.log`、`report-fixes-contracts-final.json`、`docs-ui-checks.json`。复制的 Developer 布局整体检查还存在 Linux 大小写导入差异，不能据静态链接检查宣称套件实际运行通过。

后续仍需 Windows 原生编译/UI/存档与偏好升级验证、新单文件 A/B 更新回退、社区网络通道矩阵、真实身份往返、设备输入矩阵，以及授权签名与许可收尾。文件夹/多桌面等完整桌面功能、资源回收工作流和 Launcher/Runtime 两类日志轮转尚未完成；对应安全门禁保留。未执行提交、推送或发布。

## 以下为 2026-10-03 的历史交付记录

2026-10-03，制作人明确授权将本地源码和文档上传到 `paimeng5201314/autumnlab`，并在 Release 交付单文件 EXE。此授权仅覆盖本次上传，不代表所有原验收、制作人审阅或正式发布门禁通过。用户明确暂不制作安装包。

最新本地实际产物：`meta0.0.1-20261001` / `T06-20261003-single001-03`。其 354 项源码快照保持实际构建字节；完整清单、EXE 哈希及真实验证分类见 `release/meta0.0.1-20261001/`。历史 README 保留在 `docs/history/`，原需求/验收/门禁和暂缓项仍在 `autumnos-spec/`。

T06 in_progress，T03 原收尾仍暂缓。新单文件布局完整 A/B、真实 Logto 收尾、完整社区网络隔离、全部原功能/数据验收、Win10/干净环境/离线首启/DPI/输入法等矩阵、第三方许可和制作人候选审阅不得写成通过。仓库上传和 Release 操作状态以实际 GitHub 结果为准，不把本摘要本身当作上传或发布成功证据。

## 本次远端结果

源码、规范及文档已实际上传 main，提交 2fc09076651d2366ea8ecc0d8b7ae4821cfea824。395 个上传文件全部 Git blob 哈希核对，保留原许可证。现有 Release 草稿附件 AutumnOS.exe 的 488,573,230 字节及 SHA-256 与最新本地产物一致，因此无需重传。

Release 仍为 draft，未公开发布：当前 GitHub 插件没有 Release 写入/发布接口；本机没有可用的 GitHub 登录凭据。制作人可打开现有草稿，将标题和新标签设为 meta0.0.1-20261001，勾选预发布，填入 release/meta0.0.1-20261001/README.md 的说明，点击 Publish release。不要把附件摘要当作更新发布签名。
