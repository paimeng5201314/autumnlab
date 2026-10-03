# 桌面外观持久化（T02 检查点）

Lab Chronicles AutumnOS · 制作人：派蒙。

`AutumnOS.Storage.DesktopPreferencesStore` 是仅供可信宿主系统应用调用的 C# 服务，不向第三方 SDK 暴露。文件固定为实际入口目录下的 `AutumnOS_Data/Config/desktop-preferences.json`，不接受任意路径或远端壁纸 URL。

```csharp
var store = new DesktopPreferencesStore(InstallationRoot.ForCurrentProcess());
DesktopPreferencesResult current = store.Load();
DesktopPreferencesResult saved = store.Save("dark", "night");
if (saved.Success)
{
    // 只有成功结果才代表可应用的完整配置；State 不为空。
    string theme = saved.State!.Theme;
    string wallpaper = saved.State.Wallpaper;
}
```

两个方法都接受可选的 `CancellationToken`。结果含 `Success`、`State`、`ErrorCode` 和可恢复说明 `RecoveryMessage`。失败时 `State=null`，不把坏配置回退成已保存的默认值。缺少文件时 `Load` 返回未持久化的 `light`/`warm`、`Revision=0`，不创建设置文件。

| 字段 | 合同 |
|---|---|
| `schemaVersion` | 整数 `1`；更高版本返回 `CONFIG_FUTURE_SCHEMA`，原文件保留 |
| `theme` | 严格小写 `light`、`dark`、`system`；`system` 由 Shell 跟随当前系统主题 |
| `wallpaper` | 严格小写 `warm`、`mist`、`night`；Shell 将其映射到原创内置壁纸 |
| `revision` | 正整数，每个实际变更递增；溢出失败，不回绕 |
| `updatedUtc` | 可解析的 UTC `DateTimeOffset`，偏移量必须为零 |

读取限制为 16 KiB、JSON 深度 8；拒绝重复/缺失/未知字段、未知值、错误类型和损坏 JSON。`Save` 首先验证固定选项，在独占锁内重新读取磁盘，再写同目录临时文件并刷盘，以 `File.Replace` 或首次 `File.Move` 原子提交。相同已保存选项幂等返回，不更改时间、修订或文件字节。并发宿主进程的提交串行化，最后一个成功提交生效；没有自动配置订阅，其他窗口需要重新 `Load`。

写入锁为 `.desktop-preferences.lock`。另一个写入者持锁时返回 `CONFIG_BUSY`，界面可提示稍后重试，不能无限循环。损坏或未来版本配置绝不被 `Save` 重置。未知的遗留暂存文件不自动提升、不删除；只清理本次调用创建但未提交的文件。取消在提交前检查；原子提交完成后按成功结果处理。

通过已有 `InstallationRoot` 检查安装目录、数据目录和所有受管目录，并拒绝配置文件、锁文件及暂存路径上的链接/重解析点和目录冲突。实际不可写返回 `DATA_ACCESS_DENIED`；不重定向到工作目录，不要求日常提权。错误与恢复沿用 Storage 结果，选项输入错误为 `CONFIG_INVALID_VALUE`，读入坏文件为 `CONFIG_CORRUPT`。

配置属于当前便携安装的系统外观，独立于游客/Logto 账号和游戏会话；返回桌面、退出账号或后台游戏不修改它。背景/挂起应用不通过此 API 获得设置写权限。当前仅实现内置主题及壁纸，完整 T02 的外观订阅、动态壁纸、图标/布局/Dock 设置、备份迁移等仍保留为后续阶段工作，不能据此称 T02 完成。

专用测试由 `DesktopPreferencesTests.Cases()` 提供：默认/保存/新服务读回、幂等、坏数据及未来版本保留、大小/字段校验、取消、竞争锁、遗留暂存、只读文件、目录冲突、工作目录隔离、修订溢出，以及 Windows ACL 与 Config/状态/锁 junction 拒绝。实际执行记录以当前构建测试报告为准；本文件不代替测试报告。
