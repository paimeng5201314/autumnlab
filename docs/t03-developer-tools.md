# T03 开发者工具与本地项目工作流

Lab Chronicles AutumnOS · 制作人：派蒙。入口：设置 → 开发者诊断 → 开发者模式；启用后桌面出现开发者应用。开关使用独立 `Config/developer-mode.json` 的版本化原子存储，保留既有桌面与首启配置。读取失败保持关闭并保留原配置，不伪装恢复成功。

关闭模式会取消正在进行的选择器、打包、检查和预览工作，结束独立开发预览实例，撤销其绑定能力、清理最多 200 条调试记录；普通元素配对与用户存档不被删除。当前没有调试网络监听器或任意 Shell 执行入口。× 关闭沿用原窗口语义。

## 可运行入口

1. 选择项目文件夹：系统 FolderPicker，项目根含 `manifest.json`；源目录只读。不选择 AutumnOS_Data、含 .env 等隐藏文件、任意 EXE 或普通用户数据目录。
2. 校验并打包：原生 `DeveloperToolsService.BuildProject` 读取有限 Web/WASM 资源，生成新 .autumn，再使用实际 `PackageInstaller.Inspect` 做清单、权限、入口、归档、原生文件伪装、大小及路径检查。输出到当前 EXE 的 `AutumnOS_Data/Packages/DeveloperOutputs/`，每次唯一文件名，旧包保留。最终提交前再次检查开发模式未撤销。
3. 选择并检查 .autumn：系统文件选择器 → 实际包解析器 → 显示真实 appId、版本、权限、资源大小和 SHA-256。不会安装/执行未通过检查的内容。
4. 内部预览：检查过的包进入单独 WebAppHost 与 `local-preview:实际包hash` 来源绑定；不会通过伪造内置 appId 继承元素配对授权。预览使用实际运行时与权限弹窗，不假装任意第三方完整沙箱已验收。返回桌面不等于结束普通游戏；关闭开发模式只关闭预览。
5. 发布资料校验：选择与当前 .autumn 配对的 autumn.release.json，核对身份、版本、入口、权限、附件名、实际字节大小与 SHA-256。仅本地检查，无 GitHub 写入/Release。分类和商店发现仍留给 T04。
6. 隔离生命周期/权限检查：实际 RuntimeSession 执行前台、后台、挂起、恢复、拒绝存档授权、错误来源与关闭状态检查。UI 显式显示合成应用与游客测试命名空间，不混入正常 Logto 账号；不代表物理输入、所有网络通道或完整生命周期验收。
7. SDK 记录：只保留有限已知方法名和有限固定结果码，无 payload、参数、用户身份、授权码或 token；未知字符串变为 unknown-method/OTHER_RESULT。可刷新、清空；关闭模式后停止记录。

先运行 `./scripts/New-DeveloperSample.ps1 -Destination artifacts/developer-projects/自己的新项目目录`，再在开发者工具选择它。这个本地脚本把实际样例与 `sdk/autumn-sdk.js` 一起复制到新目录，拒绝覆盖已有项目。源码 `samples/element-pairs` 的 SDK 由正常构建脚本加入包，直接只选该源码目录会漏掉共享 SDK 文件；不要把这种不完整项目当成成功 SDK 预览。

项目工具不运行 npm、dotnet、脚本或任意命令；它只打包适配的静态 HTML/JS/CSS/JSON、PNG/JPG/SVG/WOFF2/WASM。单文件 8 MiB、总内容 16 MiB、128 文件、32 目录、12 级深度上限；源/目标 reparse points 拒绝。输出目录不能位于输入项目内。开发者须确保项目资源自身不包含私密内容；工具不声称能从任意 JS 字符串识别所有秘密。

## C# 宿主接口

| 接口 | 输入输出 / 约束 |
|---|---|
| SetEnabled(bool) / CapabilityToken | 真实能力代次与取消；关闭立即撤销，重新开启使用新 token |
| BuildProject(projectDir, outputDir, ct) | 系统选择的绝对目录 → DeveloperBuild（实际路径及 PackageInspection）；不覆盖旧输出；取消/失败只清理本次暂存文件 |
| InspectPackage(path, ct) | 绝对 .autumn 路径 → 与安装器一致的验证结果；不执行应用 |
| ValidateRelease(metadata, package, ct) | 两个本地绝对路径 → appId/version/channel/hash；64 KiB JSON 上限、严格字段、内容必须匹配真实包 |
| Record(method, resultCode) / GetTrace / ClearTrace | 宿主网关绑定的调用记录；200 条内存环，关闭清除；不接收应用自报身份和任意消息 |

以上入口均只对宿主可用，不添加到第三方 SDK。Shell 包住关键写操作协调器，避免未来维护动作与打包同时提交；这不是 T05 维护锁或更新完成。最长 UI 操作 5 分钟，允许取消；权限验证/预览使用已有 Runtime 限额。失败返回有限 PackageException.Code，不把文件内容或令牌写入日志。

## 发布资料合同 v1

当前工具读取以下精确字段（必填、不可重复、未知字段拒绝）：`schemaVersion=1, appId, version, channel, runtime, minHostVersion, minSdkVersion, entry, asset, bytes, sha256, permissions, saveFormatVersion`。

`channel` 为应用 stable/preview，不借用主程序 plus/meta 更新渠道；最低宿主/SDK 使用版本字符串，saveFormatVersion 为大于零的存档格式号。asset 是真实本地包文件名，bytes 与 sha256 来自真实字节，permissions 与包清单完全一致。最低版本这里只做格式与资料一致性检查，不能宣称 T04 的运行环境兼容性筛选已完成。

正例：原项目真实打包后，发布资料填写同一包的身份/版本/hash，验证通过。反例：改名后仍使用旧 asset、替换包字节、缩减权限、用另一应用的清单、放入 EXE 或 MZ 伪装 JS，均拒绝。

默认没有模拟账号功能；身份 fixture 只出现在显式自动测试和隔离诊断。工具退出不删除项目、普通应用、已有开发输出或存档。Windows 实测、真实截图和开发模式重启恢复证据记录在本轮验收报告，源代码与单元测试不冒充 UI 验收。
