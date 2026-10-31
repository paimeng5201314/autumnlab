# 生产运行视图后端 · G2-A

制作人：派蒙。当前为内部接口提取，源码已接入，Windows构建/原生运行成绩以实际报告为准；本页不代表第三方已获得运行资格。

`src/AutumnOS.Shell/RuntimeViewBackend.cs`定义内部`IRuntimeViewBackend`和现有进程内`WinUiWebViewBackend`。本增量只将原WebView机械操作从`WebAppHost`移出，不新增进程后端，不解除`required_boundaries`，不改变公开SDK、包格式、权限、账号或存档路径。后续独立进程须使用单独的实际投递/撤权协议，不能把此接口的同步Post实现为入队成功。

## 所有权与初始化

`WebAppHost`先把backend保存为自己的字段，再调用`Attach()`；构造函数不创建或挂接原生控件。因此事件注册/挂接失败时，原控制对象仍有明确所有者，正常Dispose重试可关闭并移除它。

环境和controller分两步实际await，创建尝试一经开始就保留状态；不以取消代理Task或计时器推断原生创建已返回。父端继续保存`startInProgress`，并在每步后检查取消、固定版本/实际UDF、InPrivate和实际browser image。环境创建失败但没有返回environment对象时，不能证明资源为空，保持pending并保留维护租约。

backend对外提供原生事实快照和受限的导航、source/content事件、资源请求/deferral、消息及进程失败事件，不暴露CoreWebView2对象。资源只由父端已校验包字节及固定响应头产生；512流上限保持。主程序仍负责parser-gate的一次性完成、原文档/来源/实例绑定、网络原生挑战，以及实际成功后才能放出应用字节的顺序。

导航、source/content及WebMessage事件的原生属性继续延迟到父端原guard/try内读取；创建managed包装本身不能提前访问可能已随Close失效的native对象。纯managed桥接回归覆盖构造与已关闭短路不读native getter、帧取消不读URI、正常读取才调用及异常不被吞；这不替代真实COM关闭场景。

## SDK与权限

父端保持原`SdkEventReady`冻结revision、Dispatcher后的同Session/Source检查及`DeliverEvent`；响应继续在HandleMessage await后重新核同Session/Source，再通过`DeliverResponse`完成。backend的`PostWebMessageAsJson`在当前调用栈内直接调用真实原生Post，整个调用仍在现有权限→state→账号租约内；没有async void、队列或远端ACK的语义替换。

后续远端投递需要异步reservation、原始实例/文档/账号/权限代次、实际子端STA Post receipt及权威变更前的fence。ACK未知时既不能把取消当未投递，也不能释放旧ticket后允许迟到投递；这些尚未在本增量实现。资料、文件能力、存档和用户提示保留在父端，不传宿主凭据或裸文件系统能力到应用进程。

## 退休与更新锁

父端先结束Session、拒绝parser gate、取消及关闭文件能力，再分别尝试CloseView、只移除本实例的旧控件、释放全部response streams。backend的`TryRetire(startInProgress)`只在控件/流已退且原生创建已真实返回后，检查原PID退出事件、原持Process对象HasExited和SDK进程集0；成功才释放原进程/环境并置显式retired状态。

退出类型Normal用于成功验收，资源清理本身允许异常退出但已证明组空，防止故障后永久假pending。旧控件关闭/移除或stream Dispose失败可重试，不能清空共享容器；任何失败都阻止提前释放运行租约和维护租约。`ResourcesReleased`仍须启动/清理都不忙、backend原资源退休、包资源与bootstrap清空、两种租约释放。更新、关机、预览替换和原实例召回继续依赖这一事实。

键盘归属仍限定当前可见宿主、未销毁/已导航/期望前台和启动时持有的原browser对象，不以重新查询PID代替原句柄。独立进程后端以后必须将WinSta0的原生画面代理纳入同样的实例判据，不能简单允许任意Shell PID。

## 验证边界

现有RuntimeSession投递/撤权/账号/关闭并发回归、parser-gate重放与文档绑定测试必须保留。普通测试工程不引用WinUI Shell，因此纯测试不能证明新适配器的真实COM/控件detach；需要对应Windows Release及原生成功、故障、关闭/重启的实际报告。当前G1自有机制f670的通过不转记为本生产接口或第三方应用验收；G2生产运行、G3–G5仍待完成。
