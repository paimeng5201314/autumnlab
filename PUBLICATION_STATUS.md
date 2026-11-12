# autumn正式版v1.0.0 发布准备

制作人：派蒙。检查日期：2026-11-12。

本分支保存当前本地工程源码、规范、SDK、开发文档及必要的自有原生测试源码。它是发布准备分支，**不是已经完成的 v1.0.0 正式发行**。程序源码保持现有版本；没有将旧测试成绩重新计入新版本。

## 当前实际交付

- 构建：T06-20261110-viewport-02；内部版本 0.5.2-local.28，显示版本 0.0.18-20261110-platform-test.2。
- 源快照：sha256:0fe47e8f5de44fa5da45ff53bd4f8d56a220546d02f4954b5cbc533f1550c7c8。
- Windows Release 零警告/零错误，C# 1159/1159、九组自动检查与109项源码检查通过，仅属于该冻结构建。
- 本机普通权限 Server 20348 上完成 ChemLeap 0.1.1 的限定视口/输入/退出与重开验证。Windows 10/11、完整隔离、真实更新和失败恢复的最终矩阵未完成。
- T00 verified；T01–T06 in_progress。生产普通第三方准入仍 denied，完整隔离 not_completed。
- 旧原始日志已按用户清理要求移除，保留摘要；不能宣称完整原始证据仍可重放。

## 正式发布阻塞

1. 当前构建启用了 TemporaryPlatformAcceptance 和 LocalIsolationAcceptance。NativePayloadCatalog 仅接受编译时验收路径或每用户验收目录；普通分发目录不能据此打开商店应用。取消路径门禁不是完成隔离。
2. 当前 AutumnOS.exe 为目录版入口，依赖完整1990文件交付。单独上传该文件不能交付可运行产品。
3. Package-SingleFile.ps1 删除全部 PDB，而 NativeHost 可信文件表包含两个 PDB。须在正式打包前统一文件集合并验证，不能签名或发布一份缺少可信文件的包。
4. 当前可检查范围未发现原生产 signing-key.dpapi。必须恢复与 autumnos-release-20261024 公钥匹配的原签名环境；DPAPI CurrentUser 文件不能直接作为跨机器凭据。不得创建新根假装兼容旧客户端。
5. 当前 GitHub 连接可写源码，但未提供 Release 附件上传工具；本机非交互 GitHub 凭据检查也没有可用凭据。正式二进制发布还需可用的维护者发布登录。
6. Prepare-ProductionUpdate.ps1 的 test_build 检查不足以排除 temporary/private acceptance；在补齐显式拒绝之前，不得直接对本候选运行生产更新准备。

## 拟发行身份及配套资产

GitHub 标题：autumn正式版v1.0.0；内部 SemVer：1.0.0；推荐 tag：plus-v1.0.0；channel：plus；prerelease：false。名称不代表质量门禁自动通过。

必须提供真实新构建的完整 AutumnOS.exe、AutumnOS-1.0.0-win-x64-payload.zip、autumn.update.json、autumn.update.sig、SHA256SUMS.txt、对应源码 ZIP 及构建/发布核验摘要。

更新顺序：冻结源码并构建测试 → 完整入口及载荷验证 → 上传载荷获得真实 Release/Asset ID → bind-release → 原生产根 sign-production → verify → 上传清单与签名 → 下载核验及真实升级/失败回退。

目标旧 Release ID 为422539615，tag为meta-v0.5.2-local.9。检查时它仍是“AutumnOS 0.0.5-20261024-update · 派蒙 · 预览交付”。其仓库归档清单 sequence=1、最低Updater=1.1.0，有效期已于2026-10-31结束；发布前应重新核对在线附件，不复用过期签名。此次未删除旧附件、未移动旧tag、未改旧Release名称。

更早的 EXE 未内置生产信任根，不能通过内部更新载荷补入稳定 Bootstrap/Updater 的首次信任。应明确提供完整新入口迁移，保留同目录整个 AutumnOS_Data。

## 源码范围

未包含真实 AutumnOS_Data、凭据、私钥、工具链缓存、bin/obj、旧错误日志或本机安装副本。artifacts 内仅明确选择的自有测试源码和项目/锁文件；构建依赖的样例/商店夹具由既有脚本重新生成。未要求云端CI替代Windows实测。

本说明与公开README提示属于发布准备文档，不改变冻结客户端字节或源快照含义。
