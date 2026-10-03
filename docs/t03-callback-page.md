# 登录回调页

制作人统一来自 `BrandInfo.ProducerCredit`。自 T03 0.3.2 起，系统浏览器回环页使用 AutumnOS 品牌卡片、原创叶片图形、柔和渐变与短暂入场/漂浮动画。支持系统浅色/深色、高对比度、窄屏和减少动态效果；动画结束后静止，不显示假进度。

页面由 `CallbackResponsePage.Create(bool accepted)` 生成，嵌入 Identity 程序集；没有下载字体、图片、JavaScript 或外部资源。只有“请求已送达”与“请求未被接受”两个固定状态。回调已接收不等于登录成功，最终身份结果仍由宿主协议验证和账号页决定；服务端拒绝也不会在浏览器伪报成功。页面以 Alt+Tab 的真实系统操作提示用户切回应用，不放置无实际协议支持的“自动返回”或“自动关闭”按钮。

`LoopbackCallback` 保留固定地址、事务、state、方法、路径及一次消费校验。HTTP 200/400、no-store/no-cache、no-referrer、nosniff 不变；CSP 默认与脚本均拒绝，仅以准确 SHA-256 授权自身 CSS，并禁止 frame-ancestors、base-uri 和 form-action。CSS 换行标准化后再计算哈希。所有品牌字符串经 HTML 编码，生成器不接收 code、state、完整URL、错误描述、用户资料或令牌。

`CallbackResponseTests` 检查真实TCP响应的字节长度、CSP及参数不反射。浏览器视觉检查、动效与减少动态效果、最终EXE执行分别记在 AUTUMNOS_PROGRESS.md 与本轮构建目录，不把合成回调页面预览当作真实用户认证。
