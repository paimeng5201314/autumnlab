# 应用包与机器合同

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。`.autumn`是受限ZIP封装，根manifest.json声明schemaVersion=1、appId、name、version、runtime=web、entry和permissions。appId是小写分段身份，不能与已有不同来源同ID接管；SemVer决定版本顺序，发布时间和字符串排序不替代版本比较。entry必须是包内安全相对HTML资源，不是URL或外部EXE。

SDK/manifest.schema.json、store.schema.json、release.schema.json和message.schema.json均采用JSON Schema2020-12。store/release通过固定URN引用manifest定义，验证器必须注册相应Schema；合同工具已经这样加载。正例是交付四模板和Tests/contracts中的协议用例，负例包括多余身份字段、路径穿越、错误权限、source.zip附件和不合规版本。Schema不验证ZIP实际存在的资源、重复JSON键、Windows设备名、来源与三层一致、签名或跨字段存档范围；真实PackageInstaller/ReleaseMetadata检查不可省略。

包权限仅九种已支持值。permissionPurposes只能为已声明权限解释用途，desktop声明最多各四个快捷操作、link动作和widget，快捷操作指向自己声明的link。缺能力返回CAPABILITY_UNAVAILABLE，不能因在清单里写一个名字就获得实现。minReadableSaveFormatVersion≤saveFormatVersion≤maxReadableSaveFormatVersion，默认旧清单为1；这是安装兼容约束，不自动转换存档JSON。

开发工具只打包HTML/JS/CSS/JSON/PNG/JPG/SVG/WOFF2/WASM等受限内容，拒绝原生可执行魔数、隐藏私密文件、重解析点、越界、输出在源内部等危险输入。当前项目上限128文件、32目录、12级、8MiB单文件和16MiB总内容；商店附件另限20MiB。不要把开发者包、node_modules、凭据、用户数据混进应用目录。

清单检查通过不等于网络沙箱、真实身份和UI验收通过。打包后修改任何字节都要生成新材料并重测。下载、哈希校验和安装提交是三种状态，只有事务注册提交后才出现已安装应用。参见[应用发布](app-publishing.md)、[网络](network.md)、[数据](data.md)。
