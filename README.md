# Lab Chronicles AutumnOS

**制作人：派蒙。单文件预览版：meta0.0.1-20261001。**

Windows 原生 .NET 10 / WinUI 3 客户端，运行内部 `.autumn` 应用。当前交付为一个 `AutumnOS.exe`，不提供安装包。

## 下载与运行

目前正确 EXE 已在制作人的 Release 草稿中，尚未公开发布。草稿发布后，到 [Releases](https://github.com/paimeng5201314/autumnlab/releases) 下载 `AutumnOS.exe`，放入用户可写目录，双击即可使用。首次准备需要展开运行资源，会在 EXE 同级创建唯一的 `AutumnOS_Data` 文件夹；运行资源、配置和存档都在其中。移动程序时一起移动此文件夹可以保留数据。首次准备有进度窗口。本机普通缓存实测首次约 26.78 秒、再次约 3.94 秒，其他设备尚未验证。

本版 EXE 包含 .NET、Windows App SDK、固定 WebView2、内置游戏、开发者 SDK/模板及许可证，不需要复制旧发行目录的 DLL。准备器使用 Windows 自带 .NET Framework 4.x；干净系统和完整离线首次运行矩阵尚未执行，不承诺所有环境完全离线可用。

## 版本与校验

- 显示版本：`meta0.0.1-20261001`
- 内部兼容版本：`0.5.1-local.4`
- 构建：`T06-20261003-single001-03`
- EXE 大小：488,573,230 字节
- EXE SHA-256：`d6fe6e54b20ed76f2829573de877585c60cc8696b56fb36a79033f5e447f3381`
- 源快照：`sha256:65737bd0c4b759354d832795cd98aafa5feabd711e8096918379220cb4aefb39`

PowerShell 可执行 `Get-FileHash ./AutumnOS.exe -Algorithm SHA256` 核对下载完整性。摘要不代表发布签名；EXE 尚未 Authenticode 签名，生产更新信任根尚未配置，自动安装保持禁用。

## 本轮验证与限制

595 C#、22 SDK、358 机器合同、6 DOM 模拟、21 合成身份、7 本机后端 HTTP 负面/前置条件、12 CLI 检查通过。两个独立原生单文件运行检查各 41/41，通过实际首启、内部游戏、权限与保存、后台等待、重复启动唤回原窗、正常结束以及重启数据保留。嵌入内容篡改、非法参数、Data junction 三项真实负面控制通过。

这些结果只覆盖本构建对应的断言。完整 T06 仍在进行：新单文件布局的完整 A/B 更新回退、真实 Logto/T03 原收尾、完整社区网络隔离、全部原 UI/数据验收、Windows 10/干净系统/DPI/输入法等环境矩阵、第三方许可补齐及制作人候选审阅仍未完成。历史测试不能代替本版验收。当前上传是经制作人授权的预览交付，不表示完整发布门禁通过。

## 本地开发

工程需要 Windows、PowerShell 7 和锁定的 .NET SDK 10.0.401。SDK 使用项目内 `.tools/dotnet`，不会修改系统 PATH。首次恢复从官方源联网获取依赖；先阅读安装脚本再按自己的环境安装本地 SDK。

```powershell
./scripts/Install-LocalSdk.ps1
./scripts/Get-Environment.ps1
./scripts/Restore.ps1 -Locked
./scripts/Build.ps1 -BuildId T06-local-source-check-01 -BuildVersion 0.5.1-local.4 -ReleaseLabel meta0.0.1-20261001
./scripts/Test.ps1
```

构建 ID 必须唯一；本地构建结果不是已上传 EXE 的重复下载。单文件打包、受控辅助构建及原生验收步骤见 [单文件工程说明](docs/t06-single-file.md)，机器合同见 `schemas/`，开发者资料见 [版本化文档](docs/versions/0.5.1/README.md) 和 `sdk/`、`samples/`。原始任务和全部验收 ID 保留于 `autumnos-spec/`。

对应源快照及公开交付记录见 [release/meta0.0.1-20261001](release/meta0.0.1-20261001/)。源码不包含用户数据、真实凭据、签名私钥或测试默认源。仓库现有 `LICENSE` 保留；各运行组件仍遵守其自身第三方许可证，不能据此宣称许可门禁已全部通过。