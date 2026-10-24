# GitHub 上传完成记录

制作人：派蒙。2026-10-24，Asia/Shanghai。

已按派蒙“将所有内容以派蒙身份上传到对应GitHub仓库”的明确授权完成本次上传。源码提交、Release作者和所有附件上传者均核实为 `paimeng5201314`，未借用其他发布者身份。

- 仓库：https://github.com/paimeng5201314/autumnlab
- 预览发布：https://github.com/paimeng5201314/autumnlab/releases/tag/meta-v0.5.2-local.9
- EXE：https://github.com/paimeng5201314/autumnlab/releases/download/meta-v0.5.2-local.9/AutumnOS.exe
- 公开交付ZIP：https://github.com/paimeng5201314/autumnlab/releases/download/meta-v0.5.2-local.9/AutumnOS-0.5.2-local.9-public.zip
- 本轮公开源码、规范、SDK、自有探针与保留报告：577个文件逐一与GitHub树内容摘要核对，源码提交95b830c24f1ee396c8eb2605948ac63e4c69c64a；另保留17个远端既有文件，包括LICENSE、工作流和历史发行记录。
- 发布标签指向753b26b13869fd27cd6e70122cc9113cf08b145e；这次补入真实更新清单、签名、校验表及核验记录。后续主分支追加发布后核验报告，不移动既有标签。

Release ID **422539615**，预览版，未设为Latest。既有稳定Latest仍为plus0.0.4-20261017（415751560）。10个附件均核对真实ID、名称、长度、GitHub SHA-256和上传账号，见 `published-release-verification.json`。EXE仍是原核验产物，SHA-256：`6db3dceec996df71fe0030a7f006d4c2ac18f383c17e538d2865b78cb84dfe11`。

更新载荷真实Asset ID **671383797**。已用现有生产身份在本地签署绑定清单，并重新验证完整535950413字节payload；公钥与交付客户端内嵌根一致。`autumn.update.json` SHA-256：`83e55ce8ae781c2c21c8573f2fd947fc15efad0b5d6276b9966a9e707d03533d`；签名SHA-256：`d65e459d6ee39eb205ec5ad86239232a9e3fca4bce6f639df2e26b159e249f3e`。私钥仅由本机签名工具使用，未导出或上传。

发布后实际UpdateService读取3条发行记录并成功验证本次公开清单；用新客户端信任根和明确标识的较旧版本参数0.5.2-local.8进行的诊断识别候选meta-v0.5.2-local.9。实际当前版本0.5.2-local.9不提出同版本升级。两次均下载0字节、installation=not_run；这不是旧客户端真实升级或GUI验收。原始报告分别在 `live-published-older-version-probe/check-result.json` 和 `live-published-current-version/check-result.json`。

两个报告仍有 `checkComplete=false` / `UPDATE_MANIFEST_MISSING`：历史稳定版和旧预览版缺协议资产，没有篡改旧发布补假材料。首次生产根迁移仍需完整新EXE。Windows Authenticode证书仍缺少，EXE为NotSigned；真实重启升级/回滚、Logto双账号和第三方完整隔离仍未验收，ChemLeap普通模式继续拒绝。

公开ZIP从冻结交付清单制作，18文件回读通过，8份文本仅对本机路径脱敏，EXE字节不变。公开源码归档保留精确构建源码与当前公开工程两份；最新维护文档与构建时仅有密钥迁移说明差异，公开路径替换和发行文档变更另有摘要记录。公开清单不含私钥、GitHub凭据、Logto令牌、真实存档、运行数据、缓存或私人截图。源码窄模式扫描不是完整安全审计。

失败准备记录保留，未上传失败临时目录。第一次签名前校验发现GitHub草稿标签变化，已拒绝签名并保留记录，随后显式设置正确tag并重新读取核验；没有降低绑定检查。公开副本的两次失败准备目录清理曾被自动审批拒绝，已留在本地，不重试删除。

本地完整记录：`artifacts/github-upload-20261024/`。原工程源码、旧入口和用户数据未被远端文件覆盖，没有启动云端构建、没有强杀程序或修改系统安全策略。
