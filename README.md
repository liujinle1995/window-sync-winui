# 乐享同步 · WinUI 3

Windows 多窗口输入同步工具。在主窗口里的鼠标点击、键盘输入、中文文本会实时镜像到一个或多个从窗口，
主号在打怪、小号在同一台机器上跟着动，不用来回切窗口。

鼠标与键盘**全程不抢焦点**：都是把窗口消息直接投给从窗口，主窗口始终是前台。
文本同步分两条通道 —— 标准输入框（`Edit` / `RichEdit` / WinForms / Scintilla）走无焦点的字符消息，
同样不抢焦点；而自绘控件、WPF、Electron 与游戏没有无焦点这条路，剪贴板中转会**短暂切到从窗口**
再用模拟输入，注入完自动把前台还回去。详见 [文本注入的两条通道](#文本注入的两条通道)。

本项目是原 Tauri 2 + React 版本的**前端重写**：界面按设计稿用 WinUI 3 重新实现，同步引擎（Rust + Win32）逐行保留，行为不变。

```
WinUI 3 (C#/XAML)  ──P/Invoke──►  window_sync_core.dll (Rust)  ──► Win32 低级钩子 / 输入注入
```

---

## 功能

| 页面 | 能做什么 |
|---|---|
| **同步** | 进程 → 窗口两级选择（进程树可折叠），主窗口单选、从窗口多选；勾选要镜像的项（鼠标 / 键盘 / 文本）；一键启停，保存为方案；**主从一键对调**（见下） |
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
│       ├── ffi.rs               # C ABI 导出层（JSON 控制面 + 二进制数据面）
│       ├── abi.rs               # ABI 版本 / 统一错误码 / POD 结构与二进制编码
│       ├── event_bus.rs         # 分级事件队列 + 按键状态表
│       ├── session.rs           # 同步会话（RAII）：钩子 / 线程 / 监听的整体启停与回滚
│       ├── window_id.rs         # 窗口身份模型（HWND/PID/启动时间/EXE/类名/父窗/客户区/DPI）
│       ├── window_watch.rs      # 窗口生命周期监听（SetWinEventHook，事件驱动）
│       └── text_sync/           # 文本同步
│           ├── mod.rs           #   调度器：身份 / 基线 / 分派 / 校验重试
│           ├── control.rs       #   焦点控件身份与能力识别、读文本与光标
│           ├── diff.rs          #   通用单区间差分（UTF-16 安全）
│           └── apply.rs         #   三条写回通道：增量 / 退格 / 整段重写
├── scripts/build-core.ps1       # 单独构建 Rust 核心
├── docs/
│   └── input-compatibility-matrix.md   # 各类目标程序的注入兼容性实测矩阵
├── tools/input-compat/          # 兼容性测试脚手架（四类目标窗口 + 场景驱动）
└── src/LeXiangSync/
    ├── Native/                  # P/Invoke 封装（SyncNative / NativeAbi 结构体与二进制解析 / Win32 / WindowApi）
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

| 事件 | 丢弃策略 | 合并策略 |
|---|---|---|
| 鼠标移动 | 绝不丢，可被更新的位置覆盖 | 只与**紧邻的上一条**移动合并 |
| 滚轮 | 净额不为 0 就一定要投 | 只与**紧邻的上一条**滚轮合并 |
| 键盘按下 / 抬起 | 绝不丢 | 不合并 |
| 鼠标按下 / 抬起 | 绝不丢 | 不合并 |
| 文本同步 | 独立队列 + 独立线程，注入再慢也不挡鼠标键盘 | — |
| 状态通知 | 按内容去重，同一状态不重复推给 UI | — |

早期实现是 `sync_channel(2048)` + `try_send`：队列一满就**静默丢事件**。鼠标移动瞬间就能把它灌满，
紧跟着的「抬起」被扔掉，从窗口于是永远以为键还按着 —— 角色自己往前走、鼠标一直拖着。
把容量从 2048 抬到 8 万只是把概率推后，问题还在。

### 跨类型顺序：一条队列，不是一个类型一个槽

「分级」曾经做成三个槽：`latest_move` / `wheel` / `reliable`，取事件时**固定**按
「先移动、再滚轮、最后可靠事件」组装。这个顺序和用户实际操作的顺序无关：

```
真实操作：Down → Move → Move → Up        （按住左键拖一下）
从队列出来：Move(最后位置) → Down → Up    ← 拖拽语义被彻底打乱
```

从窗口看到的是「先移过去、再按下去、马上抬起」，于是拖动失效、或者只挪一下就断。
现在只有一条 `queue`，顺序就是入队顺序；合并只发生在**相邻**的同类事件之间 ——
`Move, Move, Move` 合成一条（位置取最后），中间隔了 `Down` 就绝不跨过去合并，
否则 `Down` 前后的两次移动会被揉成一次，拖动轨迹依然错。

批次边界也不会改变顺序：取的是队列的一个前缀，上一批末尾是 `Down`，下一批开头就是它之后那条移动。

配套的按键状态表（`event_bus::PressedState`）记录哪些键 / 鼠标键还按着，在三个时机补投抬起：

1. **停止同步** —— 排进可靠队列，保证排在已入队的按下事件之后投出去，然后关队列、等线程排空才返回；
2. **主窗口失焦** —— 钩子只在主窗口是前台时转发，用户 Alt+Tab 走开后抬起消息就没人转发了，
   不主动放就是永久卡键。除了钩子里的检查，还有一个 30ms 跳一次的看门狗兜底（用户可能一个输入都不再产生）；
3. **从窗口失效** —— 从同步列表剔除，不再往已销毁的窗口投递。

代码：`core/src/event_bus.rs`（队列与状态表，纯逻辑，单测覆盖）、`core/src/lib.rs`（钩子采集、投递、生命周期）。

### 鼠标消息的 `wParam` 不是空的

`WM_MOUSEMOVE` 的 `wParam` 必须带按键状态位：`MK_LBUTTON` / `MK_RBUTTON` / `MK_MBUTTON` /
`MK_SHIFT` / `MK_CONTROL`。早期实现把它固定成 `0`，于是靠标准消息判拖动的程序收到的是一串
「没按住任何键的移动」—— 它们不会去查 `GetKeyState`，只信 `wParam`，拖动要么不生效、要么只挪一下。
滚轮消息的低 16 位同理。

状态位随事件一起走（存在 `Pos::buttons` 里），**不是投递时现取**：队列里可能压着几条移动，
等它们被投出去时用户早松手了，现取会把拖动中的移动全标成「没按键」。

按下与抬起的 `wParam` 按事件本身的语义补一位 / 去一位：按下时它还没进状态表，要 `|` 上；
抬起时还在表里，要 `&!` 掉 —— 而且只动自己那一位，不能把同时按着的另一个键抹掉。

## 关掉同步选项时会发生什么

`update_sync_options` 不是「改三个布尔值」。只改开关会留下一个必现的卡键：

```
用户按下 W → 从窗口收到 KeyDown → 用户关掉键盘同步 → 用户松开 W
                                                      ↓
                          钩子看到开关是 false，这次抬起既没转发、也没记账
                                                      ↓
                                     从窗口永远认为 W 还按着
```

鼠标键完全同理：拖拽途中关掉鼠标同步，从窗口会一直拖着不放。现在改成一次显式的状态转换：

| 步骤 | 做什么 | 为什么是这个顺序 |
|---|---|---|
| ① | 先改开关 | 之后进来的抬起一律不转发，也就不会再制造新的「按下没人放」 |
| ② | **在锁内**取出已按下的键 / 鼠标键 | 与钩子的「读开关 + 记账 + 入队」互斥，否则钩子能在取出之后再记一笔按下 |
| ③ | 补抬起走队列 | 队列是 FIFO，补抬起必然排在已入队的按下之后；顺序反了等于没补 |
| ④ | 只放被关掉的那一类 | 用户可能正拖着画面去关键盘同步，顺手放掉鼠标会让他一直拖着 |

第 ② 步的锁（`OPTIONS_LOCK`）是必需的：没有它，钩子可能读到 `true` → 变更线程置 `false` 并取空状态、
投出抬起 → 钩子这才记下按下并入队，结果**按下排在抬起之后**，从窗口卡死。

另一个容易漏的点：**即使不转发，钩子也要继续记账**。关掉键盘同步后按下/松开的键仍然进出状态表，
否则下一次关开关时根本没有状态可放。补一个从没转发过的抬起对接收方无害，漏一个抬起的代价是永久卡键。

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

### 文本注入的两条通道

文本进从窗口有两条路，**能力边界完全不同**，所以在设置页里也明确写出来，不藏在实现里：

| 通道 | 目标 | 做法 | 代价 |
|---|---|---|---|
| 无焦点 | 标准 `Edit` / `RichEdit` / WinForms / VCL / Scintilla | `GetGUIThreadInfo` 找到焦点控件 → 直接投 `WM_CHAR` | 不抢前台，不打断用户 |
| 前台切换 | 自绘控件、WPF、Electron、游戏 | `SetForegroundWindow` + 逐字 `SendInput` | 会短暂切走前台；输入打到「当前前台」而不是指定窗口 |

为什么不做成一种：

- 对标准输入框用 `SendInput` 是**没必要的打断** —— 字符消息就能写进去；
- 反过来说，自绘控件和游戏只认真实输入，字符消息它们一条都不吃（实测：WPF 的 `TextBox`
  文本是靠它自己的 `WM_KEYDOWN` 生成的，`WM_CHAR` 发过去毫无反应），只能切前台。

几个必须踩过的细节：

1. **字符消息要用 `PostMessageW`**。用 `A` 版会把 `WM_CHAR` 的字符按 ANSI 转换一遍，
   `0x4E2D`（中）被截成 `0x2D`（-），中文全变乱码。
2. **只投 `WM_CHAR`，不补 `WM_KEYDOWN`**。标准 Edit 收到字符消息就会插入文本，
   再补按下消息，目标自己的 `TranslateMessage` 会**又**生成一次字符 —— 实测多打一个字。
3. **焦点必须靠 RAII 还**。注入过程中用户点了停止，函数会在循环里提前 `return`；
   手写的「结尾切回主窗口」根本走不到，前台就留在最后一个从窗口上了。
   现在用 `ForegroundGuard`，任何提前返回都会把前台还给进来时那个窗口。
4. **Ctrl 要认具体键码**。低级键盘钩子给的是 `VK_LCONTROL` / `VK_RCONTROL`（`0xA2` / `0xA3`），
   只比对通用的 `VK_CONTROL`（`0x11`）会漏 —— 这就是「Ctrl+C 触发不了剪贴板中转」的实际原因。

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

### 还有一层：窗口实例代次

上面四项能挡住「换进程」，挡不住**同一个进程里的窗口重建 + 句柄复用**：

```
同一个游戏进程 → 原窗口销毁 → 新窗口创建 → 系统把原来的 HWND 值发下来
PID、启动时间、EXE、类名  →  一个字节都没变
```

这时候 `WindowKey::matches()` 会说「还是它」，于是继续往一个已经换人的窗口投键鼠消息。
所以身份被拆成两个模型：

```rust
struct ProcessKey { pid, process_start_time, exe_path }   // 这个进程还是原来那个吗
struct WindowKey { process: ProcessKey, class_name, generation }  // 这个窗口实例还是原来那个吗
```

`generation` 不看窗口长什么样，只数**这个句柄值上销毁过几次**：`EVENT_OBJECT_DESTROY`
一到就 `+1`（`window_id::note_instance_destroyed`），捕获时记下当时的值。窗口一直活着就恒等，
销毁过就一定不等 —— 这一项**不通配**，因为它是我们自己数出来的，`0` 就是实实在在的第一代。

用销毁而不是创建推进：创建事件会在重建流程、隐藏的辅助窗口、消息框里反复冒出来，
拿它推进会把一个活得好好的窗口算成新一代，身份校验就会把用户正在用的窗口无故判掉。

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

## 主从对调

多开时经常要「换一个号来操作」：刚才在 A 上带 B、C，现在想换到 B 上带 A、C。
老的「设为主窗口」做不到这件事——它只是把 B 设成主窗口，A 直接掉回「未加入」，
用户得重新勾一遍从窗口。对调则是**互换角色、列表不变**：B 升为主窗口，A 转为从窗口，
C 的序号也不动。

入口有三个，都指向同一个 `SetupViewModel.SwapMasterCommand`：

- 从窗口行「角色」列右侧的小图标（双向箭头），一步到位；
- 行右键菜单「与主窗口对调」；
- 主窗口行不出现这个按钮——对调没有意义，也避免误点。

### 运行中切换，三件事一件都不能少

同步跑着的时候换主窗口，危险在于「状态换了一半」比「不换」更糟。`swap_master()`
在 `ROLE_LOCK` 里按固定顺序做完这三步：

| # | 动作 | 漏了会怎样 |
|---|---|---|
| ① | `PRESSED.drain()` 后**直接** `post_release()` | 投递读的是「当前从窗口列表」。排进队列的抬起会在列表换完之后才被消费，那时它投给的已经是新列表——旧主窗口变成从窗口后就漏掉这次抬起，**永久卡键** |
| ② | `capture_identities()` 整体重建身份键 | 一秒后的看门狗还在用旧键，会把刚升上来的新主窗口判成「句柄被系统复用」，**直接把同步停掉** |
| ③ | `text_sync::reset_baseline()` + `LAST_POS` 清零 | 基线绑的是旧主窗口焦点控件的内容与光标。不丢的话下一次采样把新窗口的整段文本当成「在旧内容上大改」，然后整段重写灌给从窗口，**把从窗口的内容一起冲掉** |

第 ① 步的 `post_release` 是**绕过队列直投**的，不排队：排队就意味着「晚一点再抬起」，
而不排队才能保证它一定落在旧列表还有效的时候。

### 为什么需要一把 `ROLE_LOCK`

对调要改两样东西（身份键、`MASTER_HWND` + `SLAVES`），改的过程中必然有一瞬间两边对不上。
看门狗（`verify_targets`，约 1 秒一拍）正好落在那一瞬间，就会把新主窗口判成失效。
`ROLE_LOCK` 把「对调」和「身份复查」串起来，谁也别在对方改到一半时插进来。

锁内只做读写判断，**处置一律在锁外**：失效处置会走 `stop_sync()`，那要 join 线程，
持着锁等线程退出，而线程可能正等着这把锁——死锁。

### 错误路径

`swap_master()` 先分清「状态不对」和「参数不对」，别让用户拿参数错误去排查状态：

| 情况 | 返回 |
|---|---|
| 没在同步 | `NOT_RUNNING`「同步尚未启动，请先开始同步」 |
| 句柄为 0 / 就是当前主窗口 / 不在从窗口列表里 | `INVALID_ARG`，各带具体原因 |
| 宿主侧的 C# 内存里这一行已经不是从窗口 | `SwapMaster` 返回 `false`，UI 记一条警告并重刷标记 |

判「在不在同步」只看 `SYNC_ACTIVE`。`MASTER_HWND` 在 `stop_sync()` 之后是**故意保留**的
（宿主靠它恢复上次的选择），拿它非零当「正在跑」会一路走到列表检查，
最后报出一个「该窗口不是从窗口」——那是参数问题，不是状态问题。

宿主侧的顺序也定死了：**先通知引擎、再改本地选择**。反过来的话，引擎那一步失败时
UI 已经换完角色，而真正在投递的还是旧窗口——界面和现实对不上。

## 同步会话：一次启动拥有的全部资源

`start_sync()` 要同时管六样东西：两级低级钩子、事件总线、三条消费线程、文本镜像线程、
窗口监听线程、以及一组全局状态（主窗口 / 从窗口列表 / 身份键 / 按键表）。老实现是
**先起线程、再装钩子**，两条钩子都用 `?` 直接返回：

```text
start_input_worker / start_text_worker / start_focus_watch   ← 线程已经在跑
SetWindowsHookExA(键盘)                                      ← 成功
SetWindowsHookExA(鼠标)?                                     ← 失败，直接 return Err
```

后果不是「报了个错」那么轻：进程里留着三条无人引用的线程、一条装着事件的队列、
一把还没摘的键盘钩子，以及一套看起来仍然有效的全局状态。用户再点一次启动，上一套就成了
孤儿——它们的 `SYNC_ACTIVE` 读到的是**新会话**的值，会跟着新会话一起往从窗口投事件。

现在整个会话是一个结构体，字段就是资源清单：

```rust
SyncSession
├── KeyboardHook        // Drop 里 UnhookWindowsHookEx
├── MouseHook           // 同上
├── EventWorker         // 总线 + 输入投递 / 文本注入 / 失焦看门狗三条线程
├── TextMirrorWorker    // text_sync 的采样线程
├── ClipboardHandler    // 剪贴板哈希（Ctrl+C 走键盘钩子，无独立线程）
└── WindowMonitor       // window_watch 的监听线程
```

**每个字段自己负责释放**，所以 `open()` 里任何一步 `?` 返回，已经建好的部分都会自动拆掉。
调用方拿到 `Err` 时，进程状态和「从来没启动过」完全一致——这一点由
`session_tests.rs` 里的三条失败注入用例守着（键盘钩子失败 / 鼠标钩子失败 / 线程启动失败，
各自断言钩子数、总线、线程、全局状态全部归零）。

### 两条顺序

启动：① 验证所有窗口 → ② 创建资源 → ③ 安装钩子 → ④ 启动工作线程 → ⑤ 全部成功才进 Active

停止：① 禁止接收新事件 → ② 释放所有按键状态 → ③ 卸载钩子 → ④ 停止工作线程 → ⑤ 等待线程退出 → ⑥ 清理窗口状态

两处顺序是有讲究的：

- **钩子排在 Active 之前**。钩子回调第一行就看 `SYNC_ACTIVE`，标志没置起来之前它们一律空转，
  所以「装好钩子但还没 Active」这段窗口里不会有半成品事件漏进队列。
- **「先禁止、后释放」**。停止时先落 `SYNC_ACTIVE = false`，再补投抬起，最后才卸钩子；
  反过来做的话，补抬起之后仍可能有排队的 `KeyDown` 落地，从窗口照样卡键。

「验证所有窗口」也不再只是查非零：句柄必须真的还在（`IsWindow`），从窗口不能是主窗口自己
（否则形成自投递回环，打一个字进去出一串）。校验失败时还什么都没创建，直接返回即可。

### 文本镜像线程的代次管理

`text_sync` 原来的 `RUNNING` 是一个被三处共同修改的布尔量（`spawn` 置 true、`shutdown` 置 false、
`set_enabled` 关闭时只唤醒），快速「停 → 启」会出两种错乱：旧线程还在收尾时 `spawn()` 看到
`RUNNING` 仍是 true 就提前返回，新会话从此一个采样都不做（静默失效，最难查）；或者新会话已经
把 `RUNNING` 置回 true，旧线程醒来误以为自己是当前线程，接着跑一轮把刚清空的基线又写脏。

现在「谁在跑」只看 `WORKER` 里那一条记录：每条记录自带**代次**与**停止标志**，线程每轮先问一句
`still_current(generation, stop)`，不是就退；启停全程持同一把锁，任何时刻最多一条采样线程。
`shutdown()` 用 `fetch_max` 把代次推到比它还大，并在锁内 join + `reset_state()` 才算返回——
「退出时明确回收状态」而不是把清理留给下一次启动。

## 通信架构：控制面走 JSON，数据面走二进制

全走 JSON 开发最省事，但高频那几项是在替序列化库打工：Rust 侧拼字符串分配，
C# 侧 `JsonDocument.Parse` 再解析一遍，中间还夹着一堆 `\uXXXX` 转义。
所以按**频率**分成两条通道：

| 通信内容 | 通道 | 为什么 |
|---|---|---|
| 启停同步 / 改配置 / 进程管理 / 聚焦 | JSON 信封 | 低频，可读、好调试、加字段不破坏兼容 |
| 实时输入事件（鼠标键盘） | **Rust 内部通道**，不过 FFI | 每帧几十条；C# 只负责启停 |
| 窗口列表刷新 | **二进制定长记录 + 字符串区** | 几百个窗口，省掉「序列化 + 解析 + 一堆小字符串」 |
| 高频状态 | **POD 快照 + 无载荷通知** | 通知只带一个 revision，宿主自己来拉快照 |
| 大段文本 | **UTF-8 缓冲区**（调用方自备内存） | 几千字不必转义、不必二次分配 |

### 控制面：JSON 信封 + 统一错误码

```json
{ "ok": true,  "data": { } }
{ "ok": false, "code": "INVALID_ARG", "code_value": 1, "error": "…" }
```

`code` 是给程序看的稳定标识，`error` 是给人看的中文说明——文案随便改，宿主不用跟着调整匹配逻辑。
码表在 `core/src/abi.rs` 的 `Code`：`INVALID_ARG` / `INVALID_JSON` / `NOT_RUNNING` /
`NO_MASTER` / `WINDOW_GONE` / `BUSY` / `UNSUPPORTED` / `BUFFER_TOO_SMALL` / `INTERNAL`。

**参数校验在边界上做**：`master_hwnd == 0`、从窗口为空 / 含 0 / 含主窗口、三个开关全关、
从窗口超过 16 个、`pid == 0`、空路径，都在进引擎之前拦下来，报错能精确到字段。
二进制接口没法返回信封，约定是**返回 `i32` 错误码（0 = 成功）**，
需要细节时调 `wsync_last_error()` 拿一条 JSON。

### 数据面之一：二进制窗口列表

```
[header 20B][record × count][UTF-8 字符串区]
 header = magic('WSW1', u32) | abi(u32) | count(u32) | record_size(u32) | string_bytes(u32)
 record = hwnd(i64) | process_start_time(u64) | parent_hwnd(i64) | pid(u32) | dpi(u32)
          client_w/height(i32×2) | visible(u32) | (exe|class|title)_(off,len)(u32×6)
```

定长记录才能被 C# 直接 `MemoryMarshal.Cast` 成结构体数组，字符串放块尾按 `(offset, len)` 引用。
头部里带 `record_size`，宿主用自己的 `Marshal.SizeOf` 对一遍，对不上就明确报错——
宁可显式失败，也不要按错位的偏移读出一堆垃圾。

### 数据面之二：POD 状态快照 + 无载荷通知

```c
wsync_status_snapshot()   // 按值返回 32 字节结构体，零分配零解析
wsync_status_revision()   // 只读版本号
wsync_set_status_notifier(fn(u64))  // 回调只给 revision
```

以前每次状态变化都要序列化一份 `SyncStatus` JSON 塞给回调，宿主再反序列化。
现在回调只传一个 `u64`，宿主比对一下自己渲染过的版本号，要刷新就去拉一次 POD 快照。
**连续变化十次，宿主也只需要拉最后一次**——这就是「批量通知」，而且不需要用时间门限
去合并（延迟门限会丢掉尾沿状态，反而让 UI 停在中间态）。
旧的 JSON 回调 `wsync_set_status_callback` 保留，给还在用它的宿主。

### 数据面之三：文本缓冲区

```c
int wsync_text_read(long hwnd, uint8_t* buf, uint32_t cap, uint32_t* out_len);
int wsync_text_write(long hwnd, const uint8_t* buf, uint32_t len);
```

`cap = 0` 时返回 `BUFFER_TOO_SMALL` 并在 `out_len` 里给出**所需字节数**，宿主按这个值扩容再调一次——
不用猜大小、也不用 Rust 侧代为分配。UTF-8 直接传，中文和 emoji 都不需要转义。

### 版本、回调生命周期与 panic

| 入口 | 说明 |
|---|---|
| `wsync_abi_version()` | 二进制布局版本。只有 `#[repr(C)]` 结构变化才 +1，JSON 加字段不用动 |
| `wsync_core_version()` | 核心版本，静态字符串，**不需要释放** |
| `wsync_panic_mode()` | 0 = unwind，1 = abort（宿主可据此告警） |
| `wsync_shutdown()` | 停同步 + 摘全部回调，宿主退出前必须调 |

回调注册都返回 **token**，注销要带同一个 token：

```c
uint64_t t = wsync_set_status_notifier(cb);   // 换回调时可能重新注册
wsync_clear_status_notifier(t);               // 旧 token 注销不掉新装的
```

注销用 token 而不是裸指针，是因为宿主会在换窗口 / 重连时重装回调，
拿旧 token 去注销一旦把新装的那个顺手清掉，就是「回调莫名不来了」。
另外回调是**先出锁再调用**的——原先是 `if let Some(cb) = *SLOT.lock()`，
临时 guard 会活到 if-let 结束，宿主回调里只要再回调进来一次就是自己锁自己。

### panic 与构建配置

`ffi.rs` 每个导出都套了 `catch_unwind`，但这**只在 `panic = "unwind"` 时才有意义**。
`Cargo.toml` 的 release profile 因此显式写成 `panic = "unwind"`：

```toml
[profile.release]
# panic = "abort" 会让 catch_unwind 变成摆设，而且一次 panic 直接带走整个宿主进程
panic = "unwind"
```

这个 DLL 是被 WinUI 3 进程加载的，一次 panic 就是用户「程序凭空消失」。
换成 unwind 后，`catch_unwind` 把 panic 变成 `INTERNAL` 错误码，宿主还能弹个提示继续活着。
代价是二进制略大一点。宿主启动时会调 `wsync_panic_mode()` 自查，万一有人改回 `abort`，
日志里会有一条警告，而不是等用户遇到「闪退」再猜。

## 原生互操作契约

| Rust 导出 | 说明 |
|---|---|
| `wsync_get_processes` / `wsync_get_windows` | 枚举进程 / 窗口（JSON） |
| `wsync_get_windows_bin` | 窗口列表（二进制，`wsync_free_bytes` 释放） |
| `wsync_get_sync_status` / `wsync_status_snapshot` | 状态（JSON / POD） |
| `wsync_start_sync` / `wsync_stop_sync` | 启停同步 |
| `wsync_update_sync_options` | 运行中修改同步项 |
| `wsync_focus_window` | 聚焦并还原窗口 |
| `wsync_launch_app` / `wsync_kill_process` / `wsync_kill_process_by_name` | 进程管理 |
| `wsync_text_read` / `wsync_text_write` | 大段文本（UTF-8 缓冲区） |
| `wsync_set_status_callback` / `wsync_set_status_notifier` / `wsync_set_log_callback` | 注册回调（返回 token） |
| `wsync_clear_status_callback` / `wsync_clear_status_notifier` / `wsync_clear_log_callback` | 按 token 注销 |
| `wsync_free` / `wsync_free_bytes` | 释放字符串 / 二进制块 |
| `wsync_shutdown` | 停同步 + 摘回调 |

约定：输入为 UTF-8 JSON；返回字符串由 Rust 用 `CString::into_raw` 分配，**必须**用 `wsync_free` 释放
（`Native/SyncNative.cs` 已在 `finally` 里处理）；二进制块用 `wsync_free_bytes(ptr, len)`，长度要原样传回。

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
- `start_sync` 会校验每个窗口当前是否真的还在（`IsWindow`），已经不存在的窗口直接报
  `WINDOW_GONE`。宿主需要把这条错误当成「选择过期了」，提示用户重新选，而不是重试即可。
- **输入注入的兼容性有天花板**。键鼠走 `PostMessage`，属于「窗口消息级注入」：对标准
  Win32 / WinForms / WPF / Scintilla 有效，对 DirectInput、Raw Input、独占渲染的游戏
  不保证 —— 那些程序读的是驱动层输入，窗口消息到不了。`SendInput` 也不是通用后台方案，
  它打的是**当前前台输入队列**，既不能定点到后台窗口，也不是绕过反作弊的手段。
  每类程序实测到什么程度，见 [输入兼容性矩阵](docs/input-compatibility-matrix.md)。
- 多开客户端**互不干扰**的前提是每个从窗口各自收消息；如果某个客户端在进程内部
  自己读原始输入设备，投上去的消息它根本不看，这条同步链路对它不成立。

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
