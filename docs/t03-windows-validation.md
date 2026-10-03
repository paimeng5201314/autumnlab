# T03 本机验证、开发接口与保留边界

制作人：派蒙。所有操作在本地执行；完整状态以 AUTUMNOS_PROGRESS.md 和实际构建目录报告为准。辅助报告不是冻结开发包的证据。

## 本地重复执行

使用 PowerShell 7，在工程根运行：

```powershell
./scripts/Build-ProtocolPeer.ps1 -BuildId T03-你的新编号-peer -Version 0.3.0-t03-protocol-peer.1
./scripts/Build.ps1 -BuildId T03-你的新编号
./scripts/Test.ps1 -ProbeLogto
./scripts/Test-Source.ps1 -ReportPath artifacts/builds/T03-你的新编号/source-check.json
./scripts/Package.ps1 -PeerExecutablePath ./artifacts/protocol-peers/T03-你的新编号-peer/distribution/AutumnOS.exe
```

每次使用新编号；失败包、日志和用户数据保留。Package 在生成 ZIP 前检查 XBF/PRI、发布字节、T02 桌面、同协议不同版本单实例、T03 账号取消/端口占用/单事务、应用权限/桌面扩展/开发模式，以及最终交付路径 EXE 原地运行。所有 UI 脚本先检查已有 AutumnOS 用户窗口；存在时请派蒙正常关闭，脚本不会结束它。清理仅针对脚本实际启动并持有的进程对象。

T03 窗口脚本也可单独指定完整交付 EXE：

```powershell
./scripts/Test-T03AccountSmoke.ps1 -ExecutablePath <本地完整目录中的AutumnOS.exe> -ReportDirectory artifacts/reports/一个新账号检查编号
./scripts/Test-T03FeaturesSmoke.ps1 -ExecutablePath <本地完整目录中的AutumnOS.exe> -ReportDirectory artifacts/reports/一个新功能检查编号
```

账号脚本会打开真实系统浏览器并取消本次事务，不输入账号凭据，不抓浏览器、不关闭用户浏览器标签。真实认证成功、实际刷新和服务端退出必须单独执行，不能根据该脚本通过填写。

## 派蒙的真实身份验收

1. 启动完整开发目录的 AutumnOS.exe。账号入口与双栏设置「账号」为同一真实页面。
2. 点击「登录并保持登录」并在 Logto 网站自行认证，无需向开发工具提供密码、验证码或任何 token。另可用「仅本次登录」明确选择关闭后不保留身份。
3. 返回 AutumnOS 确认真实「已登录」。若报错，只提供界面的固定错误码；不要复制带 code/state 的浏览器地址。
4. 在等待期间再次启动另一份支持 v1 的 EXE，应仅唤回现有页面和原事务。账号回调仍由现有实例的临时 127.0.0.1:17853 listener 接收。
5. 「检查会话」执行实际验证/刷新。关闭窗口后重开同一 EXE 目录，检查 CurrentUser DPAPI 恢复；无 refresh token 时不能断言长期刷新可用。已登录时切换保持登录立即保存/清除本机凭据；清除后当前会话仍可用，关闭再开应未登录。不要把关闭窗口与「退出本客户端」账号按钮混淆；不把密文文件发给其他人。
6. 有游戏运行时切换账号：先取消关闭确认，核对未保存输入仍在；自行保存后重新发起，明确关闭原实例才变更账号。新账号、游客存档默认分开；再次打开游戏是新实例/独立浏览器数据空间。
7. 本地退出清理凭据但保留游戏存档；Logto 浏览器可能还有效。浏览器退出需控制台确认 Post sign-out redirect URI http://127.0.0.1:17853/logout-callback/，失败如实保留，不宣称退出其他设备。

Native/public 类型与登录回调已由派蒙确认，公开 Endpoint/Authority/ClientId 无需再次提供。第二账号、真实离线/过期/刷新和换机 DPAPI 保持独立验收；fixture 成功不是这些项目成功。

## 实际接口与运行示例

- 身份与会话：t03-native-account-guide.md、adr-0004-native-identity-session.md。
- 资料权限及桌面 SDK：t03-sdk-permissions-desktop.md、adr-0006-app-profile-and-permissions.md；TypeScript、JS、Schema 在 sdk/。
- 应用/账号文件存档、配额、备份迁移：t03-storage-and-migration.md。
- 实际元素配对中的身份、存档、桌面扩展示例：t03-samples-guide.md；samples/element-pairs 原有游戏与新增默认折叠面板共同运行。
- 开发模式项目预览/清单/权限/打包/SDK记录/发布资料校验：t03-developer-tools.md。
- 独立服务器身份：adr-0005-independent-server-identity.md、samples/server-identity/README.md 和 openapi.json。需要新的独立 Logto 客户端/API 资源登记，未经确认不会创建或修改后台配置。

## 数据、故障与阶段边界

新版数据仍在正在运行的 EXE 旁；启动转发不会选择另一副本的数据。跨开发包不自动搬迁游戏或存档。需迁移时先正常退出所有实例，在原目录保留完整备份，复制到新的空数据目录前明确核对来源/目标版本；若目标已有数据，不覆盖或合并。凭据仅此 Windows 用户可解密，换机应重新认证；退出或解密失败不删除游戏进度。

恢复保留当前坏档与备份，已有 .before-restore 冲突需要先人工归档到新路径，不通过删除原数据让测试通过。配额不足/磁盘错误/取消不会报告保存成功。缓存、私有数据、安装包、存档属于不同范围；本阶段未接入清理/卸载按钮，不能把其中一种当作另一种。

T01 全通道隔离、物理触控/IME/DPI、Win10/干净机器仍按历史缺项；开发预览只用于明确受控内容，不声称任意第三方已安全隔离。T02 其余桌面能力保留。T04 商店/pm-sq/版本筛选/代理和 T05 plus-meta 安全更新继续待实施；写入协调器与启动单实例都不能代替更新维护锁。开发 ZIP 无 AutumnOS_Data/凭据/真实存档，不是正式签名发行候选。
