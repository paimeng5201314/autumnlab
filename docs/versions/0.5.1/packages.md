# 应用包与校验 · 0.5.1

AutumnOS 0.5.1 的 `.autumn` 是受限制的 Web/WASM 资源归档，必须包含合法 `manifest.json` 与真实 HTML 入口。它不能承载普通原生 EXE、安装脚本或任意命令。`validate` 检查项目，`pack` 使用与安装器一致的包检查，`preview` 在原生确认后运行受控开发实例。源码 ZIP/TAR、改名后的 EXE 或任意 GitHub 附件不是应用包。

最小清单如下，应用 ID 应改为自己的稳定标识。该例不请求敏感权限：

```json
{
  "schemaVersion": 1,
  "appId": "cn.example.hello",
  "name": "我的第一个应用",
  "version": "0.1.0",
  "runtime": "web",
  "entry": "index.html",
  "permissions": []
}
```

准确格式见开发套件 `SDK/manifest.schema.json`；运行时还检查跨字段关系、重复字段、归档路径、原生文件伪装与入口存在性，Schema 接受不代表能够安装或运行。使用权限时同步声明用途；桌面扩展需额外声明固定动作、快捷项及纯文本小组件。权限扩大后重新检查和确认，不能依赖旧包授权。

项目工具只读取受支持的静态资源，不运行 npm、构建脚本或 Shell 命令。源文件单个最多 8 MiB、展开资源合计 16 MiB、128 个文件、32 个目录、12 级深度；链接/重解析点、路径穿越和敏感文件类型被拒绝。输出不能位于项目之内。失败时保留可诊断状态，不通过删除真实数据或放宽安全检查继续。

从 [第一个应用](first-app.md) 得到 `$cli`、`$project` 后可执行 `& $cli validate $project` 和 `& $cli pack $project <项目外输出目录>`。读取返回 JSON 中的 `packagePath`、`bytes`、`sha256`，后续始终使用实际产物。安装身份绑定包来源；不同来源即使 appId 相同也不能自然继承权限与数据。

发布需要 `autumn.store.json`、`autumn.release.json` 与逐字节相符的 `.autumn`。版本、入口、权限、摘要、大小和存档格式必须一致；同版本不能偷偷替换字节。生成与校验步骤见 [应用发布](app-publishing.md)，数据兼容见 [数据](data.md)。
