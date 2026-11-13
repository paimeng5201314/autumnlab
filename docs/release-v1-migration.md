# v1.0.0 完整入口迁移与验证

制作人：派蒙。2026-11-13。本文描述正在实现的正式发行候选；是否已经可发布，以对应新构建和真实验收报告为准。

## 新签名身份

用户明确批准重新生成更新身份，key ID 为 `autumnos-release-20261112-v1`，RSA3072公钥指纹为 `4dd00cb2a0a9ca8bd1bde5d24ef2e1ac63b2a8df2fdb780ef1a18e9e56f34e36`。根格式版本仍为1，它不是旧根的远程轮换授权。私钥仅在工程外、当前用户专属ACL的目录内以DPAPI CurrentUser保护，签名进程内部解密且清零；公钥可公开。私钥目录不能放进源码、交付ZIP或GitHub。

旧key ID `autumnos-release-20261024` 公开根仅归档。新旧根必须相互拒绝对方签名；不能把旧公钥改个key ID当成新密钥，也不能关闭旧客户端验签。Windows Authenticode发布者证书是独立问题，本身份不会消除Windows的未签名提示。

DPAPI恢复依赖Windows用户环境；单独复制signing-key.dpapi不构成可跨电脑恢复的备份。未来迁移签名环境时，应先核对受保护的备份和恢复方案，不能丢弃仍有效的身份再声称旧客户端无缝升级。[微软DPAPI说明](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection)

## 从旧版迁移

首次切换新根须下载完整的新 `AutumnOS.exe`，正常退出旧客户端和游戏，保留整个同目录 `AutumnOS_Data`。不要删除存档、设置或正在使用的旧System运行目录。用完整新入口完成迁移；旧版本的稳定Updater不会从内部payload获得新的首次信任。

单文件种子使用自身载荷摘要确定 `AutumnOS_Data/System/Product-<摘要前20位>`，对应的更新绑定也以种子摘要命名。新完整入口采用新种子，保留旧种子与旧更新事务；同一旧种子已更新后缺失运行目录，仍拒绝静默退回内嵌旧版。迁移不得清空全局版本/sequence安全水位。

旧目录版可将完整新EXE置于原入口目录，并保留Data。实际存档读回、旧种子绑定共存及失败恢复必须分别验证；这里只是迁移步骤，不表示这些实测已通过。

安全更新记录改为由稳定的 `AutumnOS_Data/Updates/security-ledger.json` 承接。启动器、更新服务和独立更新器共用严格合并：最高 sequence 与对应摘要、最低安全版本、失败构建集合及可信时间都不能回退；同一 sequence 的不同非空摘要拒绝。启动时应以实际安装回执中的客户端版本检查安全下限，不能使用不会随内部更新替换的 Bootstrap 自身版本。更新服务先把本次安全字段合入账本，再写界面策略，避免旧 128 项显示历史被截断或并发覆盖时丢失失败记录。

首次接续只检查同一 Data 所属入口的旧目录版记录，以及 System 下绑定完整种子/目录/portable marker/安装回执/Entry 的旧运行目录。已有相关记录却绑定损坏、旧事务未结束、同序号摘要冲突或超出资源限制时，保留原文件并拒绝继续，不把未知记录当作空白新安装。共享账本以有界文件锁和原子写入串行合并；已初始化而账本缺失也拒绝。旧记录只提供更严格的本地限制，不带入旧公钥、不授予发行信任，也不声称能防御同一用户对所有本地状态的任意篡改。更新失败的数据恢复不覆盖这个账本。

上述变更需要新的冻结构建及完整 EXE 换代实测；纯逻辑/合成目录回归不能替代真实旧入口迁移和存档读回。

## 正式更新资产

拟发行内部版本1.0.0，tag `plus-v1.0.0`，channel `plus`，GitHub标题 `autumn正式版v1.0.0`。完整EXE、内部payload ZIP、autumn.update.json、autumn.update.sig、SHA256SUMS和匹配源码/构建摘要各有用途。

先冻结真实源码并完成构建、自动测试及原生验证，再上传payload取得真实Release/Asset ID，调用bind-release、sign-production及verify，最后发布元数据。sequence应大于既有发布水位，清单时效以签名时真实时间为准，不复用已过期清单。没有真实ID时只能保留unsigned draft，不制造正式签名成功记录。

`Prepare-ProductionUpdate.ps1` 现在明确拒绝test、private isolation及temporary platform构建，即使其test_build恰好为false；字段缺失、错误类型或相互矛盾的编译标志也拒绝。单文件打包必须保留编译NativeHost/WebView2文件表中的全部资源，包括表内PDB，并拒绝任何缺件或额外native文件。

## 运行资格与验证范围

原来的开发盘/用户名固定验收目录改为编译目录表的schema/build/source/runtimePolicy与当前程序目录/安装回执身份绑定。回执只证明布局和当前版本，不能给包授予权限；宿主、引擎、应用归档实际字节仍由独立封存和原始句柄绑定核验。真实原始进程、零能力AppContainer、私有窗口站、限长管道、每实例文档挑战及最终STA投递回执继续强制。

当前仅支持按已有SDK适配的内部Web/WASM应用；普通Windows EXE不属于包格式。直接联网仍禁止，未实现的SDK network能力不能冒充可用。完整隔离、支持系统、实际输入/账号/故障矩阵必须以新候选逐项记录passed/failed/not_run，不能由版本号或签名代替。
