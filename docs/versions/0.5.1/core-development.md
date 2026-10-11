# 核心开发 · 0.5.1

本页命令只适用于完整源码工作区。AutumnOS 0.5.1 的原生构建目标是 Windows x64，工程锁定 .NET SDK `10.0.401`、PowerShell 7 及 NuGet 锁文件。机器合同工具要求显式可用的 Node.js ≥22.9，以及 `tools/contract-checks/toolchain.json` 固定的 npm 11.6.1。先检查这些配置、`Directory.Packages.props` 和已有工具；不要为了恢复成功而升级依赖或关闭包签名、校验和、TLS 验证。开发者交付套件的 CLI 已自包含，创建应用不需要重建宿主，见 [第一个应用](first-app.md)。

在源码根的 PowerShell 7 执行以下命令；构建 ID 每次新建，不能覆盖先前证据：

```powershell
./scripts/Install-LocalSdk.ps1
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./tools/contract-checks/restore-tools.ps1
$buildId = 'T06-local-' + [Guid]::NewGuid().ToString('N')
./scripts/Build.ps1 -BuildId $buildId -BuildVersion 0.5.1-local.4 -ReleaseLabel meta0.0.1-20261001
./scripts/Test.ps1
```

安装脚本使用项目内 `.tools/dotnet`，不会要求全局 PATH 替换。构建前确认依赖下载和磁盘空间；WinUI/XAML 编译及原生程序不由 Linux 工具链替代。`Test.ps1` 要求成功且源快照匹配的构建，源码改动后必须生成新构建，不能把旧二进制测试结果关联到新源码。

以下单独执行 JS 测试和机器合同检查，要求前面的合同工具恢复已成功，报告使用新路径：

```powershell
node --test tests/sdk-tests.cjs tests/t03-sample-tests.cjs
$report = Join-Path ([IO.Path]::GetTempPath()) ('autumn-contracts-' + [Guid]::NewGuid().ToString('N') + '.json')
node tools/contract-checks/contract-check.cjs --report $report
```

Linux 可以运行同一 JS 与机器合同检查，但不能直接依赖上面恢复脚本中的 Windows `tar.exe`。云环境应使用已配置的 Linux 安装助手，按相同 toolchain.json 从官方源获取并核验固定 npm 的 SHA-512，恢复相同锁文件到 `.tools/contracts`，禁用安装生命周期脚本；不要改用未经版本核对的全局 npm。原生 WinUI/XAML 构建与 Windows 文件系统语义需在 Windows 另行验证。

机器合同包含真实 TypeScript 严格编译、JSON Schema/OpenAPI 验证和文档链接检查；DOM/SDK 测试使用明确的替身。这些结果不能写成 Windows 构建或真实产品验收通过。持续保持每项验证的执行环境、构建身份、报告位置与未执行范围，记录规范见 [协作与证据](collaboration.md)。
