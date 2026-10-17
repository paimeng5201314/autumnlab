# ChemLeap 与 AutumnOS 接入

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
