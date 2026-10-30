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
│       └── text_mirror.rs       # 中文文本镜像（剪贴板中转 + 逐字注入）
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
