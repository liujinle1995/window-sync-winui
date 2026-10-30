# 输入兼容性测试脚手架（tools/input-compat）

给 `window_sync_core.dll` 做「输入从主窗口转发到从窗口」的兼容性实测。
一行命令跑完四类本机目标，产出 `result-<role>.json`，再据此写 `docs/input-compatibility-matrix.md`。

```powershell
# 四类本机目标全跑（会自动构建）
pwsh -File tools/input-compat/run-compat.ps1

# 只跑某一类，且不重新构建
pwsh -File tools/input-compat/run-compat.ps1 -Roles winforms -NoBuild

# 指定别的 DLL（默认 core/target/release/window_sync_core.dll）
pwsh -File tools/input-compat/run-compat.ps1 -DllPath D:\somewhere\window_sync_core.dll

# Chromium 系程序（起浏览器打开探针页，跑键盘 + 拖动，出截图）
pwsh -File tools/input-compat/run-external.ps1

# 已经在跑的程序（游戏 / 桌面客户端），只跑键盘
pwsh -File tools/input-compat/run-external.ps1 -ProcessName MyGame_x64r -NoDrag

# 回归：运行中关掉同步选项不能留下卡键（独立脚本，不依赖上面那套）
pwsh -File tools/input-compat/test-option-release.ps1
```

产物默认落在 `.workbuddy/tmp/compat/`：`dll/`（DLL 副本）、`bin/`（构建产物）、
`target-<role>.jsonl`（目标端逐条消息日志）、`driver-<role>.log`（驱动侧日志，含核心日志回调）、
`result-<role>.json`（分析结果）、`results.json`（四类汇总）、`dll-info.json`（被测 DLL 的哈希与来源）、
`external-<role>.png`（第三方程序的注入后截图，`-NoDrag` 时还会先存一张 `-before.png`）。

## 文件

| 文件 | 用途 |
|---|---|
| `CompatTool/CompatTool.csproj` | 单个 net8.0-windows 工程，同时启用 WinForms + WPF |
| `CompatTool/Program.cs` | 入口：`target` / `driver` / `external` / `probe` / `shot`；进程启动时关 IME、锁英文键盘布局 |
| `CompatTool/Native.cs` | 全部 P/Invoke（SendInput、窗口、Raw Input、钩子、`WM_GETTEXT` 跨进程读文本）+ 消息解码 |
| `CompatTool/Targets.cs` | 四种目标窗口：Win32 自绘 / WinForms TextBox / WPF TextBox / Raw Input |
| `CompatTool/Driver.cs` | 主窗口 + 场景调度器 + 分析：把「目标端时间窗内收到的消息」数出来 |
| `CompatTool/Probe.cs` | 独立探针：不经过核心，直接看 SendInput 在低级钩子眼里是什么样 |
| `run-compat.ps1` | 构建 → 逐目标跑（判脏自动重跑）→ 汇总 |
| `run-external.ps1` | 第三方程序：起 Chrome 打开探针页，或直接接管已在运行的进程 |
| `test-option-release.ps1` | **回归用例**：运行中关掉键盘 / 鼠标同步，从窗口必须收到补投的抬起（卡键复现路径） |
| `external/probe-page.html` | 探针页：页内计数器（keydown/keypress/textinput/mousedown/mousemove/mouseup/wheel/dragstart/drop）+ textarea |

## 场景（`driver` 模式，按顺序）

| 场景 | 干什么 | 关键判据 |
|---|---|---|
| `keyboard` | `SendInput` 打 A/B/C | `WM_KEYDOWN/WM_KEYUP/WM_CHAR` 条数与 `chars`，以及文本控件的实际文本 |
| `mousemove` | 三个点 | `attributed`=3（只算落在注入时刻 ±50ms 内的），坐标是否落在客户区、比例映射是否正确 |
| `click` | 按下 + 抬起 | `LBUTTONDOWN(wp=1)` / `LBUTTONUP(wp=0)` |
| `wheel` | 2×120 | 条数、`delta`、`wParam`、lParam 是否是从窗口内的**屏幕**坐标 |
| `postonly` | **不产生任何真实输入**，只往从窗口 `PostMessage` 7 条消息 | 窗口消息到了、`WM_INPUT` 没到 → 证明「消息级注入到不了 Raw Input 那一层」 |
| `childmsg` | 把同样的消息直投给**最深的子窗口**（WinForms 的 Edit） | 子控件文本是否真的变化 → 区分「投错 HWND」和「这条路本身不通」 |
| `drag` | 按下后同一拍连推 3 次，再 5 次 30ms 间隔 | `DOWN → MOVE×n → UP` 顺序、`moveWithLeftButton` |
| `chinese` | 置剪贴板（重试到读回一致）→ Ctrl+C | `clipboardReady` / `coreLoggedClipboard` / 前台切换次数 |
| `mirror` | 清空主窗口输入框 → 新会话 `text_mirror=true` → 打「中文测试」 | 从窗口文本是否跟着变 |

## 为什么是 C# 而不是纯 PowerShell

钩子回调投递到**安装钩子的那个线程**，那个线程必须有消息泵。`wsync_start_sync` 装在调用线程上
（`core/src/session.rs` 的 `SetWindowsHookExA(..., None, 0)`），所以驱动必须是一个自己有消息循环的
进程。宿主本来就是这样（WinUI 的 UI 线程），脚手架照抄这个结构，用 WinForms 的消息泵 + 一个
15ms 的 `Timer` 驱动的状态机来跑场景。纯 PowerShell 里做同样的事要自己搭 `DoEvents` 循环，反而更脆。

## 必须知道的坑

1. **钩子只在「主窗口是前台」时转发。** 所以每个场景前都要 `SetForegroundWindow(master)`；
   `Driver` 里用 `AttachThreadInput` 硬抢前台（纯 `SetForegroundWindow` 经常被前台锁挡下）。
2. **`SendInput` 单独发滚轮会被丢掉。** 本机实测：一个 `MOUSEEVENTF_WHEEL` 前面没有紧跟移动事件时，
   事件连 `WH_MOUSE_LL` 都进不去，主窗口也收不到。`Native.Wheel()` 因此固定打包一个
   `MOUSEEVENTF_MOVE`（`dx=1`，光标已在原点时 `dx=0` 不产生移动），保证可复现。
3. **构建前必须杀掉残留的 `CompatTool.exe`。** 文件被占用时 `dotnet build` 会失败但不影响后续步骤，
   于是跑的是上一版二进制，结果看起来「莫名其妙对不上」。`run-compat.ps1` 已经先杀再构建。
4. **这台机器上有人在用键鼠，真实输入会漏进场景窗口。** 所以：移动用 `_moveMarks` 只认自己注入的
   那些；`postonly` 的判据只看「键盘 A 与鼠标按键」的 `WM_INPUT`（人手划鼠标只产生 `btn=0` 的纯移动）；
   `run-compat.ps1` 再按「数据形状对不对」判脏并最多重跑 3 轮。
5. **`Clipboard.SetText` 经常失败**（`CLIPBRD_E_CANT_OPEN`）。中文场景会重试 6 次并读回校验，
   `clipboardReady=false` 的那一轮，中文结论不能算数。
6. **IME 会吃掉 `KEYEVENTF_UNICODE`。** 开着微软拼音时，`VK_PACKET` 会被做成候选字（实测只落下「阿」）。
   脚手架在进程启动时 `ImmDisableIME` + `ActivateKeyboardLayout(en-US)` + 对目标 `ImmAssociateContext(..,0)`。
   注意：这是**测试工具自己**的规避，不改被测 DLL；真实用户环境里 IME 是开着的。
7. **Chrome 的窗口标题是「页面标题 - Google Chrome」**，拿页面标题 `FindWindowW` 是找不到的，
   `run-external.ps1` 取浏览器进程的 `MainWindowHandle` 再核标题前缀。同一个 `--user-data-dir`
   上残留实例没清掉时，新进程会把窗口交给旧实例然后自己退出。

## 坐标与时序

- 目标端每条事件都带 `Stopwatch.GetTimestamp()`（QPC）。QPC 是系统级计数器，驱动和目标两个进程
  读到的是同一个基准，所以场景窗口只要记 `[t0, t1]` 就能筛日志，不需要跨进程对时。
- 驱动给每个场景的时间窗前后各留 60ms / 250ms 余量，避免把边界上的消息漏掉。
- 鼠标坐标一律**物理像素**：进程按 PerMonitorV2 感知，`GetClientRect` 拿到的就是物理尺寸，
  和核心 `map_client_point` 用的坐标系一致。
- `shot` 模式只截图、不注入：`CompatTool shot --hwnd 0x1234 --path out.png`，用来给第三方程序留底片。

## 加一个新目标类型

`Targets.cs` 的 `Run()` 里加一个 `case`，写一个「建窗口 → 写 meta（角色/hwnd/客户区）→ 消息泵」的
方法即可；驱动侧不用改，`run-compat.ps1 -Roles <新名字>` 就能跑。第三方程序（Electron / Unity /
Unreal / 独占全屏游戏）没法用这个工程造，用 `run-external.ps1`；判据与「拿到样本后该看什么」见
`docs/input-compatibility-matrix.md` 第 5 节。
