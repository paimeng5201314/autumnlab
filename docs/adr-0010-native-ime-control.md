# ADR 0010 · 原生IME对照与结束事件的判据

制作人：派蒙。状态：同引擎原生对照已实测；隔离桥复验因报告发布失败停止。G1与生产放行仍未通过。

## 已有本机证据

case-4ac75558ee664064850176628e8bbfd2完成并清理。首轮IME目标不是自有textarea，16条逐动作记录中的前8条只有RPC成功，没有DOM变化；第二轮目标与document均聚焦，首非空更新产生真实trusted compositionstart/update，提交产生总compositionend1、trusted end0，最终“你好”与原生结算及ACK长度/摘要一致。同document摘要和nav1排除了重载重置计数。不能将首轮归因用户，也不能把API回执当输入生效。

旧测试把compositionend.isTrusted=true当作必要条件，但未先验证固定浏览器真实原生输入是否具备该属性。Chromium公开实现中start/update经EventTarget::DispatchEvent设置trusted，而结束事件走scoped队列，存在保留默认false的路径：[InputMethodController](https://raw.githubusercontent.com/chromium/chromium/main/third_party/blink/renderer/core/editing/ime/input_method_controller.cc)、[EventTarget](https://raw.githubusercontent.com/chromium/chromium/main/third_party/blink/renderer/core/dom/events/event_target.cc)、[ScopedEventQueue](https://raw.githubusercontent.com/chromium/chromium/main/third_party/blink/renderer/core/dom/events/scoped_event_queue.cc)。这是upstream main源码依据，不是固定Edge154字节或本机硬件对照证明；不能仅据源码把旧partial改成passed。

## 输入修复

新组合只有最新真实frame确认目标textarea与document聚焦才可开始；进行中的原生事务不由后续frame启用刷新打断。B宿主在每步前独立核目标及同文档绑定。空start只承认事务准入；首个非空更新须实际增加start/update，后续更新须增加update，事件data长度/摘要匹配请求。提交/取消须实际增加end并退出composing，提交end.data须匹配原生结算。固定只读观察预算只会失败，不作事件成立证据，不合成JS事件或强制更改焦点。

## 原生对照

同一冻结包增加独立可见原生对照A：普通WinSta0/Default，固定同一Runtime、自有页面与策略，用户直接在真正WebView中进行中文IME输入。该入口只有固定Runtime.evaluate只读观察，无CDP Input命令、无NETONLY、无自定义AppContainer profile/新station/desktop。保留真实DOM各阶段、全部isTrusted计数、最终文字/事件data摘要、文档实例与原process/browser身份，协作关闭到Normal/SDK0及已观察原句柄退出。其观察仅SDK枚举和持有的OS句柄样本，不称完整OS隔离；新UDF保留为本地artifact，不冒称已删除Windows profile。

A必须真实原进程exit0且报告通过，不能由其自报替代父端原对象退出。新prepared目录使用protected owner/SYSTEM-only继承DACL，A report和父端control-binding继承；UI只读核ACL并pin manifest、report、binding及实际SHA。所有source/manifest/runtime/core身份、原PID/创建时刻、父端退出0、真实原生start/update/input/end和end.data/“你好”、Normal/SDK0/已持原句柄退出及无错误事实都必须成立。

## 修正未证实的信任标记假设

新候选比较真实原生A与隔离桥B的同引擎语义：A有end且全部end为trusted，则B也须全部trusted；A有end但其trusted计数真实为0，则B也须为0；A无end、缺失字段、混合模式、未退出或没有受保护原进程绑定，一律拒绝，不默认接受false。保留并记录原始DOM bit，不把它改写为true。

这是将未证实的“固定true”假设替换为更严格的真实原生参考证明。实际native started/changed/ended、post-End TextChanging版本、settled/ACK/final DOM摘要、实际DOM start/update/end增量与event.data、trusted start/update/input、焦点/同document、真实帧以及原进程/Job/profile/station清理要求全部保留。只收到最终文字或false标志不能通过；JS自造结束事件不作为替代。本候选须在具体冻结审阅中明示此变化，不能事后修改旧报告或删掉未通过项。

## 一次有界执行链

1. 纯WinUI启动自检：Loaded/Rendering/原HWND可见及正常退出0。
2. 普通原生A硬件输入对照：手动输入“你好”，真实原进程0及受保护报告/绑定通过。
3. 私有B桥：至多一次随机合成NETONLY、新私有station/desktop、唯一零能力profile和固定浏览器；按真实A模式比较，并完成全部阶段和资源验收。

任一步失败不自动重试；前轮单次授权不沿用。NETONLY仍会产生真实LSA上下文/审计及可能的provider通知，不是身份认证，不宣称清除LSA。无UAC、服务或策略变更，不改既有WinSta0/Default ACL，不使用AutumnOS_Data或启动产品游戏。

2026-10-31，prepared-0b70bef97c9142019d1536d99cccf13e已另获一次三步链授权并执行。A原PID23700退出0，真实compositionstart1/update6/end1、trusted start1/update6/input6，而trusted end0；最终值与end.data均len2及“你好”SHA670d9743542cae3ea7ebe36af56bd53648b0a1126162e78d81a32934a711302e。同文档/nav1、Normal/SDK0及全部持原句柄退出均成立。A报告SHAaa4c740f22338aec581be2b60803d6a36012b586f287df5029c0a6bdfcfd6754，父端原退出绑定SHA280718c62db175d1b205de6cbda61ec1034b217398249055a43616e70880e22d。由此本机同固定引擎的false模式得到实测依据，不再只依赖上游源码推断。

B case-455f6a32f4fb453e9c937a813481d300已核对并pin该A证明，在14帧时StationHost保存报告首次失败UnauthorizedAccessException80070005；Core/UI随后失败退出，尚无B的键盘或IME阶段。原进程/Job/profile/desktop/station清理证明成立，不能据此宣称B输入通过。该次授权已消耗，不重试旧包。原保存函数没有细分子阶段，因此具体拒绝来源未证；新的自有文件IO反例表明File.Move(overwrite)在持有读取句柄时会以同码失败，即使读者允许Delete共享。本机File.Replace在允许Delete共享时成功，未允许时失败32；后续候选以真实IO回归验证修复及有限重试，永久失败仍停止并保留历史，不改原输入判据或安全边界。

新修复包的原生A/B保持not_run。完整provider/快速事务/取消/TSF边缘及G2–G5保留待验，不能由源码生成或普通文件IO检查提升为产品完整隔离。

2026-10-31后续同字节deb58链实际完成：普通A9880原退出0、end1/trusted0与受保护报告/父端绑定通过；私有B case4724d8970878469787cfcabaf34b2d2b的真实native start/change/end、post-End revision1、settled len2/你好摘要、实际commit ACK及最终DOM、end.data、同文档nav1/focus全部闭合。DOM start1/update5/end1、trusted start1/update5/input5而end0，与同包真实A模式相等。报告修复无写入错误，原Core/Host退出0且三层资源清理通过；UI仍partial/exit1，因为raw keydown/up/ACK均0。用户随后明确本轮没有按方向键，因此缺项原因确认，不归因路由故障，不删键盘验收、不把partial改成功。独立26项一致性审计SHAff0054d7cce9193de173e375ed5ca2a56cb340c32fc3497d37e9d6faa09d7010。

下一UI增量把“方向键已送达”作为中文输入前的可见步骤；同时按原生控件已处理键的独立已知语义，补窄的Preview白名单捕获、实际焦点角色和已接受down的配对up。此健壮性修正不作为4724缺项根因，也不改变本ADR的任何IME/真实A/终态清理门禁。新代码的纯检查不代替后续真实按键与IME同实例验收。
