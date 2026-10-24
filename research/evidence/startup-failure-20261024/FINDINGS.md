# 用户反馈的启动故障

制作人：派蒙。2026-10-24。对象：`T06-20261024-codecheck-01`，单文件SHA256 `0d0a85a579834dfcefb680a2a46a905ce4bcb39051e32d1ea646534ab1ecac5f`。

## 2026-10-24 取得截图及实际诊断后的结论

故障已定位到 Windows 代码完整性策略阻止程序启动。用户截图显示 Smart App Control 拦截旧根入口；CodeIntegrity 的3033/3077事件同时记录旧入口和 codecheck-01 内层 `AutumnOS.Client.exe` 被策略 `{0283ac0f-fff1-49ae-ada1-8a933130cad6}` 阻止，时间09:29:53及09:30–09:31。证据 `code-integrity-evidence.json` 保留10条相关记录。

用户主动执行的诊断结果在 `run-12c263a4e4ea4ca5ab671a515bd95301/`：外层PID26252，内层Bootstrap PID26192，退出码31，未生成Client子进程标记。host trace证明内层.NET托管入口确实执行。实际Win32Exception数字没有被旧代码记录，保持未知，不虚构为577或4551。旧错误提示 `.autumnos-update` 报告不存在，是代码中的真实诊断缺陷。

`authenticode-evidence.json` 记录旧外层、新外层、Bootstrap、Client及Updater全部为NotSigned；`signing-availability.json` 记录两个本机My证书存储没有代码签名证书。没有改变安全策略或证书信任。合法解除拦截仍需受信签名身份和实际Windows复验。

源码已补齐按原生错误分类、失败阶段与真实脱敏JSON报告，并修复单文件固定EOF读取不兼容签名证书表的问题。新构建 `T06-20261024-startup-02` 的完整交付和自动检查见本目录 `STARTUP-HANDOFF.md`（完成后生成）；第三方隔离未宣称完成。原失败包与用户数据未修改。

## 取得诊断前的历史调查（不代表当前状态）

当时用户反馈无法正常启动，实际退出码与错误原文尚未取得，根因未知，未标修复。以下保留原调查记录：

本地只读证据：

- `candidate/AutumnOS_Data/System/Product-85cb6f420ce57ddef9fb`已经存在，1687个打包清单文件全部匹配原摘要，另有合法marker和seed。没有修改这些文件。
- CLR目录为空；经只读bundle解析，内层Bootstrap有180个managed程序集、deps和runtimeconfig共182项，没有需要落盘提取的native/other项。因此不能仅凭CLR空推断它未运行。
- 没有业务Logs或`.autumnos-runtime.lock`，说明尚无完成Client早期guard的证据；日志缺失不能确定具体异常。
- 没有匹配AutumnOS的本次Application崩溃事件，不能把“无事件”当成正常退出。
- 静态发现Shell `Main`的早期catch只打印固定stderr并返回22；SingleFile原样传递退出码，没有报告非零退出。固定WindowsAppSDK的self-contained自动初始化还可能先于Main，故需要捕获原始子进程链输出。

诊断入口：工程根 `AutumnOS-启动诊断-20261024.lnk`。由派蒙主动运行；绑定原EXE实际SHA256，只启动这个失败的测试副本。若已有AutumnOS进程，拒绝继续且不结束任何进程。全局环境与安全配置不改，输入没有新增启动参数；只在子进程环境中启用.NET host tracing，并重定向stdout/stderr。输出到本目录全新的 `run-<随机编号>`，不覆盖旧证据。

本轮遵守“实际检测交给派蒙”的要求，代理未启动产品GUI，也未执行这份诊断脚本。脚本Parser检查通过，实际运行仍not_run。日志取得后按实际退出码和错误阶段修复；不修改或重打包一个未经定位的所谓修复版。

参考：[官方.NET host tracing](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-tracing.md)、[官方bundle文件提取判定](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/native/corehost/bundle/file_entry.cpp)。这些实现资料辅助解释证据，不代替本机失败复现。
