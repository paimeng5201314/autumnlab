# 启动故障修复交接

制作人：派蒙。2026-10-24。状态：代码修复与自动核验完成，Windows SAC 拦截尚未解除。派蒙已明确没有现成签名证书。不得把新包描述为可在本机正常启动的正式版本。

## 原因与实际修复

两张截图与系统记录吻合：旧根入口、代码核验包内部 `AutumnOS.Client.exe` 都被 Windows 代码完整性策略阻止。保留的10条3033/3077事件见 `code-integrity-evidence.json`。派蒙主动执行的 `run-12c263a4e4ea4ca5ab671a515bd95301/report.json` 记录退出码31，host trace确认内层Bootstrap已执行。旧代码未记录实际Win32Exception数字，不能从退出码31反推成577或4551。

已修改：

- `src/AutumnOS.Bootstrap/Program.cs`：记录校验、维护、恢复、启动Client等阶段；按真实原生错误分类，不再把Windows阻止启动统称为更新失败。
- `src/AutumnOS.SingleFile/StartupFailure.cs`：内外启动器共用的脱敏JSON报告。只追加到当前数据根 `Logs/Startup/`，写成后才显示路径；写入失败明确告知。保留数值错误、构建和源快照，不写任意异常正文、环境或登录资料。
- `src/AutumnOS.SingleFile/BundleFooter.cs`：支持附加PE证书表后的资源边界，严格拒绝损坏目录、越界、非零填充及哈希不符。原固定EOF读取方式在正式签名后会失效；本修复不验证或授予证书信任。
- 保留维护/更新恢复、原有包哈希、数据目录与第三方运行门禁。打包脚本纳入共享代码、未签名状态与签名说明。

Windows在最外层程序执行前拦截时，程序无法生成自身诊断文件。源码修复并不能自行创建受信发布者身份。本机两个My证书存储未发现代码签名证书，五个实查入口/组件均为NotSigned，见 `signing-availability.json` 与 `authenticode-evidence.json`。用户另行确认没有现成证书。没有购买/申请证书、调用云端签名或更改系统策略。

## 构建与检查

构建 `T06-20261024-startup-02`，版本 `0.5.2-local.8`，显示号 `0.0.5-20261024-startup`。

源快照：`sha256:b130e9646e87b9c043d76b3a970e48bef31135721c845558044ee9cbba76aade`。

Windows 11 / 10.0.26200；项目SDK 10.0.401；固定WebView2 154.0.4258.53；WebView2 SDK 1.0.4258.31；隔离策略 autumn-local-web-v2（本轮未变更策略）。报告根为 `<WORKSPACE>\artifacts\builds\T06-20261024-startup-02\`。

| 检查 | 结果及范围 |
|---|---|
| Windows Release | passed，0警告0错误 |
| C#自动回归 | passed，869/869；新增启动诊断15、合成PE布局16 |
| SDK / TypeScript及Schema合同 / 样例 / ChemLeap模型 / 身份客户端夹具 / 自有HTTP样例 / CLI | 全部passed，保留各自独立报告；不是GUI或真实账号 |
| 静态检查 | passed，83项 |
| .NET Framework单文件编译 | passed，包含两个共享源文件，warnaserror |
| ZIP回读与解压 | passed，16个文件摘要匹配 |
| 最终EXE与解压副本资源区 | passed，只读解析实际文件及SHA256，见 `bundle-readback-af760f9b0cd447bc8cb85448df21ef8c/report.json` |
| 源码导出 | passed，431文件ZIP回读，未含真实数据/凭据 |
| 原codecheck-01启动 | failed，用户实际运行，Windows策略拦截；证据保留 |
| 新EXE及解压副本GUI | not_run，按用户要求留给派蒙实际检测 |
| 受信签名、签名后解包与SAC接受 | not_run，缺少受信签名身份 |
| ChemLeap普通模式 | 仍在WebView创建前拒绝；既有隔离缺口未完成 |

所有新增错误测试使用合成异常及自有临时文件；证书表测试使用合成PE结构。未通过“没有弹错”或超时推定系统放行。真实源代码回归结果见 `tests.json` 的results；总体索引 `startup-02-verification.json`。

## 实际产物

- EXE：`<WORKSPACE>\artifacts\code-handoff\T06-20261024-startup-02\candidate\AutumnOS.exe`
- ZIP：`<WORKSPACE>\artifacts\code-handoff\T06-20261024-startup-02\AutumnOS-0.5.2-local.8-code-check.zip`
- 解压副本：`<WORKSPACE>\artifacts\code-handoff\T06-20261024-startup-02\extracted\AutumnOS.exe`
- 新快捷方式：`<WORKSPACE>\AutumnOS-启动诊断修复-20261024-未签名.lnk`（未启动，不覆盖旧入口）。
- 源码ZIP：`<WORKSPACE>\artifacts\startup-failure-20261024\AutumnOS-source-T06-20261024-startup-02.zip`。

EXE SHA256：`43140d2c5fb4dadacb18432c61f23540ac99b5671f0cae41a8749eb3c2e7af6b`，488766183字节。

ZIP SHA256：`e965b4b2dd293f8868c585e25b86f809c1ba8fd004cb95a17276e3444962d768`。

源码ZIP SHA256：`561dc4ae3e8bac186fb8b3e99d888be9768ae23976476ca42d0b65cc8120b83f`。

旧根 `AutumnOS.exe` 摘要仍为 `71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1`，未替换。原测试失败目录、存档与所有既有产物保留；未执行Git、上传、发布、全局策略改动、UAC或强杀用户进程。

## 后续前提

本次系统启动问题还需要受信发布者签名身份。拿到合法签名能力后必须先签内层自有EXE/DLL、生成最终清单与payload、再签外层并重新生成交付哈希；完成签名验证和真实SAC/解压副本运行后才能写“可以启动”。目前没有执行该签名流程，不能只签旧外层或安装自签测试根来声称达标。完整说明在工程 `docs/windows-startup-signing.md` 及随包 `WINDOWS-STARTUP.md`。

派蒙以后检测新包前，请正常退出旧启动器，避免单实例唤回旧版本。本次没有要求你立即重复测试已知未签名问题。

原隔离任务的剪贴板边界、两文档RTC、完整网络验收和ChemLeap普通模式仍未完成；没有ChemLeap特判、开发者放行开关或来源认可名单。完整 `AutumnOS_Isolation_Implementation_Codex_Prompt.md` 仍缺失，本轮按已有用户要求工作，未假装读取。
