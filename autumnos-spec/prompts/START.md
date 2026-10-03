# 本地开工任务入口 v0.3

工程根的 `CODEX_START.md` 是完整任务书。本文件是规范目录内的导航入口，不替代实际编程。

你正在开发Lab Chronicles AutumnOS，制作人：派蒙。先读取当前本地文件夹、工程根AGENTS.md及LOCAL_WORKFLOW.md，不读取远端源码或要求.git，再读取本执行包README、AGENTS、需求基线、依赖顺序、接口与Logto接入合同，然后执行tasks/T00.md。保留LICENSE和已有用户修改。

Logto端点与应用ID已给出，见config/logto.public.json。Native应用类型和本包新增回调尚需确认；不假称已经登记或测试。不能要求用户提供client secret。

先交付可构建原生工程、真实最小窗口、数据根、集中署名、公开配置校验、测试和本机Windows构建流程。T00通过后推进T01及其余依赖满足的阶段，不把T00成果冒充整个项目完成。

每阶段在工程根AUTUMNOS_PROGRESS.md记录真实命令、结果、证据、阻塞和下一步。缺少Windows执行环境时如实记录not_run，完成可做的独立工作并安排Windows验证；不要将项目改成网站来绕过平台要求。
