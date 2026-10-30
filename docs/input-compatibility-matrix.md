# 输入兼容性测试矩阵

**测试日期：2026-10-30**　|　**测试方法：** 在本机起一个被当作「主窗口」的 WinForms 窗口，用 `SendInput` 合成**真实**键鼠事件触发核心的低级钩子，核心把事件转发给「从窗口」；从窗口逐条记录自己收到的窗口消息 / Raw Input / 文本变化（每条带 QPC 时间戳，跨进程可比），再按场景时间窗统计。

> **本文有两次实测，别混着看：**
> - **第一轮（§1–§3，修复前）** 记录的是「现象 + 为什么」，其中三格是真实缺陷：拖动不带 `MK_LBUTTON`、Ctrl+C 触发不了剪贴板中转、关开关会留卡键。
> - **第二轮（§2.1，修复后）** 是同一套脚手架对新 DLL 的复测，用来确认缺陷确实修掉、且没有把别的格子弄坏。
> - 第一轮的结论保留原样（它是缺陷的证据），现行状态看 §2.1 与 §附-2。

被测 DLL：`core/target/release/window_sync_core.dll` 复制到 `.workbuddy/tmp/compat/dll/` 后加载。

| 项 | 第一轮（修复前） | 第二轮（修复后） |
|---|---|---|
| SHA256 | `6CCE7813AC280A254E35399BC282934738AF1CFF92143354AFDDE356D290E4CF` | `7E4F2D282D55ABE6BCDFD8292DE79B6E55B521F7C356561C42E5A2045FFF6F9A` |
| 大小 | 664576 字节 | 675328 字节 |
| FileVersion | 无（Rust 产物未写版本资源） | 同左 |
| 复制时间 | 2026-10-30 19:43:54 | 2026-10-30 20:21 |

系统：Windows 11 Pro for Workstations，DPI 150%（目标窗口 DPI=144）。

> 本表所有数字都来自 `.workbuddy/tmp/compat/` 下的 `results.json` / `result-<role>.json` / `target-<role>.jsonl`，可重跑复现（见文末）。**没有实测到的格子一律写「未验证」，不做推断填充。**

---

## 1. 测试样本

| 角色 | 目标程序 | 窗口类 | 从窗口客户区 | 说明 |
|---|---|---|---|---|
| `win32` | `CompatTool target --role win32` | `CompatWnd32`（自建 `RegisterClassW`） | 978×564 | 自绘窗口，只统计消息，没有文本控件 |
| `winforms` | `CompatTool target --role winforms` | `WindowsForms10.Window...` + 子 `...Edit...` | 1000×620 | `TextBox`（Dock=Fill），统计 Form 层与子控件层两套消息 |
| `wpf` | `CompatTool target --role wpf` | `HwndWrapper[...]`（单 HWND） | 978×564 | WPF `TextBox`，用 `HwndSource.AddHook` 统计 |
| `rawinput` | `CompatTool target --role rawinput` | `CompatRawInput` | 978×564 | `RegisterRawInputDevices`(keyboard+mouse, `RIDEV_INPUTSINK`)，统计 `WM_INPUT` |
| `chrome` | 本机 Chrome（`Chrome_WidgetWin_1`）打开 `tools/input-compat/external/probe-page.html` | — | 958×749 | Chromium 内核，页内计数器 + textarea，代表 Electron / CEF 同源路径 |
| `mygame-x64r` | 本机「梦幻西游：时空」客户端（`MyGame_x64r`，5 个实例中取 1 个） | `Sandbox:pt:pnATE5hcsmBlYdfgj4Ho5eO` | 1269×713 | 沙箱类名的游戏客户端，疑似 DirectInput / 自绘引擎；只跑键盘 |

主窗口 `COMPAT-MASTER` 客户区 760×520，测试期间保持前台；另有一个 8ms 一次的前台采样器全程记录「当前前台是不是主窗口」，用来证明这一轮没被外部抢焦点（本轮四个目标共 36 个场景，`away` 全部为 0）。

**关于干扰：** 这台机器有人在正常用键鼠，真实输入会漏进场景窗口（一次移动场景曾经混进 1775 条 `WM_MOUSEMOVE`）。所以驱动给每次注入的移动都记了 QPC（`_moveMarks`），只有落在 mark ±50ms 内的移动才算「我方注入产生的」（`mousemove.attributed`），`run-compat.ps1` 再按「数据形状对不对」判脏并自动重跑（最多 3 轮）。下文所有格子的数字都来自判为干净的那一轮。

---

## 2. 矩阵（第一轮 · 修复前）

> 这一节是**修复前**的状态，保留下来作为缺陷证据。拖动列的 `MK` 恒 0、中文列的失败，都是当时真实存在的 bug；
> 修复后的同一套测试见 **§2.1**。

| 目标类型 | 键盘 | 鼠标移动 | 鼠标点击 | 滚轮 | 拖动 | 中文 |
|---|---|---|---|---|---|---|
| **Win32 自绘窗口** | ✅ `KEYDOWN×3/KEYUP×3/CHAR×3`，`chars="abc"` | ✅ 我方 3/3，坐标 154,98 / 824,466 / **489,282**（= 期望中心点） | ✅ `LBUTTONDOWN(wp=1)` + `LBUTTONUP(wp=0)` 各 1 条 | ✅ 2 条 `delta=120`，`wParam=0x780000` | ⚠️ 顺序对（DOWN→MOVE×8→UP），但 8 条移动 `wParam` 全是 0，**不带 MK_LBUTTON** | — 目标没有文本控件，两条中文路径都没有落点 |
| **WinForms TextBox** | ⚠️ 顶层 Form 收齐 `KEYDOWN×3/CHAR×3`（`chars="abc"`），但 `TextBox.Text` 前后都是空串，**字没进输入框** | ✅ 3/3，中心点实收 **500,310** = 期望 | ✅ 1/1 | ✅ 2 条 `delta=120` | ⚠️ 同上（`MK` 恒 0） | ⚠️ 剪贴板通道 0 字符；但 `text_mirror=true` 时 TextBox 被整段写成 **「中文测试」**（真进去了） |
| **WPF TextBox** | ✅ **没有一条 `WM_CHAR`，`TextBox.Text` 却变成 `"abc"`**（WPF 自己从 `WM_KEYDOWN` 生成文本） | ✅ 3/3，中心点 **489,282** | ✅ 1/1 | ✅ 2 条 `delta=120` | ⚠️ 同上 | ❌ `text_mirror` 无效（`"abca"` 前后不变），剪贴板通道 0 字符 |
| **Raw Input 监听窗口** | ✅ 消息层收齐 3+3+3；同内容用 `PostMessage` 直投时 **`WM_INPUT=0`**，而真实 `SendInput` 时 `WM_INPUT=6` | ✅ 3/3，且 `WM_INPUT=3` | ✅ 1/1，`WM_INPUT=2` | ✅ 2 条，`WM_INPUT=5` | ⚠️ 顺序对，`WM_INPUT=14`，但 `MK` 恒 0 | — 没有文本控件 |
| **Chrome / Chromium（Electron 同源）** | ✅ 页面 textarea 里出现 `"abc"`；页面计数 `keypress=3, textinput=3`（`keydown=5`） | — 未单独测（只随拖动一起推过，页面 `mousemove=29` 含人手噪声，无法归属） | ✅ 页面 `mousedown=1 / mouseup=1` | — 未测 | ⚠️ 按下/抬起到了页面，但 `dragstart=0 / drop=0`，没形成 HTML5 拖动 | — 未测 |
| **DirectInput 类游戏（梦幻西游：时空）** | ❌ 注入前后截图对比：游戏聊天输入框仍是占位提示「输入发送至当前」，没有出现任何字符 | — 未测 | — 未测 | — 未测 | — 刻意跳过（不在运行中的游戏里点鼠标） | — 未测 |

`✅` 通过　`⚠️` 部分成立 / 有已知缺陷　`❌` 不成立　`—` 不适用或本轮未验证

---

## 2.1 修复后的复测（第二轮）

同一套脚手架、同样的场景，只是把 DLL 换成修好之后的那一版。这一节只记「和第一轮不一样的地方」，
没提到的格子就是没变。

| 目标 | 拖动（`moveWithLeftButton`） | 中文 | 其它 |
|---|---|---|---|
| `win32` 自绘 | ✅ `DOWN → MOVE×323 → UP`，**带 `MK_LBUTTON` 323 条**（窗口内所有移动都算进来了，含人手噪声） | — 目标无文本控件，兜底通道跑了 5 次前台切换共 1658ms，没有落点 | 键盘 / 点击 / 滚轮同第一轮 |
| `winforms` | ✅ `DOWN → MOVE×8 → UP`，**8/8 带 `MK_LBUTTON`** | ✅ 文本 `+='中文测试'`，**切换前台 0 次共 0ms** —— 走的是新增的无焦点通道（`WM_CHAR` 直投子 `Edit`） | 键盘 3/3、字符 3、移动 3/3、点击 1/1、滚轮 2×120 不变 |
| `wpf` | ✅ `DOWN → MOVE×7 → UP`，**7/7 带 `MK_LBUTTON`** | ✅ 文本 `+='中文测试'`，切换前台 **1 次共 77ms** —— WPF 不吃 `WM_CHAR`，走的是前台切换兜底通道，注入后前台已还回主窗口 | 键盘仍是「无 `WM_CHAR` 但文本变」；文本镜像仍无出路（`+=''`） |
| `rawinput` | ✅ `DOWN → MOVE×8 → UP`，**8/8 带 `MK_LBUTTON`** | — 无文本控件，前台切换 1 次 76ms | `postonly` 对照仍然 **`WM_INPUT=0`** —— 这是机制边界，没变也不该变 |

对比第一轮：拖动那一列从 `MK 恒 0` 变成「每条移动都带 `MK_LBUTTON`」，而且顺序依旧是
`DOWN → MOVE… → UP`（顺序是第一轮就已正确的部分，这一轮没有被破坏）；中文剪贴板中转从
「完全不触发」变成「WinForms 无焦点写入、WPF 切前台写入」。

**关开关的卡键单独测过**：`tools/input-compat/test-option-release.ps1`，14 项断言全过 ——

```
按住 W → 从窗口收到 KEYDOWN → 关掉键盘同步 → 从窗口收到 KEYUP（增量 1，且排在 KEYDOWN 之后）
按住左键 → 从窗口收到 LBUTTONDOWN → 关掉鼠标同步 → 从窗口收到 LBUTTONUP（增量 1）
重新打开两个开关 → 按下/抬起仍能正常转发（状态表没有留脏数据）
```

---

## 3. 逐类展开

### 3.0 先说清这个工具到底往哪儿投

理解这张矩阵的关键是先分清三条互不相干的通道：

| 通道 | 实现 | 注入层级 | 触发条件 |
|---|---|---|---|
| 键盘 | `WH_KEYBOARD_LL` 钩子 → `PostMessage(WM_KEYDOWN/WM_KEYUP)` 给从窗口顶层 HWND（`core/src/lib.rs:518` `post_key`） | 窗口消息级 | 主窗口是前台 |
| 鼠标 | `WH_MOUSE_LL` 钩子 → `PostMessage` 移动/按键/滚轮（`core/src/lib.rs:821` `mouse_message`） | 窗口消息级 | 主窗口是前台 |
| 中文 | ① 剪贴板中转：Ctrl+C → 读剪贴板 → `inject_text()` 逐字 `SendInput(KEYEVENTF_UNICODE)`（`core/src/lib.rs:601`）<br>② 文本镜像：采样主窗口焦点控件文本 → 差分 → `PostMessage(WM_CHAR)` 或 `SendMessage(WM_SETTEXT)`（`core/src/text_sync/apply.rs:31/48`） | ① 前台输入队列级<br>② 窗口消息级 | ① 主窗口前台 + 检测到 Ctrl+C<br>② `text_mirror=true` + 主窗口前台 |

`SendInput` 是**前台输入队列级**注入，`PostMessage` 是**窗口消息级**注入 —— 这两者的差别决定了下面每一格的成败。

### 3.1 Win32 自绘窗口（`win32`）

- **目标程序：** 自建 `RegisterClassW` 窗口，`WndProc` 里统计 `WM_KEYDOWN/KEYUP/CHAR/LBUTTONDOWN/LBUTTONUP/MOUSEMOVE/MOUSEWHEEL/WM_INPUT`，消息循环里照常 `TranslateMessage`。
- **注入路径：** 键盘/鼠标都走钩子 → `PostMessage` 到顶层 HWND。
- **现象：**
  - 键盘：`WM_KEYDOWN×3`（vk `65,66,67`，lParam `1966081/3145729/3014657` = `scan<<16 | 1`）→ `TranslateMessage` 翻出 `WM_CHAR×3`（wp `97,98,99`）→ `chars="abc"`。
  - 移动：3 条，坐标 `154,98 / 824,466 / 489,282`；主窗口客户区 760×520 → 从窗口 978×564，主窗口中心 (380,260) 映射后应为 `489,282`，**实收 489,282**，比例映射正确。
  - 点击：`WM_LBUTTONDOWN` wp=1（含 `MK_LBUTTON`）、`WM_LBUTTONUP` wp=0，各 1 条，坐标 257,152。
  - 滚轮：2 条 `WM_MOUSEWHEEL`，`delta=120`、`wParam=0x00780000`，lParam 是**屏幕**坐标 `1218,322`，落在从窗口客户区 `[831,105]-[1809,669]` 内。
- **为什么：** 这就是 `PostMessage` 的标准适用面 —— 目标是一个老老实实跑消息循环、由 `DefWindowProc` + 自己的 `WndProc` 处理键鼠消息的普通窗口。

### 3.2 WinForms TextBox（`winforms`）

- **目标程序：** WinForms `Form`（`COMPAT-WINFORMS`）+ `Dock=Fill` 的 `TextBox`。Form 的 `WndProc` 统计消息；`TextBox` 用子类化的 `LoggingTextBox` 单独统计**子控件自己**收到的消息；另有一个 250ms 的定时快照记录 `Text`。
- **现象：**
  - 顶层 Form 收齐 `WM_KEYDOWN×3 + WM_CHAR×3`（`chars="abc"`），但 `TextBox.Text` 在场景前后都是空串 `""` → **字没进输入框**。
  - 补充对照 `childmsg`：把同样三条消息（`WM_KEYDOWN` + `WM_CHAR` + `WM_KEYUP`）**直接投给子控件**（`WindowsForms10.Edit.app...`，hwnd 0x2414DC），子控件收到 `[WM_KEYDOWN, WM_CHAR, WM_KEYUP, WM_CHAR]`，读回文本 `"xx"` —— 两个字：一条来自我们直投的 `WM_CHAR`，一条来自 `WM_KEYDOWN` 经过目标自己的 `TranslateMessage`。
- **为什么：** WinForms 的 `TextBox` 是一个**独立的子窗口**（Edit 类）。`PostMessage` 打在顶层 `Form` 上，消息只会进 `Form.WndProc`，WinForms 不会把它转发给子控件。所以「窗口消息级注入」对 WinForms 有效与否，取决于**投给的是哪个 HWND**：投给顶层窗口 → 消息到了但文本不动；投给文本控件本身 → 文本真的变。这也解释了为什么同一条通道对 WPF 有效（见 3.3）：WPF 只有 **一个** HWND。

### 3.3 WPF TextBox（`wpf`）

- **目标程序：** WPF `Window` + `TextBox`，用 `HwndSource.FromHwnd(hwnd).AddHook(...)` 统计消息。
- **现象：** 3 条 `WM_KEYDOWN`（vk 65/66/67）到达，**`WM_CHAR` 一条都没有**，但 `TextBox.Text` 从 `""` 变成 `"abc"`。
  - 反向验证：`postonly` 场景里往 WPF 窗口直投 `WM_KEYDOWN(vk=0x41)` + `WM_CHAR('A')`，之后文本多出一个小写 `"a"` —— 只有 `WM_KEYDOWN` 生效，`WM_CHAR` 被忽略。
- **为什么：** WPF 的输入不靠子窗口的 `WM_CHAR`：HwndSource 接到 `WM_KEYDOWN` 后走自己的 `TextCompositionManager` 生成文本。**推论（与实测一致）：只发 `WM_CHAR` 的注入方式对 WPF 无效** —— 这正是文本镜像在 WPF 上失败的原因（3.7）。

### 3.4 Raw Input 监听窗口（`rawinput`）—— 判据核心

- **目标程序：** `RegisterRawInputDevices`（keyboard `0x01/0x06`、mouse `0x01/0x02`，`RIDEV_INPUTSINK` 让窗口不在前台也能收），解析 `WM_INPUT` 记录设备类型与 `vkey/btn/dx/dy`。
- **现象（两张表对照）：**

| 场景 | 窗口消息层收到 | `WM_INPUT` |
|---|---|---|
| 真实 `SendInput` 键盘（A/B/C） | `KEYDOWN×3 / KEYUP×3 / CHAR×3` | **6**（3 down + 3 up） |
| 真实 `SendInput` 点击 | `LBUTTONDOWN/UP` 各 1 | **2** |
| 真实 `SendInput` 滚轮 | `MOUSEWHEEL×2` | **5** |
| 真实 `SendInput` 拖动 | DOWN → MOVE×8 → UP | **14** |
| **只 `PostMessage` 直投 7 条**（`KEYDOWN/KEYUP/CHAR/MOUSEMOVE/MOUSEWHEEL/LBUTTONDOWN/LBUTTONUP`） | 7 条全到 | **0**（`rawKbdA=0`、`rawMouseButtons=0`、纯移动 0） |

- **为什么：** `WM_INPUT` 由 Raw Input 栈在**驱动/输入队列**那一层产生，只有真实设备事件（以及 `SendInput` 这种走输入队列的注入）才会生成；`PostMessage` 只是往目标线程的消息队列塞一条消息，不经过输入栈。**这一格是本项目输入转发通道的适用边界所在。**

### 3.5 Chrome / Chromium（`chrome`，代表 Electron / CEF 同源路径）

- **目标程序：** 本机 Chrome，打开 `tools/input-compat/external/probe-page.html`（页内计数器 + textarea + 拖动区），窗口类 `Chrome_WidgetWin_1`，客户区 958×749。驱动把它 `SetWindowPos` 摆到主窗口右侧，挂进同步，跑键盘 + 拖动，然后截图。
- **现象（截图 `.workbuddy/tmp/compat/external-chrome.png`）：** textarea 里出现 `abc`；页面计数 `keydown=5, keypress=3, textinput=3, mousedown=1, mousemove=29, mouseup=1, wheel=0, dragstart=0, drop=0`。
  - `keypress=3 / textinput=3` 与注入的 A/B/C 一一对应；`keydown=5` 比 3 多 2 条，**来源没查清**（不是重试、也不是连发），如实记在这里。
  - `dragstart=0 / drop=0`：按下与抬起都到了页面，但浏览器没把它识别成 HTML5 拖动（`draggable` 元素需要真实的按住-移动序列且由浏览器自己发起 drag session）。
- **为什么：** Chromium 的顶层 HWND 自己处理 `WM_KEYDOWN/WM_CHAR` 并转发给渲染进程，所以窗口消息级注入能一路走到 DOM。**这也是「Electron 类程序大体能吃下这套注入」的依据** —— 但请注意，同一内核的 Electron 应用若自己接管了输入（游戏化渲染、自定义加速键），表现可能不同，本轮没有再单独验证。

### 3.6 DirectInput 类游戏（`mygame-x64r`）

- **目标程序：** 本机「梦幻西游：时空」客户端（`MyGame_x64r`，窗口类 `Sandbox:pt:pnATE5hcsmBlYdfgj4Ho5eO`），注入前/后各截一张图（`external-mygame-x64r-before.png` / `external-mygame-x64r.png`）。
- **注入路径：** 同键盘通道（钩子 → `PostMessage(WM_KEYDOWN/WM_KEYUP)` 到游戏顶层 HWND）。
- **现象：** 注入后游戏底部聊天输入框仍是占位提示「输入发送至当前……」，**没有出现任何字符**；画面其余部分无变化。
- **只跑了键盘，拖动刻意跳过：** 往一个正在运行的游戏里发鼠标点击，可能让角色真的移动/攻击，副作用收不回来。
- **为什么（推断，未验证）：** 这类客户端一般不用窗口消息取键，而是 DirectInput / Raw Input 轮询设备状态；`PostMessage` 不经过输入栈，游戏自然看不见（与 3.4 的 `WM_INPUT=0` 是同一件事的两面）。沙箱类名也提示它可能还有自己的输入代理。
- **结论：** 对这类目标，**窗口消息级注入不成立**；能否用别的办法（切前台 + `SendInput`）不在本工具的方案内，也不在本次测试范围内。

### 3.7 中文的两条路径（矩阵里最容易说错的一列）

中文没法靠键码转发：`WH_KEYBOARD_LL` 只拿得到虚拟键码，输入法组字后上屏的是 `WM_IME_COMPOSITION`/`WM_CHAR`，钩子看不见。所以核心有两条独立路径，本次分别测了：

**路径 A —— 剪贴板中转（Ctrl+C）：本轮不触发（实测）**

| 判据 | 结果 |
|---|---|
| 测试脚本的剪贴板是否真的置成了「中文测试」 | `winforms/wpf/rawinput` 三轮均 `clipboardReady=True`（`win32` 那轮写入失败，该轮不作数） |
| 从窗口在场景窗口内收到的消息 | `WM_KEYDOWN vk=162`, `WM_KEYDOWN vk=67`, `WM_CHAR wp=3`, `WM_KEYUP vk=67`, `WM_KEYUP vk=162` |
| 目标端文本 | 前后一致，`charsReceived=1`（那 1 条就是 Ctrl+C 自己翻出的 `WM_CHAR(0x03)`） |
| 核心日志有无「剪贴板中转」 | **无**（三个目标全部 `coreLoggedClipboard=false`） |

注意从窗口收到的 Ctrl 是 **vk=162（`VK_LCONTROL`）**，而当时 `core/src/lib.rs` 判的是 `vk == 0x11`（`VK_CONTROL`）。低级键盘钩子对 Ctrl/Alt/Shift 给的是**左右分开**的码（0xA2/0xA3、0xA0/0xA1、0xA4/0xA5），所以 `CTRL_DOWN` 永远不会被置上，Ctrl+C 分支进不去。

> **已在第二轮修复**：改成同时认 `0x11 / 0xA2 / 0xA3`，复测时三个目标的 `coreLoggedClipboard` 都变成真，中文真的进了从窗口（见 §2.1）。修复同时把注入拆成两条通道 —— 标准输入框走无焦点 `WM_CHAR`，自绘控件 / WPF / 游戏才切前台。

**路径 B —— 文本镜像（`text_mirror=true`）：取决于目标有没有「可读写文本的焦点控件」**

| 目标 | 结果 | 为什么 |
|---|---|---|
| WinForms TextBox | ✅ TextBox 从 `"xx"` 变成 `"中文测试"` | 差分算出「整段替换」→ `SendMessageTimeout(WM_SETTEXT)` + `EM_SETSEL`，跨进程由内核编组，写进去就能读回校验 |
| WPF TextBox | ❌ `"abca"` 前后不变 | 差分走的是增量分支 → `PostMessage(WM_CHAR)`；WPF 忽略 `WM_CHAR`（3.3 已验证），所以什么都没发生 |
| Win32 自绘 / Raw Input | — 无文本控件 | 没有落点，`textAfter=null` |

两条路径的差异要点：剪贴板中转走 `SendInput`（会**逐字** `SetForegroundWindow(从窗口)`，`core/src/lib.rs:607`，每字 25ms + 6ms）；文本镜像走 `PostMessage/SendMessage`，**不切前台**（本轮每个目标 9 个场景的前台采样 `away=0`、`leftMasterCount=0`）。

### 3.8 未验证的部分（本机没有样本或本轮未测）

| 项目 | 状态 | 拿到样本后该看什么 |
|---|---|---|
| Unity 打包程序 | 未验证 —— 本机进程列表里没有 Unity 打包的 exe | 优先看 `WM_INPUT` 是否随注入出现（Unity 默认走 Raw Input）；没有 `WM_INPUT` 就说明是消息级注入的死角 |
| Unreal 打包程序 | 未验证 —— 本机没有 UE 打包的 exe | 同上；另外看是否独占全屏（独占全屏时 `PostMessage` 基本无解） |
| Electron 打包应用（WorkBuddy / 哔哩哔哩） | 未验证 —— 只测了同内核的 Chrome | 同 Chrome：往一个可输入的文本框打字，看是否出现 `keypress/textinput` |
| 独占全屏 / DXGI 独占游戏 | 未验证 —— 没有样本，且这类窗口通常连 `WM_KEYDOWN` 都收不到 | 看窗口是否还能收到任何键鼠消息；收不到就属于「不在支持范围」 |
| 中文输入法**组字**（真按 IME 上屏） | 未验证 —— 脚手架为避免 IME 吃掉 `VK_PACKET`，进程内主动 `ImmDisableIME` + 锁 en-US 布局（这是**测试工具自己**的规避，不改被测 DLL） | 需要在开着微软拼音的机器上手工打中文，看 `text_mirror` 是否跟得上组字中间态 |
| Chrome 的移动/滚轮、游戏的鼠标 | 未验证 —— 外部程序的 harness 只跑了键盘 + 拖动，且游戏刻意跳过鼠标 | 用 `run-external.ps1` 的拖动项 + 页内 `wheel` 计数复测 |

---

## 4. 结论（能力边界）

1. **键盘/鼠标转发是「窗口消息级注入」。** 它把 `WM_KEYDOWN/WM_KEYUP`、`WM_MOUSEMOVE/LBUTTONDOWN/LBUTTONUP/MOUSEWHEEL` 直接投给从窗口的顶层 HWND。
   - 对**标准 Win32 窗口**有效：消息进了 `WndProc`，`TranslateMessage` 还能翻出 `WM_CHAR`。
   - 对 **WPF 有效**（单 HWND、自己从 `WM_KEYDOWN` 生成文本），但**只认 `WM_KEYDOWN`，不认 `WM_CHAR`**。
   - 对 **WinForms 只在「投给文本控件本身」时有效**：投给顶层 `Form`，消息到了但文本不会变（`TextBox` 是子窗口）。
   - 对 **Raw Input / DirectInput / 独占渲染的游戏不保证**：`postonly` 对照实测 `WM_INPUT=0`，游戏实测聊天框无字符。这类目标从输入栈取键，看不到窗口消息。

2. **`SendInput` 是「前台输入队列级注入」。** 它打到**当前前台窗口**，产生真实的 `WM_INPUT`（实测 6/2/5/14 条）。所以：
   - 它不是后台方案 —— 用 `SendInput` 同步到后台窗口，前提是那个窗口得先到前台（文本注入路径确实会 `SetForegroundWindow(从窗口)`）；本项目只有中文剪贴板中转用它。
   - 它不能、也不应该被当作绕过反作弊的手段：它产生的事件与真实设备同源，游戏/反作弊看到的就是「有人在前台按键」。

3. **不承诺兼容所有游戏。** 本轮实测已经把边界画出来了：同一套注入在 Win32/WPF/Chromium 上是通的，在一个沙箱类名的游戏客户端上是完全不通的。对没测过的目标（Unity/UE/独占全屏）只能按机制推断，不能按「应该也行」来承诺。

4. **中文不是一条路，是两条。** 剪贴板中转（Ctrl+C）在本构建里因为 Ctrl 键码判断不成立而**不会触发**；文本镜像对「有可读写焦点控件」的目标有效（WinForms ✅），对只认 `WM_KEYDOWN` 的目标无效（WPF ❌），对没有文本控件的目标没有落点。

---

## 5. 后续怎么补

| 拿到什么样本 | 改哪个变量/脚本 | 看什么判据 |
|---|---|---|
| Unity / Unreal 打包程序 | `pwsh tools/input-compat/run-external.ps1 -ProcessName <exe名> -NoDrag`（先只跑键盘，确认无副作用再放开拖动） | 注入后窗口内是否出现文本/焦点变化；配合 §3.4 的 `WM_INPUT` 判据判断它是否走 Raw Input |
| Electron 应用（WorkBuddy / 哔哩哔哩） | 同上；或把 `external/probe-page.html` 的思路搬进它自己的输入框 | 输入框里是否出现 `abc`；若不通，改测「切前台 + `SendInput`」 |
| 独占全屏游戏 | 同上，另加「窗口是否还能收到消息」的检查 | 收不到任何键鼠消息 → 归入不支持；不要写成「兼容」 |
| 开关中途关闭会不会卡键 | `pwsh tools/input-compat/test-option-release.ps1` | 14 项断言应全过：按下 → 关开关 → 从窗口必须收到抬起；重开后仍能正常转发 |
| Ctrl / 拖动这两条回归 | `run-compat.ps1 -Roles <目标>`，看 `chinese.coreLoggedClipboard` 与 `drag.moveWithLeftButton` | 前者应为真、后者应等于 DOWN/UP 之间的移动条数（已在 §2.1 复测通过） |
| 想验证「文本真的进去了」而不靠截图 | 用 `CompatTool target --role winforms` 的 `childmsg`/文本快照做法 | `textBefore/textAfter/addedText` 三个字段；`addedText` 为空串即「字没进去」 |

复现命令：

```powershell
# 四类本机目标（含干扰自动重跑）
pwsh -File tools/input-compat/run-compat.ps1

# Chromium 系程序
pwsh -File tools/input-compat/run-external.ps1

# 已经在跑的程序，只跑键盘
pwsh -File tools/input-compat/run-external.ps1 -ProcessName MyGame_x64r -NoDrag
```

---

## 附：和项目原有表述不一致的地方（实测/读码得到，附修复状态）

| # | 问题 | 现状 |
|---|---|---|
| 1 | **README「全程不抢焦点」说得太满** | ✅ 已改表述：鼠标键盘与标准输入框的文本都不抢焦点；自绘控件 / WPF / 游戏走剪贴板中继时会短暂切前台，注入完由 `ForegroundGuard` 还回去（实测 WPF 77ms、rawinput 76ms） |
| 2 | **拖动过程中 `WM_MOUSEMOVE` 的 `wParam` 恒 0，不带 `MK_LBUTTON`** | ✅ 已修：状态位随事件一起走（`Pos::buttons`），复测 8/8、7/7、323/323 条移动全都带 `MK_LBUTTON`（§2.1） |
| 3 | **Ctrl+C 剪贴板中转进不去**（判 `vk == 0x11`，钩子给 `0xA2`） | ✅ 已修：Ctrl / Shift 都按具体键码认（`0x11/0xA2/0xA3`、`0x10/0xA0/0xA1`），复测 `coreLoggedClipboard=true` |
| 4 | **WPF 的文本镜像没有出路**（增量分支只发 `WM_CHAR`，WPF 忽略它） | ⚠️ 未修：文本镜像仍走 `WM_CHAR`，WPF 上无效。但**剪贴板中转现在能覆盖 WPF** —— 它走前台切换通道，复测文本真的进去了。要让镜像也支持 WPF，得另开一条基于 `WM_KEYDOWN` 的写回路径，风险较大，先记在这里 |
| 5 | **WinForms 顶层窗口收不到子 `Edit` 的文本** | ⚠️ 结构性限制，未修：键鼠注入打的是从窗口顶层 HWND，`TextBox` 是子窗口，消息不会自动转发。剪贴板中转已经绕过它（`GetGUIThreadInfo` 找到焦点子控件直投 `WM_CHAR`），但纯键鼠转发仍然进不去 —— 要修就得在注入侧做「找子控件再投」的路由，是另一个量级的改动 |
