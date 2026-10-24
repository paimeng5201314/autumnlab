# 普通本地来源配对验证夹具

制作人：派蒙。此目录从当前 `samples/element-pairs` 派生，仅用于独立测试数据。

`game.js`、`game.css` 和 `autumn-sdk.js` 保留原实现字节；HTML 改标题并移除不在本轮范围的 T03 扩展面板。新清单 ID 为 `cn.labchronicles.normallocal`，版本 `1.0.0`，仅声明 `saves`。原工程许可证继续适用，没有重新授权。

驱动将上述五项资源打包到新建隔离测试目录，然后通过普通商店的“安装 .autumn”与真实系统文件选择器安装。禁止注册表、配置预注入和开发模式。此来源应由实际安装器登记为 `local:cn.labchronicles.normallocal`，与 ChemLeap 的 `github:1414175562` 分开。

草案入口：`artifacts/isolation-20261018/drafts/Test-NormalLocalApp.ps1`。草案尚未运行 GUI；解析和包检查不能替代真实验证。
