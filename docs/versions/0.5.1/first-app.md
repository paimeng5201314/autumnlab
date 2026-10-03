# 从空目录做第一个应用

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。以下只依赖完整便携包的Developer目录、PowerShell和正常运行的宿主，不使用工作区隐藏文件。使用一个全新目录；已有项目、包和数据不会被覆盖。

在Developer目录打开PowerShell：

~~~powershell
$cli = Join-Path (Get-Location) 'AutumnOS.Developer.Cli.exe'
$hostRoot = Split-Path (Get-Location) -Parent
& $cli create hello-app ./work/hello --app-id cn.example.hello --name '我的第一个应用'
if ($LASTEXITCODE -ne 0) { throw '创建失败；保留已有目录。' }
& $cli validate ./work/hello
if ($LASTEXITCODE -ne 0) { throw '校验失败；修正项目再打包。' }
$packed = & $cli pack ./work/hello ./work/packages | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $packed.packagePath) { throw '没有生成真实应用包。' }
$package = $packed.packagePath
~~~

create从交付Templates复制真实模板、两个共享资源及SDK/autumn-sdk.js，重写清单身份后由真实解析器检查；版本初始0.1.0。validate实际检查有限资源和清单；pack输出JSON中的packagePath是之后要使用的真实新 `.autumn` 路径，不猜测文件名。普通网页里运行会得到HOST_UNAVAILABLE，不自动模拟宿主。

正常打开父目录AutumnOS.exe，在设置→开发者诊断开启开发者模式。继续使用上一步命令实际返回的包路径：

~~~powershell
$opened = & $cli preview $package --host $hostRoot | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $opened.status -ne 'passed') { throw '预览没有实际打开。' }
$session = $opened.sessionId
~~~

宿主显示原生确认，核对appId、版本、权限和哈希；确认后真实内部导航完成，CLI返回sessionId。保持应用内输入，返回桌面再继续应保留同实例。preview是开发预览，不冒充商店已安装登记；商店安装使用真实release/store清单，由生产源或明确隔离的测试源提供，详见[应用发布](app-publishing.md)。

~~~powershell
& $cli debug $session status --host $hostRoot
& $cli debug $session trace --host $hostRoot
& $cli debug $session background --host $hostRoot
& $cli debug $session foreground --host $hostRoot
& $cli debug $session close --host $hostRoot
~~~

debug只控制自己的当前开发预览，支持status/trace/clear-trace/foreground/background/close，以及下节明确标记的测试模拟。旧session失效；关闭开发模式停止监听、取消工具、关闭预览并清理调试记录。没有suspend、任意EXE、任意账号值或网络代理命令。

## 当前预览的受控模拟

模拟仅用于已经由用户确认的开发预览。普通启动、商店安装、账号系统和真实Logto不读取这个状态。再次preview后从实际JSON取得当前sessionId：

~~~powershell
$opened = & $cli preview $package --host $hostRoot | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $opened.status -ne 'passed') { throw '预览没有实际打开。' }
$session = $opened.sessionId
& $cli debug $session deny-permissions --host $hostRoot
& $cli debug $session status --host $hostRoot
& $cli debug $session restore-permissions --host $hostRoot
& $cli debug $session offline --host $hostRoot
& $cli debug $session online --host $hostRoot
~~~

| 固定action | 实际范围及恢复 |
|---|---|
| deny-permissions / restore-permissions | 在当前预览临时拒绝所有已声明权限／恢复已有内存决定；恢复不会自动授予原先prompt或denied的权限，不修改正式授权文件 |
| account-a / account-b / account-guest | 原生确认后关闭旧预览并重建已核对的相同包，进入固定测试账号A／B／游客；新sessionId必须取自响应，旧请求与句柄失效 |
| offline / online | 当前预览测试状态；固定测试账号资料的isCached为true／false。外部网络本来已受宿主限制，不改变操作系统网络或商店/更新 |
| reset-simulation | 原生确认后关闭旧预览、恢复live身份及默认模拟状态并返回新sessionId；不会擦除已保留的测试存档 |

切换测试账号及reset会结束当前预览，未保存输入需先自行保存；确认默认取消，取消后不切换。以下命令会等待宿主原生确认，并只在成功后替换sessionId：

~~~powershell
$changed = & $cli debug $session account-a --host $hostRoot | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $changed.status -ne 'passed') { throw '账号模拟没有切换；保留旧session。' }
$session = $changed.sessionId
& $cli debug $session status --host $hostRoot
~~~

account-b、account-guest、reset-simulation同样读取新ID。A/B显示“开发测试账号 A/B”，没有头像或任何凭据；它们不能调用真实后端。身份和数据命名空间包含本次预览的随机范围；同一范围回切A可读取自己的A存档，B/游客不共享；关闭开发模式后再预览使用新范围，不复用上一范围。

响应中的simulation字段明确给出isTestSimulation、account（live/guest/account-a/account-b）、permissionsDenied、offline和networkScope=preview_only_external_network_already_blocked。state是宿主枚举文本Starting/Foreground/Background/Suspended/Closing/Closed/Crashed，与应用SDK的小写生命周期值不同。状态不是独立账号认证或系统断网证据。机器合同在SDK/developer.schema.json，专用管道只接受固定动作与当前32位hex sessionId，不接受token、userId、任意参数或命令。帧≤65536字节、JSON深度8；连接3秒、客户端总等待125秒、宿主操作120秒。USER_CANCELLED、DEVELOPER_BUSY、DEVELOPER_MODE_DISABLED、DEVELOPER_PREVIEW_SESSION_EXPIRED按实际响应处理，超时后先status确认，不自动重放切换。

按同一命令创建identity-app、save-game、desktop-extension，使用不同appId和新目录。identity-app在游客下应AUTH_REQUIRED；save-game首次明确保存才请求saves，恢复备份/迁移都需明确点击；desktop-extension先申请各权限，再点击通知/组件/链接。四模板不得继承内置元素配对的权限或存档。

保存复现命令、Node/OS/BuildId、实际包SHA、CLI退出码和宿主结果。测试工具自己按文档跑完称“干净目录开发者自测”；没有真实外部新人就保持外部新人not_run。资料成功、第二账号和后端真实往返按外部条件分别记录，不能用游客、固定测试账号或fixture替代。继续阅读[SDK](sdk-reference.md)、[数据](data.md)、[身份权限](identity-permissions.md)。
