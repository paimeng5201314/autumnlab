# 宿主交付与发布门禁 · 0.5.1

本页描述 AutumnOS 0.5.1 的源码构建与交付检查，不宣称正式发布已完成。普通目录版保留整套运行资源，单文件版把这些资源及 Developer 打入外层 `AutumnOS.exe` 并首次展开；两种布局见 [产品](product.md)。修改源文件或内置文档后，既有交付 EXE 不会自动更新，必须以新构建 ID 重建、打包并验证。

在完整 Windows 源码工作区，先完成 [核心开发](core-development.md) 的锁定恢复与成功构建，再执行：

```powershell
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Test.ps1
./scripts/Package-SingleFile.ps1 -BuildId $buildId
$smokeReport = Join-Path $PWD ('artifacts/single-file-check-' + [Guid]::NewGuid().ToString('N'))
./scripts/Test-SingleFileSmoke.ps1 -BuildId $buildId -ReportDirectory $smokeReport
```

`Package-T05` 的 Prepare 读取最新匹配构建，`Package-SingleFile` 要求该构建已有匹配的准备产物；不要混合不同快照的 DLL 与报告。开发套件打包脚本会复制这 17 篇版本文档到 `Developer/Docs/0.5.1`。单文件准备结果不是完整升级、系统兼容或正式发布批准。

网络更新具有 RSA-PSS 验签、固定源、反回滚序列、维护租约、独立更新器、journal 和健康恢复，但生产信任根仍未配置，正式自动安装保持禁用。生产签名材料必须由有权持有人配置，不能接受未签名更新或将测试密钥当作生产根。EXE 摘要与嵌入完整性校验均不能替代 Authenticode 签名。

新单文件布局的 A→B 更新、健康失败回退、事务崩溃后重启恢复要在独立测试信任构建中重新实测。覆盖 Starting/Foreground/Background/Suspended/Closing 实例、存档最终提交、安装并发、已更新种子重启、磁盘满、忙文件及兼容数据保护；旧布局成绩不能转移到新 EXE。

正式收尾还包括 Windows 10、干净系统无 SDK、离线首启、多屏/DPI、输入法和触屏、外部新人、真实账号/独立后端、完整社区隔离、第三方许可及制作人审阅。安装包是否交付与 Setup 源码存在是两件事；当前安装包仍暂缓。对未执行项使用 not_run，对缺信任/登记使用 blocked，不能用一个总的“完成”掩盖它们。记录方式见 [协作与证据](collaboration.md)。
