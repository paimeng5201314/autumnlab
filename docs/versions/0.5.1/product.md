# 产品与交付形态 · 0.5.1

AutumnOS 是 Windows x64 原生 .NET 10 / WinUI 3 客户端，在内部 WebView 中运行受控 `.autumn` 应用。它不是任意 EXE 启动平台。当前版本 0.5.1 可使用内置应用、保存数据、调整桌面排序和打开开发者预览；商店发现与安装成功并不保证普通社区应用已获准运行。全面网络通道隔离仍待验证。

## 单文件入口

把本次单文件交付的外层 `AutumnOS.exe` 放在一个用户可写的新目录，双击并等待准备窗口完成。这个 EXE 已嵌入产品资源，可单独作为首次分发入口。首次准备在同级建立 `AutumnOS_Data`；运行程序及开发套件展开到 `AutumnOS_Data/System/Product-<20 位摘要>/`，升级后的实际目录以正在运行的客户端路径为准。不要另从旧 ZIP 复制 DLL。

外层目录中 `AutumnOS_Data` 同时承载配置、账号数据、存档和运行资源。移动已有环境时先结束应用及宿主，再把外层 EXE 和整个同级 Data 一起移动。不要单独删除 Data 来排错。单文件准备器使用 Windows 自带 .NET Framework 4.x；干净系统、离线首启、Windows 10 及多 DPI 矩阵仍需实际验收，不能从嵌入依赖推导全部设备通过。

## 普通目录版

旧的目录版或源码发布目录需要保留 `AutumnOS.exe`、`AutumnOS.Client.exe`、DLL、固定 WebView2、配置、`Developer` 等完整相邻资源。这种布局中单拷内层 EXE 不构成可运行交付。用户数据在该程序目录同级的 `AutumnOS_Data`，`Developer` 直接位于程序根。

## 开发入口与状态

设置 → 开发者诊断 → 开发者模式可启用原生开发入口。CLI 位于当前程序目录的 `Developer/AutumnOS.Developer.Cli.exe`。预览所用 `--host` 是包含实际 `AutumnOS.Client.exe` 的目录：单文件版为展开后的内层程序根，目录版为完整目录根。准确定位和创建命令见 [第一个应用](first-app.md)。运行用户与 CLI 用户应相同，预览还需原生确认。

桌面支持三列排序、长按/右键移动和运行切换器；文件夹、多桌面、完整搜索、自定义 Dock、抖动编辑尚未提供。系统音量、网络控制尚未接通。生产更新根未配置、Authenticode 签名与许可收尾、独立后端真实往返及完整新布局回退仍是发布缺项，详见 [宿主发布](host-publishing.md)。
