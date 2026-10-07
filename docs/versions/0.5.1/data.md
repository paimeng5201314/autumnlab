# 数据、预算与恢复 · 0.5.1

AutumnOS 0.5.1 把用户数据保存在入口 EXE 旁的 `AutumnOS_Data`。单文件版的内层程序位于 Data/System，但用户数据根仍对应外层入口。应用不能传路径或账号 ID 选择任意数据；宿主根据真实账号、安装来源、应用与实例绑定作用域。游客和不同账号保持隔离，登录不会静默迁入游客存档，卸载应用也不等于删除用户数据。

`saves.write` 写 JSON，`saves.read` 返回 `{exists,value}`；不存在时 value 为 null。应用内容 `formatVersion` 与宿主记录 schema 不同，增加版本号不会自动迁移业务结构。迁移应先校验旧内容、在内存转换、保存前验证新格式并保留恢复路径；未来不支持的格式不得按默认值覆盖。

`storage.write/read/delete` 读写严格 base64 的私有数据，`preferences.set/get` 保存 JSON 偏好。本次修订把偏好迁入独立目录，使同名 key 或 `pref_` 开头的原始存储 key 不再覆盖偏好。旧前缀记录一次性迁移时保留原字节；损坏数据返回 `PREFERENCE_CORRUPT`，迁移冲突返回 `PREFERENCE_MIGRATION_CONFLICT`，不能自动清空。偏好逻辑 key 的公开上限仍为 59，storage 为 64；别仅修改文档或 TypeScript 放宽限制。

内部配额包括每文件 1 MiB、每账号应用 16 MiB、128 个私有文件、32 个存档槽位、每存档 128 KiB；SDK 完整请求/响应却只有 32768 UTF-8 字节。内部配额不是 SDK 可传输额度。本次修订在 saves.write 与 preferences.set 提交前预检完整未来读回信封，包含字符串转义、最坏 64 字符 requestId 和 JSON 包装；超过预算返回 `RESPONSE_TOO_LARGE`，旧提交保留。storage.write 的 base64 解码后限 20 KiB，与读取一致，超限返回 `FILE_TOO_LARGE`。中文、emoji、控制字符和嵌套 JSON 都需要回归，不能隐藏错误或返回截断内容。

桥接响应使用适合 UTF-8 数据传输的 JSON 编码，通过 `PostWebMessageAsJson` 发送；这一选择只用于消息通道，不授权把序列化结果直接拼进 HTML 或可执行 JavaScript。磁盘记录格式与实际读取预算分别验证。现有大记录如果超过读取合同应明确报错，不能用后台自动改写或截断迁移损失数据。

超时、取消或网络中断后先读取实际状态；请求重试不能代替业务幂等。授权撤销、账号变化、应用关闭或维护开始时，最终提交需重新核对持有的作用域与租约。文件选择器仅返回有期限的不透明 handle；SDK 读文件适配限 20 KiB，不是系统任意文件访问。

设置中的“本地数据恢复”可检查受支持配置和当前账号内置示例存档的备份。恢复需明确确认，保留 `.bak` 与恢复前记录；已有恢复记录冲突时停止。坏文件、磁盘满、只读或未来 schema 都应保留原数据。当前尚无完整安装缓存/旧 System 自动回收策略，不应按后缀或目录年龄手工批量删除 Data。排查步骤见 [排错](troubleshooting.md)。
