# 身份与权限 · 0.5.1

AutumnOS 0.5.1 把宿主登录、应用资料授权和独立服务器登录分为不同合同。游客可继续使用允许的本地功能；进入账号页登录不代表每个应用自动获得资料。应用清单必须声明 `identity.profile`，由用户授权后才能得到应用范围 ID、显示名称、HTTPS 头像地址和缓存标记。应用不会获得宿主 access token、refresh token、授权码、邮箱或完整 claims。

```javascript
// 从真实按钮操作触发；宿主自己观察原生输入。
try {
  const profile = await autumn.request('identity.requestProfile', {});
  document.querySelector('#name').textContent = profile.displayName;
} catch (error) {
  document.querySelector('#name').textContent =
    error.code === 'AUTH_REQUIRED' ? '请先在账号页登录，或继续游客使用。' : error.code;
}
```

`appScopedUserId` 仅用于本地应用资料关联，不能发送给服务器冒充认证凭据。独立后端应采用 [服务器身份](server.md) 的独立 Native client、API resource 与资源 token；尚未接线的 `identity.beginAppSession` 继续返回 `CAPABILITY_UNAVAILABLE`。

权限名包括 saves、identity.profile、storage、files.open、files.save、notifications、shortcuts、widgets、links。`permissions.query` 只读，`permissions.request` 在状态为 prompt 时需要新近的前台原生输入。JavaScript 自报 `userGesture:true` 无效。拒绝和撤销不会因循环请求自动消失；恢复授权应由用户进入真实设置操作。账号切换后旧实例不能借用新账号作用域。

资料读取与敏感动作均重新核对账号代次、来源、实例和授权。后台与挂起有不同限制；响应排队时发生撤销也须拒绝。已经交给网页内存的资料无法远程收回，因此 UI 不能承诺撤销等于抹除所有副本。

登记状态文件只是人工说明与探测历史，读取成功也不构成 token 或登录证据。真实第二账号、浏览器退出、长期刷新和独立资源往返必须各自记录执行结果。预览中的 account-a/account-b 是显式模拟，不算真实提供方验收。权限与方法对应关系见 [SDK 参考](sdk-reference.md)，本地数据隔离见 [数据](data.md)。
