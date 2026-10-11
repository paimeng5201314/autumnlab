# 创建第一个应用 · 0.5.1

以下使用 AutumnOS 0.5.1 交付的自包含 CLI；不需要源码工作区或预装 .NET SDK。先运行宿主并在“设置 → 开发者诊断”启用开发者模式。命令在 PowerShell 执行，项目和输出使用新目录，预览时需在原生窗口确认实际包。不要把普通 EXE、用户数据目录或含凭据的工程当作应用导入。

## 1. 定位实际程序与 Developer

**单文件版**：外层 `AutumnOS.exe` 在用户可写目录中。先双击它，等待准备完成并出现桌面；把下面路径替换为此外层目录。通过运行中的客户端定位资源，不能从多个 `Product-*` 中随意选一个：

```powershell
$entryRoot = (Resolve-Path 'D:/AutumnOS').Path
$systemPrefix = (Join-Path $entryRoot 'AutumnOS_Data/System').TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$clients = @(Get-Process -Name AutumnOS.Client -ErrorAction Stop | Where-Object {
    $_.Path -and $_.Path.StartsWith($systemPrefix, [StringComparison]::OrdinalIgnoreCase)
})
if ($clients.Count -ne 1) { throw '请确认此单文件入口的客户端已启动，且能够唯一识别。' }
$hostRoot = Split-Path -Parent $clients[0].Path
```

**普通目录版**：完整目录中应直接存在 `AutumnOS.Client.exe`；从该目录启动 `AutumnOS.exe` 并等待桌面后，改用这一条定义：

```powershell
$hostRoot = (Resolve-Path 'D:/AutumnOS-directory').Path
```

选择适合自己的一个分支后，执行共同的资源检查：

```powershell
$developerRoot = Join-Path $hostRoot 'Developer'
$cli = Join-Path $developerRoot 'AutumnOS.Developer.Cli.exe'
foreach ($file in @((Join-Path $hostRoot 'AutumnOS.Client.exe'), $cli,
    (Join-Path $developerRoot 'SDK/autumn-sdk.js'),
    (Join-Path $developerRoot 'Templates/hello-app/manifest.json'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "交付资源缺失：$file" }
}
& $cli --help
if ($LASTEXITCODE -ne 0) { throw 'CLI 启动失败。' }
```

`--host` 使用 `$hostRoot`，不是 `$developerRoot`、外层 EXE 路径或 `AutumnOS_Data`。CLI 会校验命名管道服务进程确为该目录下的客户端。

## 2. 创建、检查和打包

```powershell
$workRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('Autumn-first-app-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $workRoot 'hello'
$output = Join-Path $workRoot 'packages'
& $cli create hello-app $project --app-id cn.example.hello --name '我的第一个应用' --resources $developerRoot
if ($LASTEXITCODE -ne 0) { throw '创建失败，请保留输出用于排查。' }
& $cli validate $project
if ($LASTEXITCODE -ne 0) { throw '项目验证失败。' }
$packed = & $cli pack $project $output | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $packed.status -ne 'passed') { throw '打包失败。' }
$packed.packagePath
```

`create` 支持 `hello-app`、`identity-app`、`save-game`、`desktop-extension` 四个模板，并复制真实 SDK 和共享资源。项目目录必须不存在；`pack` 每次生成唯一文件名，不能猜测附件名。输出路径必须位于项目目录之外。

## 3. 原生预览和调试

```powershell
$preview = & $cli preview $packed.packagePath --host $hostRoot | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $preview.status -ne 'passed') { throw '预览未获准或宿主不可用。' }
& $cli debug $preview.sessionId status --host $hostRoot
& $cli debug $preview.sessionId trace --host $hostRoot
```

在原生确认窗口中核对应用、来源、权限及摘要后决定是否预览。`trace` 只提供有限方法/结果码，不含正文。`debug` 的账号/权限/离线动作只改变确认过的预览模拟；账号动作需要确认并返回新的 sessionId，后续命令必须使用新 ID。它不产生真实登录令牌或全系统断网。退出开发者模式会关闭预览，项目与已保存文件保留。

验收应从空目录对两种交付形态分别执行：启动、创建、校验、打包、原生确认、打开页面、关闭预览并重新启动。本文只给出与 CLI 代码一致的步骤；没有将本次 Linux 文档检查记为这些 Windows 步骤通过。错误定位见 [排错](troubleshooting.md)，发布材料见 [应用发布](app-publishing.md)。
