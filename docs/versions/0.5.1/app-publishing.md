# 应用发布材料 · 0.5.1

AutumnOS 0.5.1 开发 CLI 可本地生成和校验发布材料，不上传 GitHub、不改仓库 Topics、不登录账号或创建 Release。先按 [第一个应用](first-app.md) 获得 `$cli` 和真实 `$packed.packagePath`。只有用户另外决定发布时才将检查过的材料交到对应仓库；运行本地命令不构成公开发布。

```powershell
$publication = Join-Path $workRoot ('publication-' + [Guid]::NewGuid().ToString('N'))
$release = & $cli generate-release $packed.packagePath $publication --developer '你的开发者名称' --description '应用的实际用途' --category sq --offline true --min-host 0.5.1 --min-sdk 0.3.0 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.status -ne 'passed') { throw '发布材料生成失败。' }
& $cli validate-publication $release.storePath $release.releasePath $release.packagePath
if ($LASTEXITCODE -ne 0) { throw '发布材料校验失败。' }
```

上述 `--offline true` 仅适合实际可离线工作的应用；需要联网时改为 false。最低宿主和 SDK 必须与所用能力一致。输出目录必须不存在；工具不覆盖旧发布目录。返回的 packagePath 是生成目录中的真实包副本，不再依赖猜测的文件名。

默认分支根放 `autumn.store.json`，包内保留 `manifest.json`；单独版本 Release 同时包含 `autumn.release.json` 和其 asset 字段指定的 `.autumn`。每仓库一应用，Topic 为 `autumnos-app`，分类可选 `autumn-app-sq` 或 `autumn-app-pm`。PM 是仓库自标，未核验，不额外授予权限或跳过运行门禁；分类冲突应修正元数据。

生成器从真实字节提取 appId、version、entry、permissions、saveFormatVersion、bytes 与 sha256，并用共享解析器复核。修改任何包内容后重新生成新版本；不能沿用旧摘要，不能把 GitHub 自动源码 ZIP/TAR 当作安装包。发布校验输出 `publishing:not_performed`、`liveGitHubRegistration:not_verified` 只证明本地材料一致，不证明仓库存在、索引完成或应用可运行。

验收至少覆盖真实包成功、错误摘要、另一 appId、未知/重复字段、普通 EXE、路径穿越及输出目录冲突。安装升级还需检查数据兼容、权限扩大和活动实例占用。网络运行限制仍见 [网络边界](network.md)，不能把“生成发布材料成功”写成“第三方运行隔离通过”。
