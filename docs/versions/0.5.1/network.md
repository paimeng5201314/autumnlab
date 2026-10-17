# 网络能力与来源限制

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。platform.getCapabilities明确返回networkSandboxVerified=false。当前公开SDK没有network.request、network.getStatus、任意外部URI或主机命令能力，未知方法返回CAPABILITY_UNAVAILABLE。不得只看某条SDK请求拒绝就宣称WebView所有网络通道隔离通过。

完整边界包括fetch、图片、脚本、iframe、WebSocket、WebRTC、Service Worker、下载、新窗口、跳转和host messages，以及账号切换后的浏览器持久状态。没有完整当前版本证据前，不能借PM/SQ分类、用户警告或开发预览绕过安全阻塞开放任意社区包。模板均不需要网络、不加载远程头像、不使用浏览器localStorage替代宿主存档。

商店和主程序更新由原生可信服务进行GitHub只读访问，与应用网络权限不同。商店查询topic:autumnos-app，支持分页、缓存、限流与不完整结果说明；无合规Release就如实空列表，不自动塞测试商品。主程序源固定paimeng5201314/autumnlab，plus/meta严格分离，版本比较不按发布时间。GitHub加速只处理允许的公开API/附件，不承载Logto请求或令牌；代理失败回退也仍需校验payload和签名。

独立后端示例只在固定127.0.0.1端口监听；可信发现/JWKS使用HTTPS且验证同源。这个受控本机接口不是赋予内部应用访问任意内网的通行证。SDK资料也不能冒充网络身份。离线样例能否运行应在完整依赖包及隔离环境实际测试；manifest offlineCapable只是开发者声明，不是宿主保证。

证据应分别标真实公网、受控本地服务器、注入断网、缓存命中与未运行。参见[应用发布](app-publishing.md)、[主程序发布](host-publishing.md)和[服务端](server.md)。

开发工具offline/online仅切换当前独立预览测试状态，networkScope固定为preview_only_external_network_already_blocked。固定测试账号可获得isCached=true资料；不会断开操作系统网卡、影响商店/更新/真实Logto，也不赋予SDK新增网络方法。外部网络仍由现有宿主来源规则限制；模拟离线不等于在无网干净系统完成首次运行。准确命令和恢复规则见[第一个应用](first-app.md)。
