# Lab Chronicles AutumnOS

**制作人：派蒙。用户入口：AutumnOS.exe。**

AutumnOS 是 C# / .NET10 / WinUI3 原生 Windows 工程，包含内部 Web/WASM `.autumn` 应用、iPad式桌面、账号权限与数据、GitHub 商店和安全更新。它不支持任意 Windows EXE 导入。

当前生产构建 `T06-20261115-signing-recovery-prod-01` / 版本 `1.0.0` / 拟发布标签 `plus-v1.0.0-20261115`，实际源码 `sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e`。在原受控身份无法恢复后，按所有者授权换用全新生产公钥 `autumnos-release-20261115-v2`，重新冻结和构建测试打包；没有用新根签旧包。当前 Build 与九组自动报告全部通过，具体计数和原报告摘要见 [当前摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-REKEY-BUILD-AND-TESTS.json)。

SDK 0.4 / 协议1 已实现 `network.getStatus/request`、声明域授权和有界网络代理；运行时已有退出/Job/STA/fence证据与关闭异常独立清理；桌面修复窄屏滚动/排列、拖放和两行名称。SDK03 历史限定原生/GUI只属于其自身冻结构件。本轮正式 native/GUI 为 not_run，完整隔离仍未完成，Win10/11 由用户暂停；普通第三方 required_boundaries 未验证时仍拒绝。详情与迁移限制见 [发布状态](PUBLICATION_STATUS.md)。

## 本地构建

PowerShell7；固定 SDK10.0.401，使用项目 `.tools/dotnet` 和锁文件，不修改系统 PATH。缺项按 [本地工作方式](LOCAL_WORKFLOW.md) 检查。构建使用全新唯一 ID，生产公钥只含公开数据：

```powershell
./scripts/Get-Environment.ps1
$buildId = 'T06-local-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
./scripts/Build.ps1 -BuildId $buildId -BuildVersion '1.0.0' -ReleaseLabel 'plus-v1.0.0-20261115' -ProductionUpdateTrustFile (Join-Path (Get-Location).Path 'build/update-trust.production.json')
./scripts/Test.ps1
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Package-SingleFile.ps1 -BuildId $buildId
```

Test 和 Package-T05 读取 `artifacts/latest-build.txt`，执行前确认对应本次 ID。完整入口在 `artifacts/single-file/<build-id>/AutumnOS.exe`，会在入口旁保留/展开 AutumnOS_Data；目录版必须带齐 DLL 与资源。源码/SDK打包不等于产品GUI通过，附件最终状态由实际发布记录说明。

SDK JS/类型/Schema 在 [sdk](sdk)，CLI 在 [tools](tools)，示例在 [samples](samples)。网络与权限合同见 [SDK网络](docs/sdk-network.md)、[接口](autumnos-spec/docs/03-interfaces.md) 和 [开发文档](autumnos-spec/docs/06-developer-docs.md)。独立 SDK 含完整自包含 CLI，preview/debug 仍需完整运行宿主；真实 Logto Native 类型、回调及浏览器往返未证实，公开配置不需要 Client Secret。

旧客户端不自动信任新根，迁移必须使用新完整入口并保留用户数据/安全账本。未签名草稿不用于无人值守更新；更新 RSA 签名与 Authenticode 分别核验。商店只读发现 GitHub topic/Release，不建设认可名单。当前源码公开不宣称全部 T01–T06 或完整隔离完成。规范与历史在 [autumnos-spec](autumnos-spec)、[工程记录](AUTUMNOS_PROGRESS.md)；许可见 [LICENSE](LICENSE)，第三方版权保持原样。