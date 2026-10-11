# Lab Chronicles AutumnOS

**制作人：派蒙。当前预览发行：meta0.0.3-20261011。**

Windows 原生 .NET 10 / WinUI 3 客户端，运行内部 `.autumn` 应用。

2026-10-11 预览版：主程序更新支持启动检查、确认后下载、指定版本与受支持回滚、成功后固定版本；首次配置增加可跳过的账号登录，保留上一版数据桥和诊断修复。[Windows Actions](https://github.com/paimeng5201314/autumnlab/actions/runs/38126950961) 已完成真实 WinUI Release 构建、完整 C# 694/694 与全部要求的非交互测试，生成完整 EXE。交互界面、真实 Logto 和实际更新重启尚需验收，详见 [本轮验收记录](docs/update-first-login-validation.md) 和 [使用说明](docs/t05-updates-and-recovery.md)。

## 下载与运行

从 [meta0.0.3-20261011 Release](https://github.com/paimeng5201314/autumnlab/releases/tag/meta0.0.3-20261011) 下载 `AutumnOS.exe`，用同页 `SHA256SUMS.txt` 校验。内部版本为 `0.5.1-local.6`；EXE SHA-256 为 `4bf30b9405329aeedc4a8755476919f348d2e974029849ce32fe9b2702453cd8`，构建身份见 `release-build.json`。这是预览版，稳定 Latest 仍保留 `meta`。旧版升级须退出后替换完整 EXE，保留同目录 `AutumnOS_Data`；此次包含启动器和更新器协议过渡。生产可信更新根与签名更新资产尚未配置，主程序内可信安装保持禁用。

单文件 `AutumnOS.exe` 放入用户可写目录，双击即可使用。首次准备需要展开运行资源，会在 EXE 同级创建唯一的 `AutumnOS_Data` 文件夹；运行资源、配置和存档都在其中。移动程序时一起移动此文件夹可以保留数据。首次准备有进度窗口。历史本机普通缓存实测首次约 26.78 秒、再次约 3.94 秒，其他设备尚未验证。

本版 EXE 包含 .NET、Windows App SDK、固定 WebView2、内置游戏、开发者 SDK/模板及许可证，不需要复制旧发行目录的 DLL。准备器使用 Windows 自带 .NET Framework 4.x；干净系统和完整离线首次运行矩阵尚未执行，不承诺所有环境完全离线可用。

## 历史版本与校验

- 显示版本：`meta0.0.1-20261001`
- 内部兼容版本：`0.5.1-local.4`
- 构建：`T06-20261003-single001-03`
- EXE 大小：488,573,230 字节
- EXE SHA-256：`d6fe6e54b20ed76f2829573de877585c60cc8696b56fb36a79033f5e447f3381`
- 源快照：`sha256:65737bd0c4b759354d832795cd98aafa5feabd711e8096918379220cb4aefb39`

PowerShell 可执行 `Get-FileHash ./AutumnOS.exe -Algorithm SHA256` 核对下载完整性。摘要不代表发布签名；EXE 尚未 Authenticode 签名，生产更新信任根尚未配置，自动安装保持禁用。

## 历史构建验证与限制

595 C#、22 SDK、358 机器合同、6 DOM 模拟、21 合成身份、7 本机后端 HTTP 负面/前置条件、12 CLI 检查通过。两个独立原生单文件运行检查各 41/41，通过实际首启、内部游戏、权限与保存、后台等待、重复启动唤回原窗、正常结束以及重启数据保留。嵌入内容篡改、非法参数、Data junction 三项真实负面控制通过。

这些结果只覆盖本构建对应的断言。完整 T06 仍在进行：新单文件布局的完整 A/B 更新回退、真实 Logto/T03 原收尾、完整社区网络隔离、全部原 UI/数据验收、Windows 10/干净系统/DPI/输入法等环境矩阵、第三方许可补齐及制作人候选审阅仍未完成。历史测试不能代替本版验收。当前上传是经制作人授权的预览交付，不表示完整发布门禁通过。

## 本地开发

工程需要 Windows、PowerShell 7 和锁定的 .NET SDK 10.0.401。SDK 使用项目内 `.tools/dotnet`，不会修改系统 PATH。首次恢复从官方源联网获取依赖；先阅读安装脚本再按自己的环境安装本地 SDK。

```powershell
./scripts/Install-LocalSdk.ps1
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Build.ps1 -BuildId T06-local-source-check-01 -BuildVersion 0.5.1-local.6 -ReleaseLabel meta0.0.3-20261011
./scripts/Test.ps1
```

构建 ID 必须唯一；上述版本参数是历史构建示例，新的发行号应按真实 GitHub 顺序和签名身份确定。本地构建结果不是已上传 EXE 的重复下载。单文件打包、受控辅助构建及原生验收步骤见 [单文件工程说明](docs/t06-single-file.md)，机器合同见 `schemas/`，开发者资料见 [工程架构](docs/architecture.md) 和 `sdk/`、`samples/`。原始任务和全部验收 ID 保留于 `autumnos-spec/`。

对应源快照及公开交付记录见 [release/meta0.0.1-20261001](release/meta0.0.1-20261001/)。源码不包含用户数据、真实凭据、签名私钥或测试默认源。仓库现有 `LICENSE` 保留；各运行组件仍遵守其自身第三方许可证，不能据此宣称许可门禁已全部通过。
