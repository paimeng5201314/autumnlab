# 应用桌面扩展 · 0.5.1

AutumnOS 0.5.1 的桌面扩展是宿主内部通知、角标、快捷动作、纯文本小组件和应用内链接，不是 Windows 全局通知、任意 HTML 小组件或外部协议注册。用 `create desktop-extension` 创建可用模板，具体创建与资源定位见 [第一个应用](first-app.md)。安装清单先声明权限、用途和有限动作，运行时仍须取得用户授权。

```json
{
  "permissions": ["notifications", "shortcuts", "widgets", "links"],
  "permissionPurposes": {
    "notifications": "由你发送内部通知和角标",
    "shortcuts": "为图标增加回到笔记的操作",
    "widgets": "显示你选择的纯文本摘要",
    "links": "把 focus 动作交给当前应用"
  },
  "desktop": {
    "shortcuts": [{"id": "focus", "title": "回到笔记", "action": "focus"}],
    "links": ["focus"],
    "widgets": [{"id": "note", "title": "我的笔记"}]
  }
}
```

以上是清单片段，应合入模板现有 manifest，不替换必需的 appId、version 等字段。最多分别声明 4 个快捷动作、链接动作和小组件。标题由清单给出；小组件每次最多 4 行纯文本、每行 160 字符，不执行脚本。

授权后可调用 `shortcuts.register({ids:['focus']})`、`widgets.update({id:'note',lines:['待办 1 项']})` 和 `notifications.show({id:'note-1',title:'笔记',body:'已保存',action:'focus'})`。注册集合每次替换当前集合；通知 action 必须属于当前应用已声明的动作，不能是 URL 或命令。通过 `onEvent('links.opened', ...)` 和 `onEvent('shortcuts.invoked', ...)` 处理真实宿主控件触发的事件。

通知标题最多 100 字符、正文 500，不接受控制字符；角标 0–999，0 清除。静音或撤销会移除通知与角标；应用结束清除该实例的扩展，后台继续使用仍需实际授权。宿主核对实例身份，快捷动作不会另开第二个同一游戏。

这些接口不能创建文件夹、多桌面、自定义 Dock 或任意排序 API。当前宿主自身的三列图标排序不等于开放桌面编辑能力；界面状态见 [界面与交互](design.md)。测试应覆盖拒绝、撤销、静音、重复通知、跨应用动作拒绝、账号切换和关闭后的迟到事件，物理触控仍需独立设备验收。
