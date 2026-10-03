# Lab Chronicles AutumnOS · 本地开发规范与任务包 v0.3

**制作人：派蒙**

本目录应位于当前本地项目根的 `autumnos-spec/`。除非明确写出工程根，本目录文档中的路径均相对本目录。完整开工提示词在工程根 `CODEX_START.md`，使用方式见 `START_HERE.md`。

这是需求、阶段任务、对接合同、公开配置和验收定义，不是已实现的客户端，不包含Windows EXE。本轮没有向GitHub写入、提交Codex任务或发布Release。

## v0.2的变化

在v0.1的56条需求、7个阶段任务和64项验收定义基础上，补入用户截图的Logto公开端点与Client ID，明确Native登录和两个拟定回环回调；扩充A030验证要求。未建设认可名单，未删减既有功能。主程序制作人为派蒙。

截图没有证实Native应用类型或回调登记；公开发现地址在本次环境遇到DNS解析失败，线上注册与登录均未验证。见 `config/logto.registration-status.json`。

## 阅读顺序

先读工程根已有AGENTS.md，再读本目录AGENTS.md、docs/01-baseline.md、docs/02-execution-plan.md、docs/03-interfaces.md与docs/11-logto-integration.md。之后按tasks/index.json和tasks/T00.md开始真实工程工作，不只生成规划。

完整功能范围见requirements.json；验收定义见acceptance-tests.json；发行门槛见docs/05-release-gates.md。历史远端快照不能代替当前本地文件；开工不查询或同步远端源码。保留原LICENSE、README和用户修改。

## 检查命令（在工程根执行）

```text
python autumnos-spec/tools/check_plan.py
python autumnos-spec/tools/check_public_config.py
```

前者仅检查规划依赖与映射；后者仅检查公开配置和本包预定合同，均不连接Logto、不编译Windows程序、不执行产品测试。真实证据由编码、Windows构建和集成测试产生，未执行的项保持not_run。

## 发布目标

用户希望尽快发布，但不允许用假登录、静态演示、跳过恢复测试或自行删减范围替代交付。plus与meta都必须满足相应质量检查。先交付真实候选和证据，未经明确授权不发布公共Release、不移动生产标签。

## 配置与安全

公开配置可以提交；密码、PAT、refresh token、client secret和签名私钥不可进入提示词、仓库或普通日志。客户端主程序仅持更新验证公钥。缺少签名私钥不会阻止工程基础开发，但不能由此放开无人值守未签名更新。

## v0.3：本地工作方式

使用本地文件夹作为工作区，不要求Git/GitHub，不执行远端源码获取或上传。原56条功能需求保留，新增R057本地开发与交付，共57条需求、65项验收定义、7阶段；全部产品测试状态仍需真实执行后更新。

本地Windows脚本承担构建、测试和打包；无commit时用源文件快照与内容哈希。商店、更新等联网产品能力不变。完整本地规则见工程根LOCAL_WORKFLOW.md。
