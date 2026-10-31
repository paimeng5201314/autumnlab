# ADR 0008 · AppContainer 的 USER32 初始化前提

制作人：派蒙。状态：诊断实施中，生产宿主方案未批准放行。日期：2026-10-30。

## 实际问题

零能力 AppContainer 已在 WinSta0 下运行固定 WebView2，实际观察到的宿主、browser、renderer、GPU、utility 进程均通过对应 token 检查，并有原进程句柄退出和原子 Job 归零证据。该结果没有证明剪贴板隔离或内部呈现。

在新私有非交互窗口站中，普通 A 的浏览器前置测试通过；B 进程在托管入口前退出 `0xC0000142`。随后独立、无 CLR 的 KERNEL32-only 原生探针实际进入 B 入口，但 `LoadLibraryExW(USER32, LOAD_LIBRARY_SEARCH_SYSTEM32)` 返回 NULL、错误 `1114`。同一轮 A 的 USER32/GDI32/SHELL32 和实际 station/desktop 名称绑定全部通过。

这证明故障发生于 USER32 加载初始化阶段。它没有证明需要某一权限位、必须允许 clipboard、不能使用非交互 station，或 ChemLeap 本身导致故障。原生探针没有加载游戏、浏览器或调用 clipboard。

原始报告：`artifacts/isolation-completion-20261030/clipboard-runs/clipboard-case-658a0a55f67847039f9694c519b1582a/report.json`，SHA-256 `dd890ab8d057060f4d1441a4d4d4f8d2366fbba49a8856b0965b2f6f6efd121a`。A 原句柄 exit0、B exit20，两 Job 空、EOF 和资源退休成立，精确 profile 删除与原路径/SID 不存在后查通过。原 station 独立后查返回 NULL/错误2，另存在同目录 station-absence 报告；没有覆盖原失败报告。

## 决定

先以普通权限把 desktop 变量与 station 变量分开。新的独立父程序固定运行三组原生 DLL 加载对照：

| 用例 | 进程 | 新 desktop 对精确 package SID 的权限 |
| --- | --- | --- |
| A87 | 普通用户 | 0x87；普通 A 仍依赖自有 owner ACE |
| B87 | 独立零能力 AppContainer | 0x87 |
| B200C7 | 另一独立零能力 AppContainer | 0x200C7 |

三组使用同一冻结原生文件，各自创建随机命名的新 desktop；两个 B 各自使用新 profile。现有 WinSta0、Default desktop 和任何既有目录的 ACL 保持原字节。新 desktop 只有 owner、SYSTEM 和对应 package 三项允许 ACE，并带 Low/NW 标签。父程序不调用 SwitchDesktop、窗口创建、COM、浏览器或 clipboard API，不请求 UAC。

`0x87` 包含 READOBJECTS、CREATEWINDOW、CREATEMENU、WRITEOBJECTS。`0x200C7` 只增加 READ_CONTROL 和 DESKTOP_ENUMERATE；这两项连同 READOBJECTS 是 Microsoft 文档给出的 desktop GENERIC_READ 映射。文档没有承诺 USER32 初始化一定请求这两个位，因此该矩阵属于诊断，不属于已经成立的修复。

每个子进程原子加入独立的新 Job（flags0，无 kill-on-close 或 breakaway），仅继承自有 stdout pipe。父程序从真实原进程 token 验证身份；子端 nonce、实际对象名称和 DLL 阶段输出作为补充。结束需要原句柄 signaled、Job ActiveProcesses/PID 列表空、stdout EOF，再释放原句柄和对应新对象。超时只标 pending，不能当作退出或隔离成功。

## 结果解释与下一关

2026-10-30 本机矩阵实际完成：A87、B87、B200C7全部加载三DLL并核验实际对象绑定、原exit0；两个B的真实token均AC1/IL4096/capability0/对应SID。三个原Job空、EOF、原句柄退休、各新desktop关闭后精确不存在；两个新profile删除及原路径/SID不存在后查通过，原station/主线程desktop保持不变。报告位于 `desktop-native-runs/invocation-7974bb8839c74ad88d22d91480a39c4a/desktop-native-case-47179f5c098944399f5a5da5fdd25453/report.json`，实际源快照sha256:7f81235b2ff435e68c290e8eccaa9c5713398379ea1a313396ee5c8ff38d8335。0x87在该WinSta0前提下足够，因此不将补齐desktop读取位当作修复；继续只读比较station差异，私有站后续UAC单独准备授权。

矩阵执行完成与 DLL 成功分开记录。B 的明确 terminal failed/exit20 可以形成有效诊断结果；缺输出、崩溃、审计缺口或清理失败不能冒充负例通过。普通 A 必须真实完成三 DLL 和对象绑定，才能解释 B 对照。

如果仅补齐读取位的 B 成功，再分开定位 READ_CONTROL 与 ENUMERATE；仍需返回私有 station 验证，不能把 WinSta0 的结果外推为完整隔离。如果两个 B 都成功，则当前 desktop 0x87 本身足以在这套 station 前提下初始化，须继续检查私有 station 的差异。如果两个 B 都失败，保留真实阶段，不添加无依据的权限。

本 ADR 不接受以下变更：向现有 WinSta0/Default 授权、给 package 加 World/AllApplicationPackages 或 clipboard 位、关闭 Chromium sandbox、变更全局策略、把独立外部窗口当作内部应用，以及用应用名称或报告自称安全解除生产门禁。

新的私有窗口站 UAC 必须在对应具体代码、影响和冻结字节准备完成后单独取得明确授权；已消费的单次授权不沿用。G1 仍需真实浏览器、六项剪贴板对照、内部动画/键鼠/中文 IME；G2–G5 不越过这些前置证据。

## 实现与复现

源码：`artifacts/isolation-completion-20261030/desktop-native-controller/`、`native-user32-loader/NativeDesktopLoader.c`。构建和执行入口分别为 `scripts/Build-DesktopNativeProbe.ps1`、`scripts/Run-DesktopNativeProbe.ps1`；实际运行结果与构建 ID、源快照、源 ZIP、二进制哈希记录在新建的 `desktop-native-builds/`、`desktop-native-runs/`，尚未执行的项目保持 not_run。

## 下一私有 station 对照包的限定范围

只读检查现有 WinSta0 的两项 built-in AppContainer 组时，各有直接作用于该 station 的 `0x20327` ACE，另有 inherit-only generic ACE；Low/NW 标签与新私有 station 相同。过滤报告为 `winsta0-metadata-inspector/readonly-8f8a6090f94043798831d12f667dddd4.json`。它只是描述符元数据，不是实际 token 的有效权限或 USER32 因果证明；不复制整个 WinSta0 ACL。

正在准备的下一包固定使用同一新非交互 station、同一新 profile SID、desktop `0x87` 和同一原生文件，依次执行 A23、B23、B20023、B20123、B20323、B23-restored、A23-restored。新 station 的 package ACE 只在前组原进程退出、Job 空、EOF 和原资源退休后变更，并逐次回读 owner/SYSTEM/package 三 ACE 和 Low 标签：

| package mask | 相对上一步增加的权限 |
| --- | --- |
| 0x23 | 原基线 |
| 0x20023 | READ_CONTROL，读取该新对象的安全描述符 |
| 0x20123 | WINSTA_ENUMERATE，允许枚举该新 station |
| 0x20323 | WINSTA_READSCREEN，只涉及该新非交互 station 的屏幕对象 |
| 0x23 | 恢复初始 DACL，回读哈希与初始值一致后重测 B/A |

这些组合都不含 clipboard、创建 desktop、写属性、退出 Windows、WRITE_DAC 或 WRITE_OWNER 的 package 权限。不向其它用户、World 或 AllApplicationPackages 加 ACE；不修改现有 WinSta0/Default。READSCREEN 是单列的候选权限，不能笼统称为非交互 station 的 generic read。探针只有 DLL 加载和自身 GUI 对象元数据调用，没有屏幕捕获、窗口创建、输入、browser 或 clipboard API。

高权限 helper 的职责保持创建并持有一个初始 `0x23` 新 station，DACL 对照由普通 controller 对自己本次创建并已验证的对象执行。恢复对照用于识别缓存、顺序或瞬态影响；四种递增组合只能找到兼容组合，不能断言某一个位就是唯一原因。全部 B 明确 DLL 初始化失败也可以形成完整诊断结果，不代表隔离通过。任一退出、审计、报告或清理证据缺失都不能判完成。

该包已冻结为prepared-d9d1c0879d2c4d1b8826503818a4fb09，经过45纯回归、2真实断pipe回归和普通入口负控制。派蒙随后明确允许该包一次UAC，已执行并消耗，原始报告为 `clipboard-runs/clipboard-case-d908454c7e6f4600aca98998eb18da70/report.json`（SHA c331fa5e94954c9e21d059defdf8a9539698633ae2ba7d9345538df471553914）。

| 实测case | 原生结果 |
| --- | --- |
| A23 | 三DLL/精确GUI对象绑定成功，原exit0 |
| B23 | USER32失败1114，原exit20 |
| B20023 | 三DLL/绑定成功，原exit0 |
| B20123 | 三DLL/绑定成功，原exit0 |
| B20323 | 三DLL/绑定成功，原exit0 |
| B23-restored | 再次USER32失败1114，原exit20 |
| A23-restored | 三DLL/绑定成功，原exit0 |

同station/desktop87/native字节及同B profile SID，B真实AC1/IL4096/capability0。7个Job空、原句柄退出/EOF/资源退休、精确profile删除及path/SID不存在、原DACL恢复哈希一致均通过；独立精确station后查错误2。没有browser或clipboard动作。

据此，当前配置的原生加载差异可收窄到缺少READ_CONTROL：仅加该位即成功，移除后再次失败，不需要把ENUMERATE/READSCREEN带入下一最小候选。该结论限于此平台及原生DLL路径；browser、剪贴板和内嵌输入仍需真实验收。下一包仅将新station的package23变为20023，在真实浏览器A/B前置后执行六项合成clipboard对照，再恢复初始DACL并精确清理。新包必须重新冻结审阅并取得单独UAC授权；生产G1–G5状态不提升。

该浏览器包已准备为prepared-a634118bc57f4985971f3bc1e7064c5a，模式clipboard_readcontrol_sixcase，构建probe-d98d8858c7214c30842f832c6d910350，源码sha256:3379b35ebcdc466e0695c21f11d6d7513b89543db6c39ab95d5850dccee1440f。56纯门禁和2真实断pipe回归通过；包含11项实际ReadControlBoundaryPolicy的新回归，明确拒绝20123/20323、clipboard和写权限，以及已有子进程时启用/未退完时恢复。ordinary controller在首个child前启用20023，运行时逐操作回读，14个browser/collector子进程组均原退出/Job0/EOF/retired后恢复23；helper仍只初始23持有新站。此包浏览器/六case尚not_run，新UAC待单独授权。

执行后记：该包随后收到明确一次授权并执行，case-db3a01bfa141493594607cbb0f53e4fc的A/B浏览器前置及六case全部通过。A copy/cut5→14/24→33 marker匹配，paste43保持且真实读取native-only baseline；B copy/cut19→19/38→38，paste48保持且真实trusted空事件和最终空DOM。所有14Job空、64原进程exit0、8Normal/SDK0/EOF、30个B观察进程AC1/同SID/caps0/IL≤4096；原DACL初末f100ff...相等、profileDelete0/path3/SID80070002、原station独立后查错误2。原report SHA6afbd43b955bb30da856df34652de0948c0e3591e0edced0b6c6b38d9a37aaa3。至此当前固定Runtime的私有站剪贴板机制通过；普通用户无UAC启动、内部显示/输入/IME和生产G2仍未通过，不能解除现有产品门禁。新普通会话方案见ADR0009，仅准备，不代表采用或运行。

## 官方依据

- [Desktop security and access rights](https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights)：权限位和 generic 映射。
- [Window station security and access rights](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-security-and-access-rights)：station 各权限及交互/非交互 generic 映射。
- [CreateWindowStationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)：命名创建权限和既有对象语义，不能采用未知共享对象冒充新隔离对象。
- [CreateDesktopW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw)：创建线程的 desktop 绑定和所请求权限要求。
- [Implementing an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)：profile、package SID、capability 和启动属性；普通 AppContainer 与 LPAC 的属性不同，本轮不把两者混称。
- [LoadLibraryExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw)：System32 搜索与加载失败检查；成功后的 LastError 不能用作本次失败证据。
