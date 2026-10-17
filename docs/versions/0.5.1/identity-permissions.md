# 身份、权限与账号代次

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。登录发生在宿主账号系统应用，通过系统浏览器OIDC授权码、PKCE、state和nonce；内部应用不承载登录表单、不获得宿主access/refresh/ID token。SDK资料授权与Logto登录是两次不同决定。identity-app不会自动弹浏览器或制造测试身份。

应用先读platform.getCapabilities判断accountMode和实际能力。用户点击identity.requestProfile时，宿主检查真实账号、当前来源清单、identity.profile授权及原生手势；identity.getProfile只读已允许资料。结果仅appScopedUserId、displayName、avatarUrl或null、isCached。appScopedUserId用于本机应用资料关联，不能作为后端认证。头像地址不等于网络访问授权，样例只显示有无，不加载远程图片。

permissions.query返回prompt/granted/denied/revoked；prompt状态request需要前台原生输入，两秒内一次消费。拒绝持久化，重复脚本请求不重新弹窗；撤销后需由用户在宿主管理权限。USER_CANCELLED是用户取消或本地等待取消，不能合并成“同意”；PERMISSION_NOT_DECLARED不能用临时伪造appId绕过。正常空资料不是模拟登录成功；游客得到AUTH_REQUIRED，配置缺失得到AUTH_NOT_CONFIGURED。

授权绑定账号、真实AppIdentity/安装来源、具体权限，不绑定显示名称。切换账号提高sessionEpoch，旧请求、订阅、WebView持久状态和能力句柄不得进入新账号。关闭旧应用保护其未保存内容，由宿主协调，不自动移动游客数据。identity.changed只在profile授权仍有效时发signed_in/offline_cached，不广播token、claims或任意账号ID。permissions.changed使样例清空资料展示和失效能力，但不能宣称从任意恶意应用内存收回已经披露的数据。

取消等待使用AbortSignal，超时范围1–120000ms；已提交宿主工作可能完成。资料结果在返回UI线程前重新核对权限/代次，不能仅检查请求开始状态。真实账号成功、第二账号、长期刷新、浏览器全局退出与独立资源登记仍按各自当前报告，暂缓项不因本样例存在而通过。全部方法参见[SDK参考](sdk-reference.md)，后端凭据见[服务端](server.md)。

开发预览的固定测试账号A/B由独立宿主工具显式切换，只显示“开发测试账号 A/B”、avatarUrl=null，不签发或读取任何凭据。测试账号资料仍需要清单声明、用户手势和当前预览的内存授权；deny-permissions立即拒绝，restore-permissions恢复原决定而非全部授权。offline使测试账号profile.isCached=true，仅表示受控状态。切换需原生确认后关闭旧预览、废弃请求和句柄，返回新sessionId；正式账号系统、DPAPI和授权文件不改变。测试账号、真实账号、游客结果分别记录，模拟A/B通过不能作为真实第二账号验收。操作与作用域见[第一个应用](first-app.md)。
