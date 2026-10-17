# 核心开发与本地工具

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。当前C#基线为固定 .NET SDK10.0.401、net10.0、win-x64、WinUI3和WebView2；锁文件是恢复依据。客户端随包包含.NET10.0.12、Windows App SDK及固定WebView2 Runtime154.0.4258.53，实际版本仍应读取最终deps/runtimeconfig和物料记录。构建成功不证明Windows10/干净环境已通过。

普通应用开发使用已交付自包含 `AutumnOS.Developer.Cli.exe`，不需要先安装.NET SDK。仅当重建BackendIdentity源码或宿主工程时，才使用显式路径指向10.0.401 SDK。`BackendIdentity/Build-Sources.ps1`把已交付源码复制到一个全新隔离目录，建立局部缓存并执行locked restore/build，不在产品目录生成obj/bin，不安装全局组件。

构建脚本在调用SDK前显式设置 `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`、`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，并退出遥测。独立后端脚本结束时恢复调用进程原环境，不登记临时工具路径、不自动生成或信任开发证书。该设置依据[微软的 .NET CLI 环境变量说明](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables)；本机复现仍需比较运行前后用户PATH与证书元数据，不能仅凭设置宣称环境无变化。

TypeScript/Schema/OpenAPI检查需要Node.js >=22.9，工具固定TypeScript5.9.3、Ajv8.17.1、ajv-formats3.0.1、swagger-parser12.1.0。从Developer目录运行：

~~~powershell
& ./Tools/contract-checks/restore-tools.ps1 -NodePath 'C:/你的已安装Node/node.exe'
& 'C:/你的已安装Node/node.exe' ./Tools/contract-checks/contract-check.cjs --kit-root . --report ./work/contracts-01.json
~~~

路径应替换成实际已安装工具；脚本不会代替用户安装Node。恢复仅写Developer下 `.tools/`，固定npm11.6.1官方SHA512、完整package-lock、忽略生命周期脚本，不修改PATH。没有联网缓存时恢复是环境前置，不把未运行检查写通过。

工程集成统一管理解决方案、版本、公共Contracts、依赖和构建脚本。模块只改自己拥有的目录。当前不用Git时，以真实源文件SHA256快照、BuildId和最终文件表关联报告；测试使用隔离目录，保留所有失败。身份令牌、私钥、用户存档和缓存不进源码快照或ZIP。合同检查与实际原生运行是不同证据，参见[协作](collaboration.md)。
