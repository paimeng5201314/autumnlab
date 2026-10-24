# F / D 盘清理与签名密钥迁移结果

2026-10-24。派蒙明确授权：签名密钥迁到D盘，清理F盘测试残留；随后授权清理D盘旧测试和构建，保留当前交付包与报告。旧startup-02由派蒙正常退出，没有强杀用户程序。

## 完成结果

| 操作 | 实际结果 |
|---|---|
| F盘测试与缓存清理 | 删除1559500592字节文件，约1.45GiB；包括旧T05测试安装877文件、4个本轮NuGet元数据缓存，以及空测试目录 |
| 签名密钥迁移 | 两文件共2906字节迁到D盘；复制字节一致，实际DPAPI解密及同一公钥匹配通过后删除F盘原件 |
| D盘旧构建与测试输出清理 | 删除217518文件，共189902731821字节，约176.86GiB；67715个已核对的文件/目录目标完成 |
| 测试入口清理 | 删除两个负面测试junction链接本身，没有遍历其目标；删除三个旧测试快捷方式 |
| 当前交付保护 | EXE、ZIP、解压EXE、源码ZIP和原根入口SHA-256均匹配清理前记录 |
| 数据保护 | 所有65个已识别AutumnOS_Data根原位保留；工程根真实AutumnOS_Data也不在清理范围内 |

字节数是删除文件的逻辑大小；不是对同时运行的其他软件或文件系统压缩后的空闲空间变化作全盘归因。

## 当前密钥位置

`<PRIVATE_SIGNING_DIRECTORY>`

其中 `signing-key.dpapi` 为受保护私钥、`public-trust.json` 为对应公钥元数据。目录在工程之外，创建时已附加仅当前Windows用户访问的ACL；迁移没有生成新身份，没有输出明文私钥，也没有改变客户端生产公钥。仍绑定原Windows账号的DPAPI，位置变化不等于跨机器/重装后可直接恢复。

[实际迁移记录](<WORKSPACE>/artifacts/update-fix-20261024/signing-key-moved-to-D.json)

## 保留范围与后续入口

- 当前交付：`artifacts/code-handoff/T06-20261024-update-04/`，含EXE、ZIP、解压副本、文档和报告。
- 当前builds/packages/single-file/update-preparation中的update-04及源码ZIP保留，方便复核实际交付与更新载荷。
- 新快捷方式：[AutumnOS-更新修复-20261024.lnk](<WORKSPACE>/AutumnOS-更新修复-20261024.lnk)。旧代码核验、启动诊断、startup-02快捷方式已删除。
- 原根AutumnOS.exe、全部工程源码、SDK/依赖、开发者项目、唯一探针源码、当前手工检测依赖和当前报告引用的少量失败证据保留。
- 旧交付目录中的AutumnOS_Data仍在原路径；保守保护可能含存档的目录，没有把它们仅凭位于artifacts就认定为垃圾。没有合并、搬迁或清空存档，也没有读取账号/令牌内容。
- 用户授权后删除了大量历史报告及生成物。AUTUMNOS_PROGRESS中的旧路径是历史记录，部分已不再存在；不应据此宣称历史失败转为通过。当前GUI/第三方隔离验收状态没有改变。
- F盘原有共享NuGet缓存、Codex运行时与日志、Windows组件和用户截图没有作为项目测试残留删除。

## 核验记录

- [最终核验](<WORKSPACE>/artifacts/update-fix-20261024/cleanup-final-verification.json)
- [D盘实际删除记录](<WORKSPACE>/artifacts/update-fix-20261024/d-cleanup-result-04.json) 与同目录 `d-cleanup-plan-04.json`
- [F盘逐项说明](<WORKSPACE>/artifacts/update-fix-20261024/F-DRIVE-CHANGES.md)

所有删除限定在核实的测试根或工程artifacts内，排除了源码、当前交付和数据保护路径。删除前核对完整文件树、重解析点、文件数/字节数及运行进程；期间发现新数据目录或活动产物进程会停止。当前正式执行完整结束。目录检查初版因PowerShell元数据读取慢被取消，未执行删除；最终采用等价的.NET元数据检查生成清单并完成实际操作。

没有Git、远端上传/发布、UAC、全局安全策略修改或强杀用户程序。本轮只进行了清理、迁移和静态产物核验，没有新增GUI验收。
