# 乐享同步 · WinUI 3

Windows 多窗口输入同步工具。在主窗口里的鼠标点击、键盘输入、中文文本会实时镜像到一个或多个从窗口，
**全程不抢焦点** —— 主号在打怪、小号在同一台机器上跟着动，不用来回切窗口。

本项目是原 Tauri 2 + React 版本的**前端重写**：界面按设计稿用 WinUI 3 重新实现，同步引擎（Rust + Win32）逐行保留，行为不变。

```
WinUI 3 (C#/XAML)  ──P/Invoke──►  window_sync_core.dll (Rust)  ──► Win32 低级钩子 / 输入注入
```

---

## 功能

| 页面 | 能做什么 |
|---|---|
| **同步** | 进程 → 窗口两级选择（进程树可折叠），主窗口单选、从窗口多选；勾选要镜像的项（鼠标 / 键盘 / 文本）；一键启停，保存为方案 |
| **多开启动** | 按组批量拉起客户端，等窗口出现后按「屏幕基准尺寸 + 相对坐标」自动落位；时序参数（等待超时、点击延时）可调，带一键流程进度 |
| **方案** | 方案只记窗口标题指纹，目标程序重启后仍能一键还原；支持导入 / 导出 JSON、用当前选择覆盖 |
| **设置** | 外观 / 同步细节 / 热键与提示音 / 管理员权限 / 诊断与日志，六张卡片一屏排完 |

窗口外壳：

- **迷你条**：折叠成一条 406×49 DIP 的桌面长条，默认常驻置顶、可拖动，条上直接「置顶 / 启停 / 还原 / 最小化 / 关闭」。
- **自定义导航**：左侧 3px 强调色指示条 + 圆角胶囊选中态，没有用 `NavigationView` —— 为了还原设计稿的选中态。
- **系统托盘**：双击恢复窗口，右键显示 / 启停同步 / 退出。
- **全局热键**：`Ctrl + Alt + S` 启停同步，可在设置里关闭。
- **关闭窗口**默认最小化到托盘。
- **双击不最大化**：标题栏双击、迷你条双击都做了屏蔽；最大化按钮和 `Win + ↑` 仍照常可用。

## 技术栈

| | |
|---|---|
| UI | WinUI 3 / WindowsAppSDK 1.8，.NET 8（`net8.0-windows10.0.19041.0`） |
| 架构 | MVVM，`CommunityToolkit.Mvvm` 8.4（`[ObservableProperty]` / `[RelayCommand]`） |
| 引擎 | Rust 2021 `cdylib`（MIT），导出 C ABI + JSON 信封，C# 侧 P/Invoke |
| 形态 | 免 MSIX（`WindowsPackageType=None`）+ 自包含，单文件发布约 84 MB，双击即用 |

## 快速开始

前置：Windows 10 1809+、.NET 8 SDK、Rust 工具链（MSVC 目标）。

```powershell
# 构建（首次会自动 cargo build --release，并把 window_sync_core.dll 拷到 exe 同目录）
dotnet build src/LeXiangSync/LeXiangSync.csproj -p:Platform=x64
```

两个常用开关：

```powershell
# 只改了 Rust 代码
.\scripts\build-core.ps1

# 只编 C#，跳过 cargo
dotnet build src/LeXiangSync/LeXiangSync.csproj -p:Platform=x64 -p:SkipRustCoreBuild=true
```

发布单文件：

```powershell
dotnet publish src/LeXiangSync/LeXiangSync.csproj -c Release -p:Platform=x64 -p:PublishSingleFile=true -o dist
```

产物是 `dist\LeXiangSync.exe` 一个文件（Rust 核心也打进去了），直接双击运行。
用户数据落在 `%LOCALAPPDATA%\LeXiangSync`（settings / plans / shortcuts 三个 JSON，原子写入）。

## 目录结构

```
window-sync-winui/
├── core/                        # Rust 同步引擎（cdylib）
│   └── src/
│       ├── lib.rs               # 原 Tauri 版 lib.rs 的逻辑本体
│       ├── ffi.rs               # C ABI 导出层（JSON 信封）
│       ├── event_bus.rs         # 分级事件队列 + 按键状态表
│       ├── window_id.rs         # 窗口身份模型（HWND/PID/启动时间/EXE/类名/父窗/客户区/DPI）
│       ├── window_watch.rs      # 窗口生命周期监听（SetWinEventHook，事件驱动）
│       └── text_sync/           # 文本同步
│           ├── mod.rs           #   调度器：身份 / 基线 / 分派 / 校验重试
│           ├── control.rs       #   焦点控件身份与能力识别、读文本与光标
│           ├── diff.rs          #   通用单区间差分（UTF-16 安全）
│           └── apply.rs         #   三条写回通道：增量 / 退格 / 整段重写
├── scripts/build-core.ps1       # 单独构建 Rust 核心
└── src/LeXiangSync/
    ├── Native/                  # P/Invoke 封装（SyncNative / Win32 / WindowApi）
    ├── Models/                  # 数据模型，与 Rust 结构体一一对应
    ├── Services/                # 引擎门面、日志、持久化、托盘、热键、主题、多开启动…
    ├── ViewModels/              # 每个页面一个 VM
    ├── Views/                   # 四个页面
    ├── Converters/              # XAML 值转换器
    ├── Helpers/                 # 图标、窗口分组、方案指纹、显示名、双击拦截
    ├── Styles/                  # 设计令牌 + 自定义控件模板
    └── Assets/                  # 图标 / 二维码 / 提示音
```

## 分层

```
Views (XAML)  ──x:Bind──►  ViewModels  ──►  Services  ──P/Invoke──►  Rust 核心
                                ▲                 │
                                └──── 状态推送 ─────┘（引擎回调 → DispatcherQueue → UI 线程）
```

- **`SyncEngine`** 是唯一真相源：主窗口、从窗口集合、同步项、运行状态都在它手里，四个页面共享同一份，不各存一份。
- **`LogService`** 是 UI 与 Rust 侧统一的日志出口，自动封送到 UI 线程，环形缓冲（上限可配）。
- **`DataStore`** 负责持久化，全部 JSON 原子写入。
- **启动 / 停止同步必须在 UI 线程执行**：Rust 侧安装的 `WH_MOUSE_LL` / `WH_KEYBOARD_LL` 低级钩子依赖安装线程的消息循环。

## 事件管线（为什么不丢按键）

同步链路是 `全局钩子 → 分级事件队列 → 投递线程`。队列不是简单的有界通道，而是按「丢了会不会让从窗口卡死」分级：

| 事件 | 处理 |
|---|---|
| 鼠标移动 | 只保留最新一条（覆盖写），位置本来就是「当前状态」 |
| 滚轮 | 累计 delta，投递时一次发出去 |
| 键盘按下 / 抬起 | 可靠队列，FIFO，绝不丢 |
| 鼠标按下 / 抬起 | 可靠队列，绝不丢 |
| 文本同步 | 独立队列 + 独立线程，逐字注入再慢也不挡鼠标键盘 |
| 状态通知 | 按内容去重，同一状态不重复推给 UI |

早期实现是 `sync_channel(2048)` + `try_send`：队列一满就**静默丢事件**。鼠标移动瞬间就能把它灌满，
紧跟着的「抬起」被扔掉，从窗口于是永远以为键还按着 —— 角色自己往前走、鼠标一直拖着。
把容量从 2048 抬到 8 万只是把概率推后，问题还在。

配套的按键状态表（`event_bus::PressedState`）记录哪些键 / 鼠标键还按着，在三个时机补投抬起：

1. **停止同步** —— 排进可靠队列，保证排在已入队的按下事件之后投出去，然后关队列、等线程排空才返回；
2. **主窗口失焦** —— 钩子只在主窗口是前台时转发，用户 Alt+Tab 走开后抬起消息就没人转发了，
   不主动放就是永久卡键。除了钩子里的检查，还有一个 30ms 跳一次的看门狗兜底（用户可能一个输入都不再产生）；
3. **从窗口失效** —— 从同步列表剔除，不再往已销毁的窗口投递。

代码：`core/src/event_bus.rs`（队列与状态表，纯逻辑，单测覆盖）、`core/src/lib.rs`（钩子采集、投递、生命周期）。

## 文本同步（中文、粘贴、中间编辑）

键盘钩子拿不到中文：`WH_KEYBOARD_LL` 只有虚拟键码，中文是输入法组字后由
`WM_IME_COMPOSITION` / `WM_CHAR` 投给窗口的，钩子看不见那个字；拿键码去
`PostMessage(WM_KEYDOWN)` 也不会经过输入法。所以中文只能「读文本 → 算差量 → 补投」。

第一版只做了「末尾追加 / 末尾删除」两种形状，于是中间插字、移动光标后编辑、全选替换、
输入法上屏全都同步不过去；而且快照没有绑定控件的身份，从一个输入框切到另一个
（新文本恰好以旧文本为前缀）时，会把整个新内容当成「追加」灌过去。

现在的链路是：

```
键盘钩子 ─┐                                          ┌─ 从窗口 A（独立跟踪期望值 / 校验）
剪贴板   ─┼→ 唤醒 → 采样焦点控件 → 差分 → 分派 ─┼─ 从窗口 B
焦点变化 ─┘         (身份+文本+光标)  (单区间)      └─ 从窗口 C
```

**1. 差分是通用的单区间替换**（`diff.rs`）。求公共前缀、再求公共后缀，中间剩下的就是改动区间。
末尾追加、末尾删除、中间插入、全选替换、输入法上屏都只是区间取法不同，一套逻辑全覆盖。
位置长度一律按 UTF-16 code unit 算（与 `EM_GETSEL` / `WM_SETTEXT` 同口径），
边界劈开代理对时会自动往回让，不会把 emoji 切成半个。

**2. 快照绑定控件身份**（`control.rs`）。`ControlId = 句柄 + 进程 + 窗口类名`。
句柄变了就是换了控件 → **只重设基线，不推差量**，前缀陷阱自然消失。

**3. 每个从窗口独立决策**，读得到就整段重写，读不到才盲投末尾增量：

| 从窗口状态 | 处理 | 为什么 |
|---|---|---|
| 已等于目标文本 | 什么都不做 | 键盘通道已经送过去了，再写一遍就是「打一个字出两个字」 |
| 读得到（标准 Edit / RichEdit） | **整段重写** + 校验 + 失败重试一次 | 绝对赋值不会重复；逐字补是相对操作，会和键盘通道抢同一个字 |
| 读不到（自绘 / 游戏） | 只在「末尾改动且不超过 64 unit」时逐字补 | 盲写整段会把对方已有内容冲掉 |
| 内容解释不成「落后」 | 跳过并记一次日志 | 多半是用户在从窗口上自己改的，不该覆盖 |

**4. 读不到 ≠ 没有输入**。这三件事被拆成不同的状态，只有第一种才允许说「没有变化」：

| 情况 | 读出来是 | 处理 |
|---|---|---|
| 标准输入框且确实是空的 | `Text{空}` | 正常，基线为空 |
| 自绘输入框 / 焦点在顶层窗口 | `NotText` | 不做差量，日志提示改用剪贴板中转 |
| 目标进程卡死、消息超时 | `Unresponsive` | 保留基线重试，**绝不当成文本变空** |

超时与「读到空」靠哨兵值区分：出参先填 `usize::MAX`，调用后还是它就说明这次 `SendMessage`
没落地。另外，标准输入框类名之外还覆盖了 WinForms（`WindowsForms10.EDIT.*`）和
Delphi/VCL（`TEdit` / `TMemo`）—— 它们内层都是原生 EDIT，漏判就会被当成自绘。

还有一个反向验证：连续转发 8 次可打印按键、而焦点控件的文本一次都没变，就判定「类名像输入框
但读不到」，打一条日志说明文本差量对它无效。这是拿键盘钩子当证人，不是靠猜。

**5. 触发不是死轮询**。键盘钩子每转一个键就唤醒调度器（条件变量，不是裸 sleep），
所以输入延迟基本是 0；空闲时 120ms 一拍，每 8 拍做一次一致性校正，
把后启动的、漏过一拍的从窗口拉回来。停止同步时唤醒 + join，实测 9–27ms 返回。

## 窗口身份与生命周期

### 为什么不能只认 HWND

句柄是系统发的编号，**会被复用**：关掉一个窗口后，新窗口完全可能拿到同一个数值，
而这时的 `IsWindow(hwnd)` 对那个陌生窗口照样返回真。只比句柄，就会出现
「用户换了个程序，同步还在往别人的窗口里打字」。所以 `window_id.rs` 记的是一整套身份：

| 字段 | 用途 |
|---|---|
| `HWND` | 当前窗口句柄 |
| `PID` | 进程 ID |
| 进程启动时间 | 挡住 PID 复用（PID 会被回收） |
| `EXE` 完整路径 | 区分同名程序（两个目录下的 `client.exe`） |
| 窗口类名 | 识别窗口类型（`UnityWndClass` / `Chrome_WidgetWin_1`…） |
| 窗口标题 | 辅助识别 |
| 父窗口 `HWND` | 识别窗口层级，区分主窗与工具窗 |
| 客户区尺寸 | 坐标映射（比例换算要有源尺寸） |
| `DPI` | 处理缩放（不同显示器缩放不一样） |

判定规则：`PID` 必须相等，其余字段「取不到就通配、取到了就必须相等」。
`0` / 空串表示「没取到」，不参与比较 —— 否则进程信息读失败会把正常窗口误杀。

### 事件驱动，而不是轮询清理

早先是「刷新进程列表时顺手清理失效窗口」+「每次投递前 `IsWindow` 兜一下」。
前者要靠用户手动操作，后者只是热路径上的补丁。现在用 `SetWinEventHook` 直接订阅：

| 事件 | 处理 |
|---|---|
| `EVENT_OBJECT_DESTROY` | 从窗口 → 立刻剔除；主窗口 → 立刻停同步 |
| `EVENT_OBJECT_HIDE` | 记录日志（最小化到托盘、切场景），**不**剔除 |
| `EVENT_OBJECT_CREATE` / `SHOW` | 观察用，不干预 |
| `EVENT_SYSTEM_FOREGROUND` | 前台换了 → 处理残留按键、唤醒文本同步 |

隐藏为什么不剔除：游戏切场景、最小化到托盘都会隐藏窗口，但消息照样能投递，剔除了反而漏同步。

另有一条兜底看门狗（`start_focus_watch` 里每 4 拍 ≈ 1 秒）复查一次身份，
专门对付「句柄被复用」这种不产生任何事件的情况。事件是主路，看门狗是保险。

### 回调里不做重活，也不在回调栈里停同步

`WINEVENT_OUTOFCONTEXT` 的回调由**安装钩子的那条线程**调用，这里单开一条线程装钩子跑自己的消息循环，
免得跟着 UI 线程一起卡。三个细节都是踩出来的：

1. **回调里只入队**。停同步要 join 输入 / 文本线程，在回调栈里做等于把事件流堵死，
   极端情况下还会「在回调里等自己所在的线程退出」。真正的处理放在循环每轮之后（已退出回调栈）。
2. **入队后要给自己投一个唤醒消息**。出上下文钩子的回调是在 `GetMessageW` **等待期间**被调用的，
   那时循环还没往下走；队列里没有别的消息，`GetMessageW` 会继续等，攒下的事件就一直没人处理。
   投一个 `WM_APP+0x52` 过去，循环立刻转一圈来 drain；用原子标志合并，窗口风暴也只投一条。
3. **叫停用消息窗口，不用线程消息**。`PostThreadMessageW(tid, WM_QUIT)` 有个必现竞态：
   `start()` 里 `thread::spawn` 一返回调用方就可能 `shutdown()`，此时 `tid` 还没写进全局，
   投给 `tid = 0` 什么都不会发生，`GetMessageW` 永远阻塞、`join()` 跟着永远等。
   改成在监听线程里先建一个 `HWND_MESSAGE` 窗口，`start()` 等它建好才返回，
   之后 `PostMessageW` 到这个句柄必然能送到。

退出等待还留了超时（2s）：真出了意外也只脱管一条线程并记日志，绝不把 UI 线程钉死在那里。

## 原生互操作契约

所有调用返回同一个 JSON 信封，C# 侧统一解包：

```json
{ "ok": true,  "data": { } }
{ "ok": false, "error": "…" }
```

| Rust 导出 | 说明 |
|---|---|
| `wsync_get_processes` / `wsync_get_windows` | 枚举进程 / 窗口 |
| `wsync_get_sync_status` | 查询状态 |
| `wsync_start_sync` / `wsync_stop_sync` | 启停同步 |
| `wsync_update_sync_options` | 运行中修改同步项 |
| `wsync_focus_window` | 聚焦并还原窗口 |
| `wsync_launch_app` / `wsync_kill_process` / `wsync_kill_process_by_name` | 进程管理 |
| `wsync_set_status_callback` / `wsync_set_log_callback` | 注册推送回调 |
| `wsync_free` | 释放返回的字符串 |

约定：输入为 UTF-8 JSON；返回字符串由 Rust 用 `CString::into_raw` 分配，**必须**用 `wsync_free` 释放
（`Native/SyncNative.cs` 已在 `finally` 里处理）。

## 实现备注（WinUI 3 上踩过的坑）

留档给后来者，这几条都不会在编译期报错，只能靠跑到界面上看：

1. **`VisualStateManager.VisualStateGroups` 必须写在模板根元素里面。** 写成根元素的兄弟节点会被静默忽略，控件永远停在未选中态。
2. **同一个 XAML 文档里只能有一个 `ControlTemplate`。** 两个模板会共用同一个 VisualState 名称作用域，报 `WMC0047 Element name already used` —— 所以每个自定义模板单独一个 `ResourceDictionary`（`Styles/*.xaml`）。
3. **系统强调色会污染所有默认控件。** 必须在 `App.xaml` 里、`XamlControlsResources` **之后**覆写 `SystemAccentColor` 与 `Light1..3 / Dark1..3`，否则复选框、开关、进度条会跟着系统主题变成别的颜色。
4. **双击最大化走的是非客户区消息**，XAML 的 `DoubleTapped` 收不到。要拦只能挂窗口过程（见 `Helpers/TitleBarDoubleClickGuard.cs`）；而且**不能直接吞掉** `WM_NCLBUTTONDBLCLK` —— 系统的双击序列没收尾，紧接着的第一次点击会被判成双击，表现为「双击过标题栏之后窗口拖不动了」。正确做法是把它降级成 `WM_NCLBUTTONDOWN` 转发。
5. **迷你条的宽度要实测，不要写死。** 内容网格 `Measure(∞)` 拿真实宽度，再加「隐形窗口边框」（`AppWindow.Size - RootGrid.ActualWidth`，且必须在 `SetBorderAndTitleBar(false,false)` **之前**量）。写死只要比内容宽一点点，多出来的宽度就会被 `*` 列吃掉，堆成中间一段莫名其妙的空白。
6. `OverlappedPresenter` 没有 `IsFullScreen` 属性，判全屏只能看 `Presenter is FullScreenPresenter` 再 `SetPresenter(AppWindowPresenterKind.Overlapped)`。
7. **鼠标消息的坐标系是按消息类型分的**，混用会静默偏移：`WM_MOUSEMOVE` 与左右中键的 lParam 是**目标窗口客户区**坐标，而 `WM_MOUSEWHEEL` 的 lParam 是**屏幕**坐标。滚轮若塞客户区坐标，接收方会把它当屏幕点，整体偏移「客户区原点」那么远，窗口换个位置就滚到别的控件上。另外主从窗口客户区尺寸不同时要按比例换算，否则边缘区域点不中同一个按钮。见 `core/src/lib.rs` 的 `mouse_message` / `map_client_point`，对应单测在文件末尾。

## 已知限制

- 向**管理员权限**的窗口投递消息需要本程序同样提权；普通权限启动时日志会提示，设置页可一键提权重启。
- 通过 `.lnk` 启动的程序拿不到 PID，需手动关闭（界面已注明）。
- 剪贴板中转按 `Ctrl+C` 触发，单次上限可配（默认 800 字符），逐字符注入（每字约 6 ms）。
- 多开启动靠「进程名 + 窗口标题里的沙箱名」匹配窗口，客户端改版换了标题格式需要同步调整。
- 文本同步只对**标准 Windows 文本控件**有效（原生 Edit / RichEdit、WinForms、VCL）。
  自绘输入框（游戏、`Chrome_RenderWidgetHostHWND` 这类）读不到文本，只能走剪贴板中转；
  这类控件会被识别出来并在日志里说明，不会被误当成「没有输入」。
- 文本同步靠轮询采样，发现文本变化后会先等 20ms 重读直到稳定（settle），把输入法组字、
  连打、粘贴的中间态合并掉；绝大多数情况只同步上屏结果，但内联组字的控件仍可能漏出中间态。
- 密码框（`ES_PASSWORD`）的 `WM_GETTEXT` 读到的是**明文**，星号只是显示效果，
  所以密码框里的内容同样会被镜像过去。介意的话把从窗口挪到看不见的地方。
- 窗口事件监听不到**进程被强杀**（`TerminateProcess` 不产生 `EVENT_OBJECT_DESTROY`），
  这种情况由投递路径的 `IsWindow` 兜底 + 看门狗的身份复查处理，最多晚 1 秒发现。

## 与原 Tauri 版的差异

| | 原版 | 本版 |
|---|---|---|
| 前端 | Tauri 2 + React（单文件 `App.tsx` 1033 行 + 35 KB CSS） | WinUI 3 / WindowsAppSDK 1.8 / .NET 8（MVVM 分层） |
| 后端 | `src-tauri/src/lib.rs`（Tauri 命令） | `core/src/lib.rs`（同一份逻辑，去掉 Tauri 依赖） |
| 通信 | Tauri IPC（`invoke` + `emit`） | C ABI：P/Invoke + JSON 信封 |
| 窗口 UI | 无边框自绘标题栏 | 自定义标题栏 + 自绘导航 + 迷你条 |
| 字体 | Google Fonts 远程加载 | 本地 Segoe UI / 微软雅黑 |
| 文件选择 | `tauri-plugin-dialog` | `FileOpenPicker` / `FileSavePicker` |

刻意修掉的行为差异：

1. **`update_sync_options` 会推送状态** —— 原版修改选项后不 `emit`，界面与引擎实际状态对不上。
2. **`SyncStatus` 补上 `sync_clipboard`** —— 原版状态结构缺这个字段。
3. 移除 182 KB 的 `qrcode_b64.ts` 死文件，二维码改用本地 PNG。
4. 移除未使用的 `tauri-plugin-shell`、`dragDropEnabled: []` 等空配置。
5. 进程列表可自动刷新，并自动剔除已关闭窗口的选择。

## 许可

`core/`（Rust 同步引擎）为 MIT；提示音资源见 `src/LeXiangSync/Assets/Sounds/LICENSE.txt`。
