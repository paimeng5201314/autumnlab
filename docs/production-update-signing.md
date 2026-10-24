# 本地主程序更新签名与发布衔接

制作人：派蒙。2026-10-24。此协议的RSA-PSS更新签名与Windows Authenticode发布者证书独立；不需要购买CA证书来建立AutumnOS自己的更新验证根，也不会因此解除Windows SAC拦截。

## 这次更新页故障

本地startup-02未内置生产更新根。2026-10-24只读核查 `paimeng5201314/autumnlab`：最新稳定发布为 `plus0.0.4-20261017`（Release 415751560），仅包含EXE、源码ZIP、release-build.json及SHA256SUMS.txt，没有自动更新所需的 `autumn.update.json`、`autumn.update.sig` 和清单绑定的payload ZIP。只有EXE或SHA256SUMS不满足验签协议。历史发布缺件不能由客户端虚构补足。

更新服务现在分别报告缺少清单、缺少签名、缺少负载、重复资产；设置页只展示一次相同原因。`LastReleaseMetadataUtc` / `ReleasesExamined` 表示本轮GitHub发行记录读取/分页解析成功，不代表签名或负载已验证；`CheckComplete` / `LastSuccessfulCheckUtc` 保留完整检查语义。失败分页或离线不会产生新的读取成功记录。候选、下载确认、维护锁、更新器二次验签与回退规则不变。

## 生产密钥

正常构建默认读取工程 `build/update-trust.production.json`；它只含公开RSA-3072公钥、key ID和purpose=production。Build.ps1显式记录该输入，源快照包含实际公钥字节，Shell与稳定Updater均通过同一Update程序集嵌入。测试信任根仍只能通过显式测试构建启用，不能与生产根混用。

当前维护者私钥位于工程外的 `<PRIVATE_SIGNING_DIRECTORY>\signing-key.dpapi`。2026-10-24按派蒙要求从F盘LocalAppData迁到此处，复制字节核对、当前用户DPAPI解密与公钥匹配验证通过后已删除F盘原件；没有生成新签名身份。记录在 `artifacts/update-fix-20261024/signing-key-moved-to-D.json`。只允许当前Windows用户访问，以DPAPI CurrentUser保护，不放客户端、源码、报告、ZIP或GitHub。迁移仍依赖同一Windows账号，不能单凭文件位于D盘就认为重装或换账号后可以解密。

初始化密钥目录只能全新创建；重跑不能重置既有发布身份。现有身份直接使用上述D盘目录调用sign-production或export-production-public，不要再次init-production。私钥解密仅发生在本地签名进程内，使用后清零字节缓冲。该保护不抵御已控制同一Windows账号或管理员的恶意程序。

密钥绑定当前Windows用户环境；重装、账号或机器迁移前，应先落实受保护的离线备份/迁移策略，不能认为单独复制DPAPI文件便可在另一账号解密。丢失密钥后不能用下载源提供的新公钥接管旧客户端，需受控完整入口迁移。现版本未实现远程根轮换，不冒充已支持。

初始化/恢复公钥（只由发布维护者使用，不属于普通用户操作）：

```powershell
. ./scripts/Common.ps1
$cli = 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll'
# 私有目录的父目录应先存在；两个输出均必须是新的本机路径。
& $Dotnet $cli init-production --private-directory '<工程外新私有目录绝对路径>' --public-output '<build/update-trust.production.json绝对路径>' --key-id '<确定的新key-id>'
# 若仅公开文件写入失败，原受保护密钥保留。恢复导出不生成新密钥：
& $Dotnet $cli export-production-public --private-directory '<已有私有目录>' --public-output '<新的公开JSON路径>'
```

## 从本地构建到更新附件

1. 按既有Build/Test/Package-T05 -Stage Prepare流程得到关联同一源快照的普通构建；不使用旧包、不启用测试feed。
2. 运行 `scripts/Prepare-ProductionUpdate.ps1 -BuildId <构建ID> -Channel meta -ReleaseTag <拟用tag> -Sequence <单调递增序号>`。脚本核对构建、测试、公钥和所有实际发行文件摘要，创建真实payload ZIP并逐文件校验。
3. 本轮无远端发布授权，只输出 `autumn.update.draft.json`：releaseId/assetId均为0、没有签名，不可安装，也无法通过sign-production。不能将草案冒充已绑定的正式清单。文件名、版本与tag是本地准备信息，不代表GitHub已发布。
4. 以后另获发布授权，先上传冻结payload，获取真实Release/Asset ID，再用 `bind-release` 生成 `autumn.update.json`，核对真实发布tag、稳定/预览标志与清单channel。此命令只改变绑定字段，重新验证payload，不改变负载字节。
5. `sign-production --manifest <绑定后清单> --private-directory <私有目录> --trust <公钥JSON> --output <autumn.update.sig>`；签名工具验证私钥、公钥、key ID、purpose和root version一致，拒绝未绑定草案、错根及被篡改的私钥记录。
6. `verify --manifest ... --signature ... --trust ... --payload ...`通过后才发布清单和签名。发布完成再执行真实客户端检查/下载/重启升级/失败恢复。当前源限制、HTTPS、payload摘要、序列反重放、到期时间、跨应用及路径拒绝全部保留。

`prepare-draft`生成的清单有效期默认7天；正式发布前应确认时间、单调序号、真实ID及兼容性，过期材料不能通过关闭时钟检查继续安装。维护者不得把测试脚本中的合成Release ID用于真实发布。

## 第一次交付与验证边界

startup-02的稳定Updater没有生产根，而稳定Updater/Bootstrap不在普通更新payload中。首次必须给出新完整入口，不能靠旧版自动下载附件来建立初始信任。本轮交付新EXE/ZIP且保留旧入口；测试新包前应正常退出旧启动器，避免单实例唤回旧版，不清理已有AutumnOS_Data。

`check-production --data-directory <全新诊断目录> --current-version <当前SemVer> --build-id <当前构建> --preview true` 调用实际UpdateService与固定GitHub传输，仅读取公开元数据并在隔离目录记结果，不下载更新payload或启动GUI。检查结果会区分读取成功与签名验证/安装状态。

实际验收以本轮报告为准：合成密钥/惰性文件的签名正反例、公开源读取、最终包字节核对不能代替GUI升级。没有上传或正式发布时，公网自动升级仍为not_run。Windows Authenticode、第三方隔离、ChemLeap普通模式是独立未完成项，不因本项自动通过。
