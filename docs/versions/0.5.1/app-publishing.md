# 应用发布材料与安装

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。本页命令只生成/校验本地材料，不上传、不创建仓库或Release，不修改Topics/Tag。实际远端发布必须另获制作人授权。

先按[入门](first-app.md)创建、校验并pack，使用真实返回的packagePath。从Developer目录：

~~~powershell
$cli = Join-Path (Get-Location) 'AutumnOS.Developer.Cli.exe'
$package = '填入pack实际返回的packagePath'
& $cli generate-release $package ./work/publication-01 --developer '开发者名字' --description '应用的实际功能' --category sq --offline true --min-host 0.5.1 --min-sdk 0.3.0
~~~

输出新目录含autumn.store.json、autumn.release.json和实际 `.autumn`逐字节副本。将真实输出路径用于：

~~~powershell
& $cli validate-publication ./work/publication-01/autumn.store.json ./work/publication-01/autumn.release.json '该目录的实际包路径'
~~~

真实共享引擎核对appId、版本、入口、权限、存档格式、大小、SHA256、附件名及三层元数据；未知/重复字段、跨应用清单、错误摘要或EXE伪装拒绝。输出已存在时保留旧材料，不能覆盖同版本内容后仍引用旧摘要。stable/preview来自应用SemVer预发布部分，与主程序plus/meta不同。

未来授权发布时，每仓库一应用：默认分支根autumn.store.json，独立版本Release含autumn.release.json与其asset指向的.autumn；自动生成source ZIP/TAR不能安装。仓库标签autumnos-app，分类autumn-app-sq或autumn-app-pm；PM自标未核验，冲突显示冲突，不增加权限。首次确认来源绑定真实repositoryId，同名仓库不等于同来源。

开发者预览不进入商店安装注册。真实安装还需适用环境、兼容版本、来源确认和安装事务完成；应用运行中不可替换资源，固定版本偏好保留，降级需检查所有相关存档格式并备份，卸载默认保留存档。完整沙箱未证实仍阻止公开开放任意社区应用。受控本地测试源必须隔离并在关闭开发模式后撤销，不算公网分发成功。参见[应用包](packages.md)、[网络](network.md)。
