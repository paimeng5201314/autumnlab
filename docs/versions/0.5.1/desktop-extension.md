# 桌面扩展合同

适用 AutumnOS 0.5.1、SDK 0.3.0、协议1。制作人：派蒙。desktop-extension模板只使用当前实例与清单中的focus动作和note小组件；它不会注册Windows外部协议、启动命令、新建游戏或修改其他应用。所有操作先取得各自权限，不能把PM标签当成额外授权。

notifications.show需要notifications，输入id/title/body/action；title≤100、body≤500且无控制字符。action为null或本应用声明link。shown=false、reason=muted/duplicate是正常受限结果，不应显示“已发送成功”。通知每实例20条、全局100条，最多256个十分钟去重ID，尚存在的同ID一直去重。setBadge范围0–999，0清除；静音下返回0。关闭实例清除临时通知/角标；撤销或静音即时移除。

shortcuts.register输入声明过的ids数组，每次替换集合，空数组撤下。宿主长按/右键显示真实动作，点击后投递shortcuts.invoked{id,action}给原实例并重新检查权限。widgets.update仅更新声明id的最多四行纯文本，每行160字符，标题来自清单；没有HTML/JS执行能力。links.openInternal需links及新原生手势，action已声明、arguments最多八个短字符串，投递links.opened并保持当前实例。

appearance.get/changed无需敏感权限，带theme/language/scale/reduceMotion；主题支持system，当前中文正文不冒称多语言翻译。共享样例壳先订阅再读快照，防止旧值覆盖，并在pagehide/结束撤订阅。权限事件也用于清理应用已有临时引用；宿主仍在每次最终事件投递时验证代次。

验证顺序：申请通知→发送→重复观察duplicate→宿主通知中心静音后观察muted；申请shortcuts/widgets/links→登记/更新/投递→返回桌面查真实控件→继续原实例；撤销后操作失败，结束后扩展清理。DOM单测不证明WinUI覆盖层可见可点，真实窗口和截图另记。逐字段及取消/错误见[SDK参考](sdk-reference.md)。
