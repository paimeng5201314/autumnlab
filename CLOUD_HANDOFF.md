# AutumnOS 云端接续 · 2026-10-31

制作人：派蒙。用户本次明确授权把当前本地源码上传到公开 paimeng5201314/autumnlab 新分支，后续用云端 Codex 开发、GitHub Actions 构建 EXE。这覆盖旧文档中本地工作方式限制，但不授权发布 Release、移动旧 tag、上传私钥/凭据或伪造原生验收。

从本分支开始，而非较旧 main；保留 .NET 10 / WinUI / .autumn / 派蒙署名和现有所有产品要求。Windows EXE workflow 对 codex/windows-exe-* 分支自动运行，使用固定 SDK、本地缓存、既有普通用户构建包装及完整非交互测试。下载 Actions 的 AutumnOS artifact；原生键鼠/中文 IME/LSA/私有窗口站与 UI 故障矩阵不能由云端构建成绩代替。

build/update-trust.production.json 是既有 RSA-3072 生产公钥，必须原样保留，不执行 init-production 或替换 keyId。私钥保存在维护者本机、DPAPI CurrentUser 保护；未上传。Actions 可编译并嵌入公钥，但正式更新清单仍需本机原私钥签署。不要把 DPAPI 文件放进 Git 或把它当作可跨机器的签名凭据。Windows Authenticode 证书是另一套身份，目前未配置。

本地 G2-A02 frozen build 已完成 Release、988 C#、其余9组、101源码检查以及单文件53项原生回归；仅属于当时冻结字节，不能算本分支测试分数。G1 自有原型已验证私有窗口站/零能力 AppContainer/动态图/真实键鼠和中文输入，并完成真实退出清理；完整边界矩阵和生产独立宿主尚未完成。当前第三方 required_boundaries 继续拒绝，ChemLeap 普通模式尚未放行。

G2-B1 RuntimeDeliveryCoordinator.cs 及24个纯测试为正在开发的源码，尚未构建运行。先修复待审问题：两个重叠 fence 在 ChannelGeneration=max-1 排队，首个 ACK 推进到 max 后，后一个 activation 的 checked(max+1) 可能抛出并遗留 writer 状态；退役 endpoint 也不能因为代次耗尽溢出。修复并补确定性回归后，以全新 build ID 跑完整构建/测试。不得把 Unknown、断管或计时器当成功 Post/退出。

生产当前只有进程内 backend 抽取。后续 G2-B2 必须在真实权限/账号/实例提交前接 async fence，涵盖同 epoch SessionExpired，且不持 Monitor 跨 await；再实现独立原生宿主、同序屏障/实际 STA Post receipt、限长 IPC 和 G3–G5 验收。详见 docs/runtime-delivery-coordinator.md、docs/runtime-view-backend.md、docs/isolation-completion-plan.md 和 AUTUMNOS_PROGRESS.md。

artifacts/isolation-completion-20261030 中仅携带13个明确源码目录，保留其 CompileLink 路径；没有复制 builds/runs/invocations、profile、凭据、真实存档或历史 EXE。历史报告路径可能仅在原维护者本地存在；引用不是本云端实际通过证据。原用户数据与本机私钥未改动。
