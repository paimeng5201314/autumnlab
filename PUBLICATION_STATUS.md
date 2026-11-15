# AutumnOS v1.0.0 源码与验收状态

制作人：派蒙。2026-11-15。当前构建 `T06-20261115-signing-recovery-prod-01`，产品 `1.0.0`，拟发布标签 `plus-v1.0.0-20261115`，源码 `sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e`。

本轮因原受控签名身份无法在当前允许环境恢复，按所有者授权创建全新生产身份 `autumnos-release-20261115-v2`（rootVersion 1）。新公开根 SHA256 为 `7609941f0705632b4353d350282388b18a2901a18c03a95d2677de82b40180dc`。两个独立新进程的非生产随机挑战已验证私钥可打开、签名可验且旧根拒绝；这不等于真实更新清单已签名或远端发布已完成。私钥没有公开。

新公开根进入实际源快照，全部生产构建、九组自动报告和打包重新关联新字节，没有用新身份给旧 prod01 包补签。新正式配置未启用测试根、测试 feed 或临时平台验收开关。实际构建/九组状态与各报告摘要见 [当前构件摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-REKEY-BUILD-AND-TESTS.json)，新公钥挑战的脱敏范围见 [身份摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-SIGNING-IDENTITY.json)。二进制打包或远端资产若仍 pending，以该摘要和随后真实发布核验为准。

SDK 0.4 / 协议 1 的 `network.getStatus/request`、完整声明域权限、有界 HTTPS/DNS/重定向/头部/二进制预算、会话与预览策略租约、最终投递门禁已实现。原生运行时包含有界 IPC、原始进程/Job 与退出证据、资源限额、STA receipt/fence、独立关闭资源清理。桌面已修窄屏排列/滚动、拖放列数和两行名称行高。这些实现属于当前源码；它们不构成完整隔离验收通过。

SDK03（旧 b776 源、临时平台验收配置）有独立的 Server20348/普通 Medium/合成游客限定原生与 GUI 证据：[历史结果](artifacts/publication-sdk03-20261114/PUBLIC-SDK03-RESULTS.json)。历史四项原生、桌面/游戏存档/网络预览 GUI 结果不转记给本轮新根构件。本轮正式 native/GUI 仍为 `not_run`，除非后续有明确同字节报告。旧 qa03 间歇正常关闭失败根因仍未证明，不能用后续通过覆盖。

完整隔离未完成；Windows10/11 验证由用户暂停，保持 `paused_by_user_not_run`。普通第三方应用仍受权限和 `required_boundaries` 门禁约束，未验证边界继续拒绝。真实 Logto 浏览器往返、普通已安装网络应用持久许可 GUI、物理键鼠/触控/中文输入、完整 DPI/系统/安装器/失败回退矩阵仍需真实证据。预览内存许可不代表普通安装许可验收。

旧 `plus-v1.0.0` tag 应保留在 `f8aac024717984914ce26ce0744201f8b5e934cc`；本轮使用新的 tag/source commit，不覆盖旧标签。旧客户端不会自动信任新根，现有版本未实现远程根轮换；同为 1.0.0 也不能承诺自动升级。首次迁移需要新完整入口并保留原 AutumnOS_Data、存档、凭据与安全账本，稳定 Updater/Bootstrap 的实际根和路径必须一致；不能删除 ledger 或启用未签名无人值守更新绕过。生产清单只有绑定真实 Release/Asset、验签及 payload 核验通过后才可提供，未绑定草稿不能安装。Authenticode 是独立证书事项，未声明已完成。

历史冻结文档 `docs/production-update-signing.md` 和迁移说明中旧 keyId、旧发布限制或“SDK网络未实现”等内容只描述当时构件。当前源码/根及实现状态以本页、当前源清单和 SDK0.4 合同为准，其余历史失败与未执行项保留。