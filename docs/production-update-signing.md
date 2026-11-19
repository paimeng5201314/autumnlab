# 本地主程序更新签名与发布衔接

当前生产签名身份为 `autumnos-release-20261119-v3`，公钥 SPKI SHA-256 为 `f4c13b436e55b540093d92aee5b0dae126d79cd165af70c2ba4bd45a5f378143`。已在本机从独立加密备份还原，主副两个签名器均在新进程中通过挑战签名和验证。维护者已选定 GitHub 规范仓库名 `paimeng5201314/autumnOS`。客户端、载荷地址与签名清单绑定此名称。旧版 1.0.0 首次切换 v3 需手动运行完整 EXE 并保留 AutumnOS_Data。

2026-11-19：11 月 18 日发布的 1.0.0（`plus-v1.0.0-20261115`）内嵌公钥 `autumnos-release-20261115-v2`，SHA256 为 `7609941f0705632b4353d350282388b18a2901a18c03a95d2677de82b40180dc`。其更新载荷已绑定真实资产，但生产签名失败，公开发行仅支持手工下载。11 月 19 日在相同工具与公钥字节下再次调用 `export-production-public`，仍无法打开 v2 签名器。原身份未被覆盖。11 月 12 日的 v1 以及下文 10 月 24 日迁移例子均属历史记录；各版本信任的实际公钥以冻结构建和包内结果为准。

制作人：派蒙。2026-10-24。此协议的RSA-PSS更新签名与Windows Authenticode发布者证书独立；不需要购买CA证书来建立AutumnOS自己的更新验证根，也不会因此解除Windows SAC拦截。

## 这次更新页故障

本地startup-02未内置生产更新根。2026-10-24只读核查 `paimeng5201314/autumnlab`：最新稳定发布为 `plus0.0.4-20261017`（Release 415751560），仅包含EXE、源码ZIP、release-build.json及SHA256SUMS.txt，没有自动更新所需的 `autumn.update.json`、`autumn.update.sig` 和清单绑定的payload ZIP。只有EXE或SHA256SUMS不满足验签协议。历史发布缺件不能由客户端虚构补足。

更新服务现在分别报告缺少清单、缺少签名、缺少负载、重复资产；设置页只展示一次相同原因。`LastReleaseMetadataUtc` / `ReleasesExamined` 表示本轮GitHub发行记录读取/分页解析成功，不代表签名或负载已验证；`CheckComplete` / `LastSuccessfulCheckUtc` 保留完整检查语义。失败分页或离线不会产生新的读取成功记录。候选、下载确认、维护锁、更新器二次验签与回退规则不变。

## 生产密钥

正常构建默认读取工程 `build/update-trust.production.json`；它只含公开RSA-3072公钥、key ID和purpose=production。Build.ps1显式记录该输入，源快照包含实际公钥字节，Shell与稳定Updater均通过同一Update程序集嵌入。测试信任根仍只能通过显式测试构建启用，不能与生产根混用。

每个本机签名器以工程外的 `<PRIVATE_SIGNING_DIRECTORY>\signing-key.dpapi` 保存工作密钥。只允许当前 Windows 用户访问，以 DPAPI CurrentUser 保护，不放入客户端、源码、报告、ZIP 或 GitHub。复制该文件仅复制受保护记录，即使用户名和账号 SID 不变，也不足以保证换环境可解密。

初始化只能写入新目录；重跑不能重置既有发布身份。对能正常打开的既有签名器使用相应私有目录调用 `sign-production` 或 `export-production-public`。不要用同一 key ID 生成另一把私钥。仅持有公钥、已签名文件或匹配 key ID 都不能签发原身份的更新。

密钥绑定当前Windows用户环境；重装、账号或机器迁移前，应先落实受保护的离线备份/迁移策略，不能认为单独复制DPAPI文件便可在另一账号解密。丢失密钥后不能用下载源提供的新公钥接管旧客户端，需受控完整入口迁移。现版本未实现远程根轮换，不冒充已支持。

### 恢复原身份：有备份才可还原

以下命令需使用含恢复功能、已重新构建并核验的维护者 CLI。旧 1.0.0 冻结工具不含这些命令。若原签名器仍能打开，用 `backup-production` 创建加密 PKCS#8 离线备份；若已有该格式的**同一把钥匙**的备份，用 `restore-production` 恢复到全新私有目录。将可信公钥随备份保存，并独立记录其哈希。下例的 `$trust` 是与目标客户端匹配且已独立核验的公钥绝对路径，其他路径亦应使用本地绝对路径。不要让下载源自行指定信任根。

```powershell
. ./scripts/Common.ps1
$cli = 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll'
& $Dotnet $cli backup-production --private-directory $workingSigner --backup $backup --trust $trust --key-id $keyId
& $Dotnet $cli restore-production --private-directory $freshSigner --backup $backup --trust $trust --key-id $keyId
& $Dotnet $cli export-production-public --private-directory $freshSigner --public-output $freshPublicJson
```

备份文件名以 `.encrypted.pk8` 结束；口令仅在维护者自己开启的私密交互终端中无回显输入（16–256 个字符），不可写进命令、环境变量、日志、脚本或聊天。未能打开的旧 DPAPI 文件无法事后转换为这种备份。还原会用预期公钥验证随机 RSA-PSS 挑战，将同一把私钥重新保护给当前 Windows 用户。更换机器时，在独立受控 Windows 环境实际执行还原、比较导出的公钥并做签名验证，才算验证了跨机恢复。

### 建立可恢复的新身份

维护者自行设置口令时，推荐执行 `scripts/New-RecoverableUpdateSigner.ps1 -CliPath $cli -KeyId $newKeyId -PrimaryDirectory $primary -RestoreDirectory $restored -BackupPath $backup -CandidatePublicPath $candidate`。四个目标均须使用工程外、互不相同、尚不存在的路径，父目录已存在；备份以 `.encrypted.pk8` 结束、候选公钥以 `.json` 结束。脚本依次初始化、备份、恢复到新目录、重新导出并比对，成功后才输出候选公钥，不改任何现有客户端。口令与密文备份分开保管。

同一台可控电脑需要无人值守建钥时，维护者工具另提供：

```powershell
& $Dotnet $cli provision-production --key-id $newKeyId --private-directory $primary --restore-directory $restored --backup $backup --recovery-code $codeFile --public-output $candidate
& $Dotnet $cli restore-local --private-directory $anotherFreshSigner --backup $backup --trust $candidate --key-id $newKeyId --recovery-code $codeFile
```

`provision-production` 生成 256 位随机恢复码，只写到当前用户独占的新目录下的 `recovery-code.txt`。该码可解锁加密备份，因此不要上传、打印到日志、与备份一起打包或存入同一个共享位置。加密备份与恢复码须在不同盘符下；这项检查本身不能证明它们是两块独立的物理介质。备份文件与恢复码文件的父目录均由命令新建；祖父目录应已存在。主签名器、演练恢复目录及候选公钥也必须是全新且互不覆盖的路径。恢复演练从磁盘读回密文和恢复码，验证同一把钥匙后才生成最终候选公钥。若中途失败，保留部分结果于原处调查，不能把它们视为已完成的建钥。

单机两份本地材料不能防整机损坏或已控制当前 Windows 用户的程序读取二者。建议维护者将密文另存离线介质，并将恢复码单独保管，例如受控纸质副本；不要把恢复码交给技术支持。换用户或机器时，如果不能正常读取原用户独占的恢复码文件，用 `restore-production` 在目标受控终端手输纸面恢复码。

**现有用户如何迁移。** 使用 v2 公钥的启动器需要手动下载一次完整的 `AutumnOS.exe`。正常退出游戏和启动器，保留原来的 `AutumnOS_Data`，再从原发行页取得包含 v3 公钥的入口。此后客户端对照自身的构建信息、安全账本和签名清单判定是否有可安装的修订。

**维护者如何发布同版本修订。** 保持产品版本 `1.0.0` 并使用现有标签 `plus-v1.0.0-20261115`。每次替换使用新的构建 ID 和递增的整数更新序号。在 `scripts/Build.ps1` 指定 `-UpdateSequence`，打包时用相同的序号生成清单。先在普通账户中验证较早修订能安装目标修订，核验目标修订再次检查时已是最新，然后记录实际使用的构建、附件和验收结果。Windows Authenticode 发布者证书单独管理。

## 从本地构建到更新附件

1. 按既有Build/Test/Package-T05 -Stage Prepare流程得到关联同一源快照的普通构建；不使用旧包、不启用测试feed。
2. 运行 `scripts/Prepare-ProductionUpdate.ps1 -BuildId <构建ID> -Channel plus -ReleaseTag plus-v1.0.0-20261115 -Sequence <与构建一致的序号>`。脚本核对构建、测试、公钥和实际发行文件摘要，创建并逐文件校验载荷 ZIP。
3. 此时仅有 `autumn.update.draft.json`：releaseId/assetId均为0、没有签名，不可安装，也无法通过sign-production。不能将草案冒充已绑定的正式清单。文件名、版本与tag是本地准备信息，不代表GitHub已发布。
4. 以后另获发布授权，先上传冻结payload，获取真实Release/Asset ID，再用 `bind-release` 生成 `autumn.update.json`，核对真实发布tag、稳定/预览标志与清单channel。此命令只改变绑定字段，重新验证payload，不改变负载字节。
5. `sign-production --manifest <绑定后清单> --private-directory <私有目录> --trust <公钥JSON> --output <autumn.update.sig>`；签名工具验证私钥、公钥、key ID、purpose和root version一致，拒绝未绑定草案、错根及被篡改的私钥记录。
6. `verify --manifest ... --signature ... --trust ... --payload ...`通过后才发布清单和签名。发布完成再执行真实客户端检查/下载/重启升级/失败恢复。当前源限制、HTTPS、payload摘要、序列反重放、到期时间、跨应用及路径拒绝全部保留。

`prepare-draft`生成的清单有效期默认7天；正式发布前应确认时间、单调序号、真实ID及兼容性，过期材料不能通过关闭时钟检查继续安装。维护者不得把测试脚本中的合成Release ID用于真实发布。

## 第一次交付与验证边界

startup-02的稳定Updater没有生产根，而稳定Updater/Bootstrap不在普通更新payload中。首次必须给出新完整入口，不能靠旧版自动下载附件来建立初始信任。本轮交付新EXE/ZIP且保留旧入口；测试新包前应正常退出旧启动器，避免单实例唤回旧版，不清理已有AutumnOS_Data。

`check-production --data-directory <全新诊断目录> --current-version <当前SemVer> --build-id <当前构建> --preview true` 调用实际UpdateService与固定GitHub传输，仅读取公开元数据并在隔离目录记结果，不下载更新payload或启动GUI。检查结果会区分读取成功与签名验证/安装状态。

实际验收以本轮报告为准：合成密钥/惰性文件的签名正反例、公开源读取、最终包字节核对不能代替GUI升级。没有上传或正式发布时，公网自动升级仍为not_run。Windows Authenticode、第三方隔离、ChemLeap普通模式是独立未完成项，不因本项自动通过。
