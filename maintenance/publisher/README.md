# AutumnOS 1.0.0 原位签名修订

维护目标是公开仓库 **paimeng5201314/autumnOS**（1387171708）已有的 Release **422539615**，标签 **plus-v1.0.0-20261115**，标题 **autumn正式版v1.0.0**，产品版本 **1.0.0**。工具每次从公开 API 复核发行页和原标签指向的提交 e9abaa5d141e4b52f95d971cb08955e8910e5720。远端修改限于原发行页附件。

同样显示为 1.0.0 的修订以序列号区分。发布时必须提供已安装基线的真实 build ID 和正序列号。工具核对基线构建收据与生产信任根、目标冻结源、九组测试、目标程序集内的修订号、完整 EXE 和 Prepare 生成的载荷。目标 build ID 必须不同，目标序列必须严格升高。签名使用本机 Windows 用户的生产 v3 身份；工具另用归档的 v2 公钥对本次清单进行拒绝校验。使用旧 v2 的现存安装需要先退出并手动换装完整入口，保留原有 AutumnOS_Data。

## 准备备份与输入

PowerShell 7.6 或更高版本。先准备原 1.0.0 的真实 EXE 和原更新 ZIP。下列命令匿名核对原资产 ID、大小与哈希，在指定位置保留只读备份，并在 originals.json 中记录校验。相同卷使用硬链接。

~~~powershell
.\Backup-OriginalReleaseAssets.ps1 -BackupDirectory $backupDir -OriginalExecutablePath $oldExe -OriginalPayloadPath $oldZip
~~~

使用目标构建的完整冻结工作区，保留官方构建、测试、单文件和准备报告。默认从同一工作区读取基线 build-result.json；若基线收据在另一目录，传入 -PreviousBuildReceipt 的绝对路径，并在旁保留配套的 source-snapshot.json。若单文件保存在外部磁盘，加 -ExecutablePath，必须与 package-single-file.json 中的物理路径和实际 SHA-256 匹配。

~~~powershell
$inputs = @{
    Workspace        = $frozenWorkspace
    BuildId          = $targetBuildId
    PreviousBuildId  = $installedBuildId
    PreviousSequence = $installedSequence
    PreviousVersion  = '1.0.0'
    PrivateDirectory = $localSigner
}
.\Publish-SignedUpdate.ps1 @inputs -CheckLocal
~~~

## 用 GitHub CLI 发布

官方 gh.exe 需以账号 paimeng5201314 登录。使用便携 CLI 时传入 -GhPath 的绝对路径。所有资产上传源均来自冻结收据和受测 CLI 的输出。命令参数只包含本地签名目录位置。

~~~powershell
.\Publish-SignedUpdate.ps1 @inputs -BackupDirectory $backupDir
~~~

工具使用以内容哈希命名的待切换文件。它核对远端 ZIP 的真实 asset ID、长度和摘要，用这个 ID 和正式的文件名在本机重新绑定清单、签名和验签。原始草稿不属于上传内容。所有候选验证完成后，依次切换载荷、EXE、签名、清单的正式名称，最后从匿名下载地址完整回读四件实际字节。清单最后就位，部分切换时客户端可能暂时提示缺件或验签失败。GitHub 的附件改名保留 asset ID。

切换中断后，用原参数和原工作区重新执行。工具会根据原资产快照尝试恢复正式名称；网络中断时保留恢复记录和已上传的待切换附件。收到 GitHub 502 而留下零字节 starter 占位时，仅本次有匹配上传记录的同名空占位可被清理。完整发布后的重跑只验证已有目标。

## 用 Edge 上传、本地签名

CLI 尚未登录时，下面的维护命令只执行本地工作与匿名 GET。浏览器中的附件写入限于原发行页。完整 ZIP 要使用与冻结报告哈希相同的待上传文件。

1. 运行下列命令。browser_files 返回两个真文件的路径、待传名称、正式名称和 SHA-256。将这两个文件添加到原发行页，保留文件名和已有附件。在 GitHub 上保存这次附件添加，待匿名 API 可以读取新的 ZIP。
   
   ~~~powershell
   $initial = .\Publish-SignedUpdate.ps1 @inputs -CheckLocal -PrepareBrowserFiles
   $initial.browser_files
   ~~~
2. 执行只读绑定和本地签名。工具从已有发行页查找哈希匹配的 ZIP，独立从资产 API 核对其 ID。也可明确传入 -BrowserPayloadId。输出的两个元数据文件已经绑定真实 ID 并通过正式签名校验。
   
   ~~~powershell
   $signed = .\Publish-SignedUpdate.ps1 @inputs -BackupDirectory $backupDir -PrepareAfterBrowserUpload
   $signed.browser_files
   ~~~
3. 将这两个中性命名的元数据文件添加至原发行页。GitHub 编辑页若支持修改附件文件名，按顺序把 ZIP、EXE、签名、清单晋升为下表中的正式文件名；只改显示标签不能改变文件名。遇到同名旧文件，应先按 previous-原资产ID-原文件名 保留它，再按已验证的 asset ID 修改新附件名称。不能用删除重传代替改名，重新上传会改变清单绑定的 ZIP ID。若网页无法修改文件名，用已登录的官方 gh.exe 运行上一节的发布命令继续。
4. 公开四件全部就位后，运行下列命令，验证目标哈希、发布前记录的其他历史附件、资产 ID、原标签、标题与真实下载内容。它将验证结果设为 published_anonymously_verified，并把收据写入目标构建的 artifacts/builds 子目录。
   
   ~~~powershell
   .\Publish-SignedUpdate.ps1 @inputs -BackupDirectory $backupDir -VerifyAfterBrowserPublish
   ~~~

| 正式文件名 | 内容 |
| --- | --- |
| AutumnOS.exe | 目标 1.0.0 修订的完整入口 |
| AutumnOS-1.0.0-win-x64-payload.zip | 目标实际程序文件 |
| autumn.update.sig | 正式 RSA-PSS 签名 |
| autumn.update.json | 绑定该发行页与新载荷 ID 的清单 |

## 原资产保留

EXE 763938577 和 ZIP 764006099 的回退字节均通过固定 SHA-256 核验，其既有 ID 保留在原发行页，便于回退。其他历史附件按发布前记录检查。Retire-OriginalReleaseAssets.ps1 默认只读；若仍有公开历史 JSON 引用任一原 ID，会返回 retained_referenced_originals。该分支即使传入 -Apply 也保留两件。原 publication.json 明确记录了这两个 ID。

只有在所有引用已经消失、四件公开读回报告完备、上一修订的真实客户端已发现并暂存本次修订、当前修订客户端确认已是最新之后，才可单独指定 -Apply 退休上述两个旧资产。收尾模式还会复核本地备份、全部其他历史附件与原发行页身份。

~~~powershell
.\Retire-OriginalReleaseAssets.ps1 -BackupDirectory $backupDir -SlotJournal $journal -PublicationReceipt $publicReport -PreviousClientReceipt $previousClientReport -CurrentClientReceipt $currentClientReport
~~~

## 可重复演练

~~~powershell
.\Test-PublishSignedUpdate.ps1
~~~

演练使用内存 GitHub 和本地测试数据。真实签名、公开客户端下载及修订安装各自以实际运行报告为准。
