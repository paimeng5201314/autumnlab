# ChemLeap 与 AutumnOS 接入

2026-10-30本地适配增量：用户已授权为隔离要求修改游戏；`samples/chemleap`现为0.1.2，修复宿主前台确认、当前与旧生命周期事件、关闭后迟到事件及权限提示期间转后台的请求行为，存档格式1、原玩法与SDK0.3.0保留。模型50项与新增生命周期控制器8项共58/58通过；这些控制器测试使用内存DOM/渲染器/宿主消息夹具，不属于原生隔离或产品GUI验收。本轮没有修改已安装包、真实存档、来源或宿主门禁，没有上传或公开发布；0.1.2完整浏览器/原生第三方运行保持`not_run`。游戏无需CDN、fetch、worker或剪贴板，宿主剪贴板缺口仍须由真实受限宿主解决，不能因修改游戏或版本而豁免。

2026-10-24 当前状态：下文为历史接入记录。当前交付及本轮源码在执行外部包之前因 `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE` 拒绝启动，开发模式不能解除。普通模式 ChemLeap 仍未完成隔离验收；后续按 [隔离实施计划](isolation-completion-plan.md) 修复与实测。不能依据历史“可预览/可普通启动”说明使用已知缺口的旧构建放行。

2026-10-17 · 用户授权将游戏发布为0.1.1。此次统一manifest/界面版本标记和发布材料，玩法、物质表、SDK0.3.0及存档格式1保留；不把宿主隔离改动说成游戏内实现。发布包与旧版字节不同，0.1.0原生16/16、浏览器14/14仅为历史证据，不能直接算作0.1.1整包UI验收。宿主0.5.2-local.1仍为本地候选，普通游戏跳跃/SDK存档读回待桌面允许后验证。当前版本包通过真实CLI校验、逐项ZIP资源hash读回与模型50项测试；公开发布与下载结果见 artifacts/chemleap-011-publication-20261017/delivery.json。下文各日期保留历史状态，不重写旧版验证。

2026-10-17新增宿主0.5.2-local.1接入：普通商店安装包现在走通用、每实例必需的原生隔离自检，通过后才执行游戏。可在商店打开 `paimeng5201314/chemleap`，安装0.1.0，再点桌面ChemLeap游玩；不需要开发者模式。此规则及失败关闭边界见[当前隔离实现](runtime-isolation.md)，实际普通下载/跳跃/SDK存档与最终EXE验证以 `artifacts/runtime-isolation-20261017/` 的交付报告为准。下文保留旧宿主及发布过程的历史记录，旧版门禁说明不代表新构建行为。游戏包未改字节，制作人仍为派蒙。

制作人：派蒙。2026-10-17，游戏0.1.0，应用ID cn.labchronicles.chemleap。

用户指定仓库为 <https://github.com/paimeng5201314/chemleap>。本轮匿名GitHub REST实际读取：repositoryId=1414175562，默认分支main只有10字节README.md，Topics与公开Releases均为空。只读报告在 artifacts/chemleap-20261017/repository-inspection-02/repository.json。此为读取时刻的远端状态；没有克隆、同步、覆盖或写入远端。

用户随后明确要求从头制作3D化学跳一跳。真实游戏已在 samples/chemleap 独立实现，包含WebGL三维场景、H₂起点、15条平衡反应、有解平台生成、点击/蓄力跳跃、难度、计分、结束/重开、暂停、反应图鉴与存档。实现方案、结构、核心逻辑、反应表及规则见 docs/chemleap-game.md。不是将既有元素配对或商店探针改名。

## 本地游戏与发布材料

运行 scripts/Package-ChemLeap.ps1 -OutputDirectory '<全新本地目录>'。项目已有固定SDK副本，脚本核对与 sdk/autumn-sdk.js 一致，使用本轮开发者CLI真实pack → generate-release → validate-publication，再逐个ZIP条目回读hash。生成 publication/ChemLeap-0.1.0.autumn、autumn.store.json、autumn.release.json，实际路径/摘要见对应package-result.json和进度。

在AutumnOS里打开设置→诊断与开发→启用开发者模式，再打开开发者工具，选择真实 samples/chemleap 项目目录，校验项目、打包、预览，确认本地代码后游玩。也可用交付CLI的 `preview '<实际包路径>' --host '<实际完整运行目录>'` 请求同一原生确认。原生自动测试使用CLI校验/打包，再执行真实宿主确认和内部游戏操作；第一次自动文件夹选择驱动失败保留。存档经宿主SDK真实授权并按包来源/游客或账号隔离。关闭后重新预览同一个包可读回同来源存档；重新打包字节变化会产生新预览来源，不自动混用旧档。

普通第三方安装包当前仍受现有社区运行门禁限制。开发预览不表示该门禁已完成。游戏没有加入固定商店探针、认可名单或额外权限，也没有更改CSP、浏览器保护或默认开启不可信代码执行。

## 商店查找与公开下载

本轮启动器补上“打开 GitHub 仓库”，支持完整仓库链接或 owner/name。也可在发现页搜索框粘贴链接后按Enter；详情可刷新应用信息。未具备发布条件的仓库显示缺少标签、展示清单或公开版本，不创建假下载任务。发现查询仍使用 topic:autumnos-app is:public，详情/版本以真实repositoryId核对来源。

本地材料准备后，公开上架需要将 autumn.store.json 放默认分支根，添加 autumnos-app 与 autumn-app-sq 或 autumn-app-pm 标签，每个独立应用版本的Release同时附 autumn.release.json 和其asset指定的.autumn。Tag为版本或v加版本，stable/preview和prerelease一致。大小、SHA-256、应用ID、版本、入口、权限、存档格式从真实包生成并匹配。

本轮没有远端上传/发布授权，未执行上述远端操作。因此真实GitHub下载及普通商店运行保持 not_run；本地应用包与开发预览成绩另记。所有实际产物、构建源快照、原生/模型/浏览器证据和未验证项见 AUTUMNOS_PROGRESS.md 及 artifacts/chemleap-20261017。T01/T04/T06保持原状态，不以本游戏增量宣称全产品验收完成。

## 2026-10-17 后续 GitHub 上传状态

用户随后明确授权将 ChemLeap 上传到自己的上述仓库。本次实际上传21个游戏独立文件，main提交 c2d77bddc21e26fb90687af790fb27a489147703；保留原始提交与README标题。包和两项清单的远端HTTP200下载及官方三层校验通过，8运行文件与原生实测完全同字节。真实下载为 https://raw.githubusercontent.com/paimeng5201314/chemleap/c2d77bddc21e26fb90687af790fb27a489147703/dist/ChemLeap-0.1.0.autumn 。

前文“没有远端上传/发布授权”是上传前的历史状态，现仅ChemLeap上传已获授权并完成。当前GitHub工具没有Release或Topics写接口且本机无可用认证，因此公开Release与商店Topics仍未配置；仓库dist直接下载不代替Release附件，普通社区运行门禁保持不变。仓库公开README/文档均写清限制。完整上传与远端核对记录见 artifacts/github-chemleap-20261017/github-result.json 和 HANDOFF.md；T01/T04/T06不因上传提升。

## 2026-10-17 后续公开发布与商店检索完成

用户进一步明确授权公开发布并完成本机GitHub设备授权。v0.1.0现已公开，Release ID415671030、非prerelease，独立附件.autumn/版本元数据/SHA256SUMS齐全；仓库已添加autumnos-app与autumn-app-sq。公开Release https://github.com/paimeng5201314/chemleap/releases/tag/v0.1.0 。此前“接口/认证缺失、未上架”的段落为历史状态，本次已完成这两项远端操作。

实际HTTP200下载三个Release附件并核对摘要，官方三层校验通过；GitHub Topic和ChemLeap文本查询返回唯一目标；AutumnOS生产适配器真实发现与直达探针均passed，SQ、版本0.1.0、installable_release_count=1、can_install=true、warnings=[]。公开main说明提交c94fc4f6d86ece2e5c5162c15ad7f8376fcde443，游戏标签仍指向c2d77bddc21e26fb90687af790fb27a489147703，8个运行文件和实测包未变化。总报告artifacts/chemleap-store-publication-20261017/delivery.json。

此次通过商店检索/发布元数据与公开附件下载验收，不代替完整商店UI安装或普通社区启动。既有安全门禁和完整阶段状态保持，已验证可玩路径仍是用户确认后的内部开发者预览。
