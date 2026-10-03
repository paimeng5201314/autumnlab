# 本地应用发布材料工具

Lab Chronicles AutumnOS，制作人：派蒙。CLI 仅打包、生成和校验本地文件，不创建仓库、修改 Topics、执行 Git、登录 GitHub 或上传 Release。工具自身的启用不会更改启动器开发者模式开关。

入口项目 tools/AutumnOS.Developer.Cli/AutumnOS.Developer.Cli.csproj。使用项目既有固定 .NET SDK 和依赖锁，在工程根 PowerShell 执行：

~~~powershell
. ./scripts/Common.ps1
Invoke-Dotnet @('build', 'tools/AutumnOS.Developer.Cli/AutumnOS.Developer.Cli.csproj', '-c', 'Release', '-p:Platform=x64', '--no-restore')
$developerCli = 'tools/AutumnOS.Developer.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Developer.Cli.dll'
& $Dotnet $developerCli --help
~~~

新环境先按本工程既有恢复脚本恢复固定锁文件；不要全局安装不同 SDK 或升级依赖。输出为 JSON，退出码 0 表示本地操作完成，2 表示拒绝/失败。Ctrl+C 在下一安全检查点取消，不把已写产物宣称为自动回滚。生成失败保留部分目录便于检查，下次选新目录，不覆盖。

## 打包项目

项目内必须有当前 schema 的 manifest.json、HTML 入口及实际 Web/WASM 资源。复用 DeveloperToolsService.BuildProject 与 PackageInstaller.Inspect，拒绝普通 EXE、隐藏凭据文件、任意命令及安装脚本；权限和路径限制与真正安装相同。项目和输出必须分离。

~~~powershell
& $Dotnet $developerCli pack 'samples/element-pairs' 'artifacts/my-app-packages'
~~~

输出 packagePath、appId、version、sha256、bytes。包名附带 GUID，重复打包不覆盖旧包。后续使用真实 packagePath；源码 ZIP 不能当安装附件。

## 生成两个发布清单

~~~powershell
$options = @('--developer', '你的开发者名称', '--description', '应用的真实简介', '--category', 'sq', '--offline', 'true', '--min-host', '0.3.0', '--min-sdk', '0.3.0')
& $Dotnet $developerCli generate-release '<pack 输出的 packagePath>' 'artifacts/my-publication-01' @options
~~~

输出目录必须不存在。必填 --developer、--description；可选 --category（sq/pm/unclassified，默认 sq）、--offline（true/false，默认 false）、--min-host、--min-sdk（默认 0.3.0）。重复/未知参数拒绝。离线能力是开发者声明，必须符合真实实现。

结果是 autumn.store.json、autumn.release.json、原 .autumn 逐字节副本。包内 manifest.json 不变；版本、入口、权限、saveFormatVersion、大小与摘要从真实包生成。稳定/预览按 SemVer 预览标识决定。生成后再次调用商店共享解析器和开发工具共享校验器，三层一致才报告成功。

按实际能力设置最低宿主/SDK。runtime=web 是当前 Web/WASM 宿主，不是普通 EXE。manifest 可选存档格式范围见 t04-installation.md。应用预览偏好与主程序 plus/meta 无关。

## 校验准备发布的材料

~~~powershell
& $Dotnet $developerCli validate-publication 'artifacts/my-publication-01/autumn.store.json' 'artifacts/my-publication-01/autumn.release.json' '<该目录实际 .autumn 文件>'
~~~

核对三层 appId、一致版本、入口、权限、运行环境、大小、SHA-256、存档格式、附件名。元数据最多 64 KiB，未知/重复字段、错误类型及未来 schema 拒绝。修改任何包字节后重新生成新版本材料；同一发布版本不能偷偷替换内容。

正例返回 status=passed、publishing=not_performed、liveGitHubRegistration=not_verified，只证明本地材料可被当前解析器接受，不证明仓库存在、GitHub 索引完成、运行隔离通过或应用获得额外权限。

负例：把发布摘要改成其他 64 位十六进制值，返回 STORE_PACKAGE_METADATA_MISMATCH；store appId 改为其他应用，返回 CLI_PUBLICATION_APP_ID_MISMATCH；.autumn 改名 EXE，返回 PACKAGE_EXTENSION_INVALID；重复输出目录返回 CLI_OUTPUT_ALREADY_EXISTS。错误仅固定 code，不输出令牌、存档或堆栈。

## GitHub 布局与手动发布边界

默认分支根放 autumn.store.json；manifest.json 在包内；独立版本 Release 同时附 autumn.release.json 与清单 asset 命名的 .autumn。GitHub 自动生成的 source ZIP/TAR 不可安装。每仓库一应用。

仓库 Topic 为 autumnos-app，分类选 autumn-app-sq 或 autumn-app-pm。PM 仓库自标、未核验，不增加权限或跳过检查；两个分类同时存在显示冲突。索引可能延迟，首次来源确认绑定 repositoryId，同名仓库不等于相同来源。

实际上传、仓库/Topic 修改或 Release 发布均需另行明确授权；工具不会执行。本轮只本地生成材料。

## 实际本地证据

artifacts/reports/t04-install/cli-smoke-01/：project 使用独立 appId fixture.cli；packages 保留真实 .autumn；publication 生成一致预览材料；report.json 记录实际进程参数、退出码及 12 个断言，含重复打包保留旧包、坏摘要、跨应用身份、重复字段、EXE 扩展名、伪装原生负载、输出冲突。它不是正常商店商品，没有上传，也没有混入元素配对或用户数据。

冻结构建的可重复验收入口是 scripts/Test-T04DeveloperTools.ps1。默认读取 artifacts/latest-build.txt，报告写入该构建的 t04-tools 新目录；可显式传 -BuildId 与 -ReportDirectory 指定另一个全新报告目录。脚本拒绝覆盖已有报告，先后检查源快照、CLI 及全部 AutumnOS 依赖的 BuildId/SourceSnapshotId/哈希，再执行同样 12 项真实进程测试，记录实际参数和退出码；冻结身份不符时标 not_run，不会把较早模块测试冒充当前构建通过。脚本不编译、不恢复依赖、不创建商店测试源配置。
