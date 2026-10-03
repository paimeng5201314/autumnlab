# ADR 0001：本地原生工程基础

状态：T00 实施，完整运行环境与发行门禁待后续阶段。制作人：派蒙。

采用 C# / .NET 10、WinUI 3 和 WebView2；不更换用户确定的技术路线。最小窗口使用 XAML 和原生控件，第三方 Web/WASM 应用只能由后续 Runtime 在主窗口内部承载。

使用 unpackaged、自包含 .NET 与 Windows App SDK 的 win-x64 目录。单个 EXE 不是完整产品，运行目录必须一起保留。目标 Windows 10 22H2 和 Windows 11；当前真实验证环境仅 Windows 11 26200。兼容性声明不会替代 Windows 10 或干净系统验收。

工具链位于项目 .tools，使用用户已授权的微软 SDK ZIP（固定版本和 SHA-512）。Windows SDK BuildTools 由锁定 NuGet 包提供，本阶段实测无需修改系统 PATH 或安装完整 Visual Studio。发行版 WebView2 缺失/离线部署策略仍是 T05/T06 验收项。

数据根固定在实际 AutumnOS.exe 旁，AppContext.BaseDirectory 不受快捷方式工作目录影响。只读目录返回可恢复错误，不偷偷切换到用户目录。基础初始化 Completed 只表示 T00 数据准备，不应被 T02 当作完整首设完成。

Logto 公开配置从规范文件直接作为 Content 打包。登记状态文件输出名为 config/registration.json，避免 Windows PRI 将原文件名中的 registration-status 当作资源限定符；源文件名称保持不变。配置校验、发现文档可达、Native 登记、浏览器登录是独立证据。

T00 的 AppInstance.BlocksMaintenance 只冻结活跃游戏判断。真正维护租约、实例启动竞争、安装/存档关键操作及跨进程交接尚未实现，不得据此启用安全更新。

没有修改远端仓库、生产账号后台或发布配置。正式签名私钥永不进入客户端，发行门禁保持未执行状态。
