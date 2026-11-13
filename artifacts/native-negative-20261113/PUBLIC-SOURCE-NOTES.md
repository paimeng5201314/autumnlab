# 公开原生验收源码补充

制作人：派蒙。本目录所列C#/项目/锁、PowerShell生成器和纯网页fixture是可审阅的自有测试源码，不包含Windows运行时、EXE/DLL、bin/obj、用户Data、凭据或私钥。public-source-paths-v2.json逐个列文件，不采用整个artifacts目录。

qa02/03/04/05-harness分别固定各次历史BuildId、source/table SHA和准备目录。它们依赖对应完整冻结产品DLL及NativeHost/WebView2文件表；公开源码补充不含这些二进制。缺少历史输入时应拒绝执行，不能拿当前或其他版本DLL替代历史输入。Prepare-Qa03/04/05Harness.ps1保留从已审阅qa02模板生成相应新身份的步骤；正常和负例必须用新report，不继承旧分数。代码无参数入口inert，受限case不接受任意PID/EXE。

qa04-cpu-helper包括完整4项源码输入：Directory.Build.props、CpuLoad.Helper.csproj、packages.lock.json和Program.cs。CPU driver包含CpuLoadOwner.cs、主Program、项目/props/锁及cpu-helper-inputs.json。后者是公开的固定4文件名/大小/SHA输入清单，不是二进制或秘密；CpuLoadOwner内ManifestHash精确绑定该清单。

CPU实验的完整构建顺序是：先按锁编译helper，再从它的固定exe/dll/deps/runtimeconfig四文件生成输入清单；复制driver到新的独立artifacts工作目录，更新该副本的ManifestHash并编译，再将同一四文件与清单拷至新driver输出。不得覆盖历史源码/证据，不得将重编译后的hash转记给旧实验。跨机器或不同构建路径若产生不同helper/driver字节，必须记录新hash，重新审阅/执行；不能宣称旧CPU成绩已复现。公共源码不自动执行worker、owner、LSA/USER或压力实验，磁盘压力方案未实现/未运行。

tests/AutumnOS.OwnedJob.Probe是自有kernel子进程/原Job回收检查，只有其源码/锁包含于补充，不含产物；不能冒充完整浏览器验收。tests/fixtures/native-resource-repeat是纯合成网页fixture；artifacts/response-stream-20261112/fixture-check.cjs只验证显式DOM/VM控制流，不能当作真实浏览器Serve计数。

PUBLIC-NATIVE-RESULTS.json是本轮脱敏精简汇总，显式记录每项来源路径与SHA；来源原始报告不在此公开补充白名单内。它省略PID、原创建FILETIME、账号、nonce、profile/实际用户路径及原始进程日志。qa03 normal-close失败保留为未解释；qa04/qa05通过和有界CPU未复现不代表修复根因或完整隔离。
