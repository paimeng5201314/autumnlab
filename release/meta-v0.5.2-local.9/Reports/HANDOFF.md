# AutumnOS 更新配置修复交付

制作人：派蒙。2026-10-24。全部工作在本地，未执行Git、上传或发布Release，未修改全局安全策略。

后续维护：派蒙已授权清理旧测试/构建并迁移密钥。当前交付EXE/ZIP及报告保留且哈希复核一致；签名密钥现在位于D盘工程外。实际清理及保留范围见 [CLEANUP-HANDOFF.md](<WORKSPACE>/artifacts/update-fix-20261024/CLEANUP-HANDOFF.md)。部分未被当前报告依赖的历史产物已按授权删除，旧进度记录中的历史路径不代表仍在磁盘上。

**本地缺少生产更新公钥、更新页重复错误、缺件原因不明确的问题已经修复。公网自动安装尚不可用：现有发行附件缺少协议要求的签名更新资产。** 本次为代码与产物核验交付，真实GUI操作按派蒙要求交给本人检测。

## 入口与实际产物

- 新快捷方式：[AutumnOS-更新修复-20261024.lnk](<WORKSPACE>/AutumnOS-更新修复-20261024.lnk)
- 新EXE：[candidate/AutumnOS.exe](<WORKSPACE>/artifacts/code-handoff/T06-20261024-update-04/candidate/AutumnOS.exe)
- 交付ZIP：[AutumnOS-0.5.2-local.9-code-check.zip](<WORKSPACE>/artifacts/code-handoff/T06-20261024-update-04/AutumnOS-0.5.2-local.9-code-check.zip)
- 源码ZIP：[439文件源快照](<WORKSPACE>/artifacts/update-fix-20261024/AutumnOS-source-T06-20261024-update-04.zip)
- 机器可读总报告：[update-04-verification.json](<WORKSPACE>/artifacts/update-fix-20261024/update-04-verification.json)
- 用户与维护者说明：[production-update-signing.md](<WORKSPACE>/docs/production-update-signing.md)，交付ZIP内同文档名为PRODUCTION-UPDATES.md。

构建 `T06-20261024-update-04`；内部版本 `0.5.2-local.9`；显示版本 `0.0.5-20261024-update`。

| 内容 | SHA-256 |
|---|---|
| 实际源快照 | 5bcc9db971e6cda62f823b8301a1bb8e6bcf788a1c11505e189213ef1b421a58 |
| 最终EXE，488770298字节 | 6db3dceec996df71fe0030a7f006d4c2ac18f383c17e538d2865b78cb84dfe11 |
| 交付ZIP | 9315d6c7bdddb5709b3e129f6313936fadd0124b96df565adfa64e240dd2444c |
| 源码ZIP | d3bd03b3035417d6d1b5f16ae2e8828ada737a82189e038ad2acb76a512c12b3 |
| 生产公钥JSON | d973775e1b5b1a60042034560e445072f59643da69eed7ec4fcb5ab3eff8f98c |

环境：Windows 11 25H2 26200.8037，项目SDK 10.0.401，固定WebView2 154.0.4258.53 / SDK 1.0.4258.31，更新信任根版本1，运行隔离策略仍为autumn-local-web-v2。

## 实际修复

1. `ProductionUpdateSigner.cs` / 更新CLI：建立专用RSA-3072更新签名身份、导出公钥、签署已绑定清单；拒绝覆盖现有身份、错误公钥、未绑定草案和重定向路径。实际生产公钥为autumnos-release-20261024，私钥使用当前用户DPAPI及创建时私有目录ACL保护，留在工程外。没有把私钥放入源码、客户端、报告或ZIP。初始化后已通过受保护密钥恢复导出同一公钥。
2. `AutumnOS.Update.csproj` / `Build.ps1`：普通构建嵌入生产公钥，并把实际输入字节计入源快照；测试根仍只用于显式测试构建。
3. `UpdateService.cs` / `UpdateModels.cs` / `UpdateUi.cs`：区分发行列表读取成功与完整可安装更新验证；准确区分缺清单、缺签名、缺载荷和重复附件；同一错误不重复展示。没有放宽来源、签名、摘要、确认安装或更新器二次验证。
4. `Prepare-ProductionUpdate.ps1`：只从已核对的构建交付文件复制到新stage，严格校验原目录完整文件集合、复制副本与最终草案的路径/长度/摘要，拒绝额外DLL混入。生成真实payload和待绑定草案，保留稳定Bootstrap/Updater及私有数据排除规则。
5. 更新了测试、交付文档和真实交付窗口测试中的公钥预期。原启动诊断修复与证书尾部兼容处理仍在本次产物内。

## 验证结果

| 验证 | 结果 | 证据或边界 |
|---|---|---|
| Windows Release构建 | passed | 零警告、零错误 |
| C#自动测试 | passed，890/890 | 包含新增9项更新来源/缺件测试、12项生产签名工具测试 |
| 实际本地CLI签名/绑定/验签 | passed，17/17 | 自有临时DPAPI密钥及惰性文件；正例可验签，篡改清单/同长度载荷、错根、未绑定ID均拒绝；额外/缺失/大小写重复文件拒绝。合成ID910001/920001仅为测试，从未联系或发布 |
| SDK、机器合同、元素配对样例、ChemLeap模型、身份客户端、HTTP样例、开发者CLI | passed | 七组各有匹配此构建与源快照的报告；样例/模型检查不冒充产品GUI |
| 静态源码检查 | passed，86项 | 不代替运行安全验收 |
| 实际生产UpdateService公开源读取 | passed | 普通根配置true；读取2条发行记录；UPDATE_MANIFEST_MISSING；完整检查false；候选null；下载0字节 |
| 最终公钥实际字节 | passed，6处 | prepared独立DLL、Bootstrap及Updater内嵌DLL；外层EXE内同3处。均匹配公钥、BuildId、源快照，无TestTrust/TestFeed |
| EXE/ZIP与解压副本 | passed | ZIP全部18文件回读校验；两个最终EXE同摘要；实际尾部布局及payload摘要核验 |
| 本地更新载荷 | passed | 1703个managed文件逐项核对；草案ID均0、无签名，不可安装；没有伪造远端绑定 |
| Windows Authenticode / SAC接受 | not_run / NotSigned | 尚无CA发布者证书。AutumnOS更新公钥不替代Windows发布者签名 |
| 新EXE与解压副本GUI、真实下载安装、重启升级、GUI故障恢复 | not_run | 留给后续实际检测；线上缺签名更新资产 |
| ChemLeap普通模式、原第三方完整隔离 | not_run / 继续阻止 | 剪贴板边界等原缺口未完成，本轮没有放行 |

详细证据：

- [最终公钥验证及可重复脚本](<WORKSPACE>/artifacts/update-fix-20261024/final-embedded-trust-04.json)，脚本为同目录Verify-FinalEmbeddedTrust.ps1。
- [真实更新服务读取](<WORKSPACE>/artifacts/update-fix-20261024/live-check-10e055609dab4e3fb3f001452a85dcad/check-result.json)。CLI退出0只表示发行元数据已读取，不能视为可安装更新成功。
- [最终EXE只读解析](<WORKSPACE>/artifacts/startup-failure-20261024/bundle-readback-f87fdfa657004c64864abd462ac296f7/report.json)。没有启动产品。
- [本地待发布载荷准备](<WORKSPACE>/artifacts/update-preparation/T06-20261024-update-04/preparation.json)。载荷SHA为87ed4d54ef028df8bf3bdb807c3101e7af2ce126d78cb83423ee626727a0dc30。
- 所有自动检查在 `<WORKSPACE>\artifacts\builds\T06-20261024-update-04\`；交付ZIP携带对应报告。

保留失败证据：最初签名工具bootstrap的10/12结果及修复后12/12分别在signer-bootstrap-tests.json和signer-bootstrap-tests-02.json；update-03更新工具第12项曾因测试脚本改变UTC时区而先触发元数据拒绝，原报告保留于该构建test-runs目录。修复仅替换清单buildId原始字节，当前严格SIGNATURE_INVALID断言已通过。准备脚本的额外文件漏洞已修复并增加有正向对照的拒绝测试。

## 派蒙实际检测

1. 正常退出旧启动器，再打开新的“AutumnOS-更新修复-20261024”快捷方式。不要同时使用旧入口，避免单实例唤回旧版。此候选在独立目录，不自动迁移或清理旧AutumnOS_Data；新目录里的空数据不代表旧存档被删除。
2. 在设置确认构建为T06-20261024-update-04，再点“检查更新”。网络可用时应显示发行记录读取结果；不应继续显示“缺少正式更新可信公钥”。现有线上附件仍缺清单，因此没有可安装候选是当前真实结果。
3. 如果Windows仍显示智能应用控制拦截，属于尚未取得受信Windows发布者签名的独立问题。此包没有修改SAC或防火墙设置。

首次必须使用完整新入口：旧startup-02稳定Updater未内嵌生产根，而普通更新payload不替换稳定Updater。以后若授权发布，需取得真实Release/Asset ID，再绑定、签名、验签及发布，并做真实GUI升级测试；草案默认7天有效，过期需重新准备。当前没有远端写入授权，未发布任何附件。

旧根入口SHA仍为71d82b083a82c62d159f146fb960c4539f3b9b96070763b2ec910b692166d6b1，用户数据未读取或清除。用户提供的startup-02截图只证明曾进入设置页，不证明Windows签名或完整隔离验收通过。原工程根完整隔离任务文件仍缺失，已说明，未假称读取。所有历史阶段没有因此全部完成。
