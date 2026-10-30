using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CompatTool
{
    /// <summary>
    /// 驱动：开一个主窗口（COMPAT-MASTER），把目标窗口挂进 wsync，然后用 SendInput 造真实输入，
    /// 最后把「目标端在场景时间窗内实际收到了什么」从目标进程的 JSONL 日志里数出来。
    /// </summary>
    internal static class Driver
    {
        private const string DllName = "window_sync_core";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wsync_start_sync(IntPtr configJson);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wsync_stop_sync();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void wsync_free(IntPtr s);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Utf8Callback(IntPtr utf8);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong wsync_set_log_callback(Utf8Callback cb);

        private static Utf8Callback _logCb; // 必须留住委托，否则回调打到已回收的闭包上

        private static string _dllPath;
        private static string _outDir;
        private static string _role;

        private static Form _master;
        private static IntPtr _masterHwnd;
        private static Size _masterClient;

        private static Process _target;
        private static Dictionary<string, JsonElement> _slaveMeta;
        private static long _slaveHwnd;
        /// <summary>从窗口里最深的子窗口（WinForms 的 TextBox 是 Form 下的 Edit），给 childmsg 用。</summary>
        private static IntPtr _childHwnd;
        /// <summary>childmsg 结束后从子控件读回来的文本（WM_GETTEXT，跨进程由内核封送）。</summary>
        private static string _childText;
        /// <summary>
        /// 我们每次 SendInput 移动时记下的 QPC。这台机器上人手会往场景窗口里掺真实的
        /// WM_MOUSEMOVE，只有「落在某个 mark ±50ms 内」的那些才是我们注入产生的 ——
        /// 用它算 attributed，就不用因为几十条噪声把整轮作废。
        /// </summary>
        private static readonly List<long> _moveMarks = new();
        private static int _slaveW, _slaveH, _slaveOriginX, _slaveOriginY;

        private static readonly Dictionary<string, long[]> Windows = new();
        private static readonly List<(long qpc, long hwnd)> FgSamples = new();
        private static CancellationTokenSource _fgCts;
        private static readonly StringBuilder Trace = new();

        private static Scheduler _sched;
        private static bool _hookLog;
        private static readonly long PadBefore = MsToQpc(60);
        private static readonly long PadAfter = MsToQpc(250);

        private static string TargetLogPath => Path.Combine(_outDir, "target-" + _role + ".jsonl");
        private static string ResultPath => Path.Combine(_outDir, "result-" + _role + ".json");
        private static string TracePath => Path.Combine(_outDir, "driver-" + _role + ".log");
        private static string MetaPath => Path.Combine(_outDir, "target-" + _role + ".meta.json");

        private static long MsToQpc(int ms) => (long)(Stopwatch.Frequency * (ms / 1000.0));

        public static int Run(Args a)
        {
            _role = a.Get("role", "win32");
            _outDir = Path.GetFullPath(a.Get("out", ".workbuddy/tmp/compat"));
            _dllPath = Path.GetFullPath(a.Get("dll", "window_sync_core.dll"));
            Directory.CreateDirectory(_outDir);

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Driver).Assembly, (name, asm, path) =>
                    name == DllName ? NativeLibrary.Load(_dllPath) : IntPtr.Zero);
            }
            catch (InvalidOperationException)
            {
                // 同一个进程里重复注册会抛，忽略即可
            }

            Log($"dll={_dllPath}");
            Log($"role={_role}");
            KillLeftovers();

            var wa = Screen.PrimaryScreen.WorkingArea;
            int masterW = Math.Min(760, Math.Max(480, wa.Width / 3));
            int masterH = Math.Min(520, Math.Max(360, wa.Height - 120));
            int tx = wa.Left + masterW + 60;
            int tw = Math.Max(420, Math.Min(1000, wa.Width - (tx - wa.Left) - 40));
            int th = Math.Max(320, Math.Min(620, wa.Height - 120));

            if (!StartTarget(tx, wa.Top + 60, tw, th))
            {
                StopTarget();
                File.WriteAllText(TracePath, Trace.ToString());
                return 1;
            }

            _masterClient = new Size(masterW, masterH);
            BuildMaster(wa.Left + 20, wa.Top + 20, masterW, masterH);
            RegisterLogCallback();

            if (a.Get("hooklog") == "true") _hookLog = true;

            _sched = new Scheduler();
            BuildScenarios();
            _sched.Finished += () =>
            {
                try
                {
                    Analyze();
                }
                catch (Exception ex)
                {
                    Log("分析失败: " + ex);
                }
                finally
                {
                    _master.BeginInvoke(new Action(() => _master.Close()));
                }
            };

            var timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (_, _) => { _sched.Tick(); if (_sched.IsFinished) timer.Stop(); };
            timer.Start();

            try
            {
                Application.Run(_master);
            }
            finally
            {
                StopTarget();
                File.WriteAllText(TracePath, Trace.ToString());
            }
            return File.Exists(ResultPath) ? 0 : 3;
        }

        // ── 外部程序（Electron / 浏览器 / 游戏等）──────────────────────────
        //
        // 外部程序不写我们的 JSONL 日志，所以判据改成「截图 + 页内计数器」：
        // 页面自己统计 keydown / mousedown / mousemove / mouseup，跑完把窗口客户区截图下来，
        // 人眼看一眼就知道消息有没有被它的输入管线吃下去。
        public static int RunExternal(Args a)
        {
            _role = a.Get("role", "external");
            _outDir = Path.GetFullPath(a.Get("out", ".workbuddy/tmp/compat"));
            _dllPath = Path.GetFullPath(a.Get("dll", "window_sync_core.dll"));
            _slaveHwnd = long.Parse(a.Get("hwnd"));
            Directory.CreateDirectory(_outDir);

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Driver).Assembly, (name, asm, path) =>
                    name == DllName ? NativeLibrary.Load(_dllPath) : IntPtr.Zero);
            }
            catch (InvalidOperationException) { }

            if (_slaveHwnd == 0 || !Native.IsWindow((IntPtr)_slaveHwnd))
            {
                Log("外部窗口句柄无效: " + _slaveHwnd);
                File.WriteAllText(TracePath, Trace.ToString());
                return 2;
            }

            // 摆到主窗口右边，别互相挡着（截图要能看见从窗口）
            Native.SetWindowPos((IntPtr)_slaveHwnd, IntPtr.Zero, 820, 60, 980, 760, 0x0040 /*SWP_SHOWWINDOW*/);
            Native.GetClientRect((IntPtr)_slaveHwnd, out var cr);
            var origin = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen((IntPtr)_slaveHwnd, ref origin);
            _slaveW = cr.Right - cr.Left;
            _slaveH = cr.Bottom - cr.Top;
            _slaveOriginX = origin.X;
            _slaveOriginY = origin.Y;
            Log($"外部窗口 0x{_slaveHwnd:X} client={_slaveW}x{_slaveH} origin={_slaveOriginX},{_slaveOriginY} title={Native.WindowTitle((IntPtr)_slaveHwnd)} class={Native.WindowClass((IntPtr)_slaveHwnd)}");

            _masterClient = new Size(760, 520);
            BuildMaster(20, 20, 760, 520);
            RegisterLogCallback();

            _sched = new Scheduler();
            _sched.Add(() => ForceForeground());
            _sched.WaitUntil(() =>
            {
                if (Native.GetForegroundWindow() != _masterHwnd) ForceForeground();
                return Native.GetForegroundWindow() == _masterHwnd;
            }, 5000);
            _sched.Add(() => StartSync(false));
            _sched.Add(StartFgSampler);
            _sched.Wait(700);

            _sched.Add(() => Begin("keyboard"));
            _sched.Add(() => Native.KeyPress(0x41));
            _sched.Wait(80);
            _sched.Add(() => Native.KeyPress(0x42));
            _sched.Wait(80);
            _sched.Add(() => Native.KeyPress(0x43));
            _sched.Wait(600);
            _sched.Add(() => End("keyboard"));

            // 拖动：按下 → 连续移动 → 抬起
            // --nodrag true 时整段跳过：往正在运行的游戏里发点击可能让角色真的动起来，
            // 那种副作用是收不回来的，所以对游戏样本只跑键盘。
            bool noDrag = a.Get("nodrag", "false") == "true";
            if (!noDrag)
            {
                _sched.Add(() => MoveClient(220, 180));
                _sched.Wait(250);
                _sched.Add(() => Begin("drag"));
                _sched.Add(() =>
                {
                    Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
                    MoveClient(260, 210);
                    MoveClient(300, 240);
                    MoveClient(340, 270);
                });
                for (int i = 4; i <= 7; i++)
                {
                    int px = 220 + i * 40, py = 180 + i * 30;
                    _sched.Add(() => MoveClient(px, py));
                    _sched.Wait(30);
                }
                _sched.Add(() => Native.MouseButton(Native.MOUSEEVENTF_LEFTUP));
                _sched.Wait(600);
                _sched.Add(() => End("drag"));
            }

            _sched.Add(() => { Log("stop_sync: " + Call(wsync_stop_sync())); });
            _sched.Wait(300);
            _sched.Add(StopFgSampler);
            _sched.Add(() =>
            {
                string shot = Path.Combine(_outDir, "external-" + _role + ".png");
                try { CaptureWindow(shot); Log("截图: " + shot); } catch (Exception ex) { Log("截图失败: " + ex.Message); }
            });

            _sched.Finished += () =>
            {
                var fg = Windows.ToDictionary(kv => kv.Key, kv =>
                {
                    List<(long qpc, long hwnd)> all;
                    lock (FgSamples) all = FgSamples.ToList();
                    var inWin = all.Where(s => s.qpc >= kv.Value[0] && s.qpc <= kv.Value[1]).ToList();
                    return (object)new { samples = inWin.Count, away = inWin.Count(s => s.hwnd != (long)_masterHwnd) };
                });

                var result = new Dictionary<string, object>
                {
                    ["role"] = _role,
                    ["mode"] = "external",
                    ["dragSkipped"] = noDrag,
                    ["generatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["dllPath"] = _dllPath,
                    ["dllSha256"] = TryHash(_dllPath),
                    ["slave"] = new Dictionary<string, object>
                    {
                        ["hwnd"] = _slaveHwnd,
                        ["title"] = Native.WindowTitle((IntPtr)_slaveHwnd),
                        ["className"] = Native.WindowClass((IntPtr)_slaveHwnd),
                        ["clientW"] = _slaveW,
                        ["clientH"] = _slaveH,
                    },
                    ["foregroundCheck"] = fg,
                    ["screenshot"] = Path.Combine(_outDir, "external-" + _role + ".png"),
                };
                File.WriteAllText(Path.Combine(_outDir, "result-" + _role + ".json"),
                    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                _master.BeginInvoke(new Action(() => _master.Close()));
            };

            var timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (_, _) => { _sched.Tick(); if (_sched.IsFinished) timer.Stop(); };
            timer.Start();

            try { Application.Run(_master); }
            finally { File.WriteAllText(TracePath, Trace.ToString()); }
            return 0;
        }

        private static void CaptureWindow(string path)
        {
            var rect = new Rectangle(_slaveOriginX, _slaveOriginY, _slaveW, _slaveH);
            using var bmp = new Bitmap(rect.Width, rect.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        // ── 主窗口 ───────────────────────────────────────────────────────
        private sealed class MasterForm : Form
        {
            public readonly TextBox Input;

            public MasterForm()
            {
                Text = "COMPAT-MASTER";
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(238, 244, 252);

                var label = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 120,
                    Padding = new Padding(20, 16, 20, 8),
                    Font = new Font("Microsoft YaHei UI", 10f),
                    Text = "COMPAT-MASTER —— 被当作「主窗口」的窗口。\r\n测试期间它是前台，所有输入都由 SendInput 合成真实键鼠事件。\r\n下面的输入框用于「文本镜像」场景（往里打字，看从窗口是否跟着出现同样的字）。",
                };

                Input = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    Font = new Font("Consolas", 14f),
                    ScrollBars = ScrollBars.Vertical,
                };

                Controls.Add(Input);
                Controls.Add(label);
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_MOUSEWHEEL:
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_LBUTTONUP:
                        Log($"master 收到 {Native.MessageName((uint)m.Msg)} wp=0x{(long)m.WParam:X} lp={(long)m.LParam}");
                        break;
                }
                base.WndProc(ref m);
            }
        }

        private static MasterForm _masterTyped;

        private static void BuildMaster(int x, int y, int w, int h)
        {
            _masterTyped = new MasterForm
            {
                Location = new Point(x, y),
                ClientSize = new Size(w, h),
            };
            _master = _masterTyped;
            _master.Show();
            _masterHwnd = _master.Handle;
            _masterTyped.Input.Focus();
            Log($"master hwnd=0x{_masterHwnd:X} client={w}x{h}");
        }

        /// <summary>把核心自己的日志接进来：剪贴板中转、补投抬起这些都只在核心日志里有记录。</summary>
        private static void RegisterLogCallback()
        {
            _logCb = p =>
            {
                try { Log("[core] " + (Marshal.PtrToStringUTF8(p) ?? "")); } catch { }
            };
            try { wsync_set_log_callback(_logCb); } catch (Exception ex) { Log("注册日志回调失败: " + ex.Message); }
        }

        // ── 目标进程 ─────────────────────────────────────────────────────
        private static bool StartTarget(int x, int y, int w, int h)
        {
            if (File.Exists(MetaPath)) File.Delete(MetaPath);
            if (File.Exists(TargetLogPath)) File.Delete(TargetLogPath);

            var exe = Environment.ProcessPath;
            var args = $"target --role {_role} --log \"{TargetLogPath}\" --meta \"{MetaPath}\" --pos {x},{y} --size {w},{h}";
            _target = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                if (File.Exists(MetaPath))
                {
                    try
                    {
                        var json = File.ReadAllText(MetaPath);
                        if (json.TrimEnd().EndsWith("}"))
                        {
                            using var doc = JsonDocument.Parse(json);
                            _slaveMeta = doc.RootElement.EnumerateObject()
                                .ToDictionary(p => p.Name, p => p.Value.Clone());
                            _slaveHwnd = _slaveMeta["hwnd"].GetInt64();
                            _slaveW = _slaveMeta["clientW"].GetInt32();
                            _slaveH = _slaveMeta["clientH"].GetInt32();
                            _slaveOriginX = _slaveMeta["clientOriginX"].GetInt32();
                            _slaveOriginY = _slaveMeta["clientOriginY"].GetInt32();
                            Log($"target 0x{_slaveHwnd:X} client={_slaveW}x{_slaveH} origin={_slaveOriginX},{_slaveOriginY}");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("meta 读取重试: " + ex.Message);
                    }
                }
                Thread.Sleep(100);
            }
            Log("目标窗口启动超时");
            return false;
        }

        private static void StopTarget()
        {
            try
            {
                if (_target != null && !_target.HasExited)
                {
                    _target.Kill(true);
                    _target.WaitForExit(3000);
                }
            }
            catch { /* 目标自己退出也算正常 */ }
        }

        /// <summary>清掉上一轮可能残留的目标进程（测试中断时会出现），避免窗口叠在一起。</summary>
        private static void KillLeftovers()
        {
            foreach (var p in Process.GetProcessesByName("CompatTool"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(true); } catch { }
            }
        }

        // ── 场景 ─────────────────────────────────────────────────────────
        private static void BuildScenarios()
        {
            _sched.Add(() => ForceForeground());
            _sched.WaitUntil(() =>
            {
                if (Native.GetForegroundWindow() != _masterHwnd) ForceForeground();
                return Native.GetForegroundWindow() == _masterHwnd;
            }, 5000);
            _sched.Add(() => Log("主窗口是否前台: " + (Native.GetForegroundWindow() == _masterHwnd)));
            _sched.Add(() => StartSync(false));
            // 全程采样前台窗口：有人在旁边动鼠标把焦点抢走时，数据会失真，
            // 分析阶段按场景统计「有多少拍前台不在主窗口」，用来自证这一轮是否可信。
            _sched.Add(StartFgSampler);
            // 调试钩子必须装在核心钩子之后：后装的先被调用，才能看出「事件到底进没进钩子链」
            if (_hookLog) _sched.Add(() => Probe.AttachInlineMouseHook(Log));
            _sched.Wait(500);

            // ① 键盘
            _sched.Add(() => Begin("keyboard"));
            _sched.Add(() => Native.KeyPress(0x41)); // A
            _sched.Wait(80);
            _sched.Add(() => Native.KeyPress(0x42)); // B
            _sched.Wait(80);
            _sched.Add(() => Native.KeyPress(0x43)); // C
            _sched.Wait(500);
            _sched.Add(() => End("keyboard"));
            _sched.Wait(300);

            // ② 鼠标移动：按主窗口客户区比例落点
            _sched.Add(() => Begin("mousemove"));
            _sched.Add(() => MoveClient(120, 90));
            _sched.Wait(120);
            _sched.Add(() => MoveClient(_masterClient.Width - 120, _masterClient.Height - 90));
            _sched.Wait(120);
            _sched.Add(() => MoveClient(_masterClient.Width / 2, _masterClient.Height / 2));
            _sched.Wait(500);
            _sched.Add(() => End("mousemove"));
            _sched.Wait(300);

            // ③ 单击
            _sched.Add(() => MoveClient(200, 140));
            _sched.Wait(250);
            _sched.Add(() => Begin("click"));
            _sched.Add(() => Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN));
            _sched.Wait(90);
            _sched.Add(() => Native.MouseButton(Native.MOUSEEVENTF_LEFTUP));
            _sched.Wait(400);
            _sched.Add(() => End("click"));
            _sched.Wait(300);

            // ④ 滚轮：移动和滚轮放在同一拍里，避免中途被真人鼠标挪走光标
            _sched.Add(() => Begin("wheel"));
            _sched.Add(() =>
            {
                MoveClient(300, 200);
                Native.GetCursorPos(out var cp);
                Log($"wheel 前光标=({cp.X},{cp.Y}) master客户区原点=({MasterClientOrigin().X},{MasterClientOrigin().Y})");
                Log("wheel#1 SendInput=" + Native.Wheel(120));
            });
            _sched.Wait(150);
            _sched.Add(() => Log("wheel#2 SendInput=" + Native.Wheel(120)));
            _sched.Wait(500);
            _sched.Add(() => End("wheel"));
            _sched.Wait(300);

            // ④b 对照：完全不产生真实输入，只往从窗口 PostMessage 四条消息。
            //     判据是「窗口消息到了、WM_INPUT 没到」—— 证明 PostMessage 只到窗口消息层，
            //     到不了 Raw Input 那一层。
            _sched.Add(() => Begin("postonly"));
            _sched.Add(() =>
            {
                Log("postonly: 前台=0x" + Native.GetForegroundWindow().ToString("X") + " master=0x" + _masterHwnd.ToString("X"));
                var lp = (IntPtr)((200 << 16) | 150);
                bool a = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_KEYDOWN, (IntPtr)0x41, (IntPtr)0x1E0001);
                bool b = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_KEYUP, (IntPtr)0x41, (IntPtr)unchecked((int)0xC01E0001));
                bool c = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_MOUSEMOVE, IntPtr.Zero, lp);
                bool d = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_MOUSEWHEEL, (IntPtr)0x00780000, lp);
                bool e = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_LBUTTONDOWN, (IntPtr)0x0001, lp);
                bool f = Native.PostMessageW((IntPtr)_slaveHwnd, (uint)Native.WM_LBUTTONUP, IntPtr.Zero, lp);
                Log($"postonly 直投 6 条 -> keydown={a} keyup={b} move={c} wheel={d} ldown={e} lup={f}");
            });
            _sched.Wait(500);
            _sched.Add(() => End("postonly"));
            _sched.Wait(300);

            // ④c 补充对照：把同样的消息直接投给**子控件**（WinForms 的 TextBox 是 Form 下的 Edit）。
            //     用来区分两件事：「投到顶层窗口进不了文本控件」和「PostMessage 这条路径本身进不了文本」。
            //     窗口没有子控件（win32 自绘 / WPF 单 HWND）时这一步只记录一句「无子控件」。
            _sched.Add(() => Begin("childmsg"));
            _sched.Add(() =>
            {
                var child = Native.DeepestChild((IntPtr)_slaveHwnd);
                _childHwnd = child.ToInt64() == _slaveHwnd ? IntPtr.Zero : child;
                Log($"childmsg: 从窗口 0x{_slaveHwnd:X} 最深子窗口=0x{child.ToInt64():X} class={Native.ClassOf(child)}");
                if (_childHwnd != IntPtr.Zero)
                {
                    var lp = (IntPtr)0x001E0001;
                    Native.PostMessageW(_childHwnd, (uint)Native.WM_KEYDOWN, (IntPtr)0x58, lp);
                    Native.PostMessageW(_childHwnd, (uint)Native.WM_CHAR, (IntPtr)'x', lp);
                    Native.PostMessageW(_childHwnd, (uint)Native.WM_KEYUP, (IntPtr)0x58, (IntPtr)unchecked((int)0xC01E0001));
                }
            });
            _sched.Wait(600);
            _sched.Add(() => _childText = Native.ReadText(_childHwnd));
            _sched.Add(() => End("childmsg"));
            _sched.Wait(300);

            // ⑤ 拖动：按下之后**同一拍里**立刻开始移动，不等待 —— 真实拖动就是这个节奏，
            //    也是「可靠队列 vs 移动合并」那条已知顺序缺陷最容易暴露的时机
            _sched.Add(() => MoveClient(200, 160));
            _sched.Wait(300);
            _sched.Add(() => Begin("drag"));
            _sched.Add(() =>
            {
                Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
                MoveClient(225, 172);
                MoveClient(250, 184);
                MoveClient(275, 196);
            });
            _sched.Wait(30);
            for (int i = 4; i <= 8; i++)
            {
                int px = 200 + i * 25;
                int py = 160 + i * 12;
                _sched.Add(() => MoveClient(px, py));
                _sched.Wait(30);
            }
            _sched.Add(() => Native.MouseButton(Native.MOUSEEVENTF_LEFTUP));
            _sched.Wait(600);
            _sched.Add(() => End("drag"));
            _sched.Wait(300);

            // ⑥ 中文：剪贴板中转路径（Ctrl+C 触发）
            // 剪贴板经常被别的进程占着（实测会遇到 CLIPBRD_E_CANT_OPEN），写失败就等于这一轮
            // 的判据不成立 —— 所以要重试到读回来确实是目标文本，失败也要在日志里说清楚。
            _sched.Add(() =>
            {
                for (int i = 1; i <= 6; i++)
                {
                    try
                    {
                        Clipboard.SetText("中文测试");
                        if (Clipboard.ContainsText() && Clipboard.GetText() == "中文测试")
                        {
                            Log($"剪贴板已置为「中文测试」（第 {i} 次成功）");
                            return;
                        }
                        Log($"剪贴板写入后读回不一致（第 {i} 次）");
                    }
                    catch (Exception ex) { Log($"剪贴板写入第 {i} 次失败: {ex.Message}"); }
                    System.Threading.Thread.Sleep(150);
                }
                Log("剪贴板写入最终失败 —— 本场景的中文判据不成立");
            });
            _sched.Add(() => ForceForeground());
            _sched.Wait(400);
            _sched.Add(() => Begin("chinese"));
            _sched.Add(() => Native.KeyDown(0x11)); // Ctrl
            _sched.Wait(40);
            _sched.Add(() => Native.KeyPress(0x43)); // C
            _sched.Wait(40);
            _sched.Add(() => Native.KeyDown(0x11, up: true));
            _sched.Wait(1800);
            _sched.Add(() => End("chinese"));

            // ⑦ 文本镜像：换一套会话（text_mirror=true），往主窗口的输入框里打中文
            //    先把输入框清空再开会话：基线是空串时，打进去的就是「末尾追加」，
            //    文本镜像才会走增量通道；否则它会因为「改动不在末尾」拒绝盲投。
            _sched.Add(() => _masterTyped.Input.Clear());
            _sched.Wait(200);
            _sched.Add(() => Log("stop_sync: " + Call(wsync_stop_sync())));
            _sched.Wait(500);
            _sched.Add(() => StartSync(true));
            _sched.Wait(700);
            _sched.Add(() =>
            {
                ForceForeground();
            });
            _sched.Wait(250);
            _sched.Add(() =>
            {
                _masterTyped.Activate();
                _masterTyped.Input.Focus();
                Log("mirror: 主窗口焦点控件=0x" + Native.GetFocus().ToString("X") +
                    " textbox hwnd=0x" + _masterTyped.Input.Handle.ToString("X") +
                    " activeControl=" + (_masterTyped.ActiveControl?.GetType().Name ?? "null"));
            });
            _sched.Wait(250);
            _sched.Add(() => Begin("mirror"));
            _sched.Add(() =>
            {
                Native.TypeUnicode("中文测试");
                Log("已向主窗口输入「中文测试」");
            });
            _sched.Wait(4000);
            _sched.Add(() => End("mirror"));
            _sched.Add(() => Log("master 输入框文本 = " + _masterTyped.Input.Text));

            _sched.Add(() => Log("stop_sync: " + Call(wsync_stop_sync())));
            _sched.Wait(400);
            _sched.Add(StopFgSampler);
        }

        // ── 输入注入 ─────────────────────────────────────────────────────
        private static void MoveClient(int cx, int cy)
        {
            // 主窗口客户区 → 屏幕 → SendInput 绝对移动
            var pt = new Native.POINT { X = cx, Y = cy };
            Native.ClientToScreen(_masterHwnd, ref pt);
            _moveMarks.Add(Logger.Qpc());
            Native.MoveTo(pt.X, pt.Y);
            Log($"move client=({cx},{cy}) screen=({pt.X},{pt.Y})");
        }

        private static Native.POINT MasterClientOrigin()
        {
            var pt = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen(_masterHwnd, ref pt);
            return pt;
        }

        private static void ForceForeground()
        {
            bool ok = Native.ForceForeground(_masterHwnd);
            Log($"ForceForeground -> {ok} (fg=0x{Native.GetForegroundWindow():X})");
        }

        private static void StartSync(bool textMirror = false)
        {
            long slave = _slaveHwnd;
            var cfg = new
            {
                master_hwnd = (long)_masterHwnd,
                slave_hwnds = new[] { slave },
                sync_mouse = true,
                sync_keyboard = true,
                sync_clipboard = true,
                text_mirror = textMirror,
            };
            string json = JsonSerializer.Serialize(cfg);
            Log("start_sync " + json);
            var res = Call(wsync_start_sync(StrPtr(json)));
            Log("start_sync -> " + res);
        }

        private static IntPtr StrPtr(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\0");
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        private static string Call(IntPtr p)
        {
            if (p == IntPtr.Zero) return "<null>";
            try { return Marshal.PtrToStringUTF8(p) ?? ""; }
            finally { wsync_free(p); }
        }

        // ── 时间窗 ───────────────────────────────────────────────────────
        private static void Begin(string name)
        {
            Windows[name] = new[] { Logger.Qpc() - PadBefore, 0 };
            Log($"BEGIN {name}");
        }

        private static void End(string name)
        {
            Windows[name][1] = Logger.Qpc() + PadAfter;
            Log($"END {name}");
        }

        // ── 前台采样（全程）──────────────────────────────────────────────
        private static void StartFgSampler()
        {
            _fgCts = new CancellationTokenSource();
            var cts = _fgCts;
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    lock (FgSamples)
                    {
                        FgSamples.Add((Logger.Qpc(), (long)Native.GetForegroundWindow()));
                    }
                    await Task.Delay(8);
                }
            });
        }

        private static void StopFgSampler()
        {
            try { _fgCts?.Cancel(); } catch { }
        }

        // ── 分析 ─────────────────────────────────────────────────────────
        private static void Analyze()
        {
            var events = LoadEvents(TargetLogPath);
            Log($"target 日志 {events.Count} 行");

            var result = new Dictionary<string, object>
            {
                ["role"] = _role,
                ["generatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["dllPath"] = _dllPath,
                ["dllSha256"] = TryHash(_dllPath),
                ["master"] = new Dictionary<string, object>
                {
                    ["hwnd"] = (long)_masterHwnd,
                    ["clientW"] = _masterClient.Width,
                    ["clientH"] = _masterClient.Height,
                },
                ["slave"] = _slaveMeta,
                ["windows"] = Windows.ToDictionary(kv => kv.Key, kv => new { start = kv.Value[0], end = kv.Value[1] }),
                ["scenarios"] = new Dictionary<string, object>(),
            };

            var scenarios = (Dictionary<string, object>)result["scenarios"];

            // 每个场景里「前台不在主窗口」的采样数：> 0 说明这一轮被外部抢过焦点，数字要打折看
            result["foregroundCheck"] = Windows.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    List<(long qpc, long hwnd)> all;
                    lock (FgSamples) all = FgSamples.ToList();
                    var inWin = all.Where(s => s.qpc >= kv.Value[0] && s.qpc <= kv.Value[1]).ToList();
                    return (object)new
                    {
                        samples = inWin.Count,
                        away = inWin.Count(s => s.hwnd != (long)_masterHwnd),
                    };
                });

            scenarios["keyboard"] = AnalyzeKeyboard(events);
            scenarios["mousemove"] = AnalyzeMove(events);
            scenarios["click"] = AnalyzeClick(events);
            scenarios["wheel"] = AnalyzeWheel(events);
            scenarios["drag"] = AnalyzeDrag(events);
            scenarios["chinese"] = AnalyzeChinese(events);
            scenarios["mirror"] = AnalyzeMirror(events);
            var postEvs = InWindow(events, "postonly").Where(e => S(e, "ev") == "msg").ToList();
            var postRaw = InWindow(events, "postonly").Where(e => S(e, "ev") == "raw").ToList();
            scenarios["postonly"] = new Dictionary<string, object>
            {
                ["types"] = postEvs.Select(Name).Distinct().ToList(),
                ["counts"] = postEvs.GroupBy(Name).ToDictionary(g => g.Key, g => g.Count()),
                // 判据只看「与直投内容对应的那几类 Raw Input」：
                // 人手动一下鼠标会带来一堆 WM_INPUT（btn=0 的纯移动），那是环境噪声，不是直投产生的
                ["rawTotal"] = postRaw.Count,
                ["rawKbd"] = postRaw.Count(e => S(e, "ri") == "kbd"),
                ["rawKbdA"] = postRaw.Count(e => S(e, "ri") == "kbd" && L(e, "vkey") == 0x41),
                ["rawMouseButtons"] = postRaw.Count(e => S(e, "ri") == "mouse" && L(e, "btn") != 0),
                ["rawMouseMoves"] = postRaw.Count(e => S(e, "ri") == "mouse" && L(e, "btn") == 0),
            };
            scenarios["childmsg"] = new Dictionary<string, object>
            {
                ["childHwnd"] = _childHwnd == IntPtr.Zero ? null : "0x" + _childHwnd.ToInt64().ToString("X"),
                ["childClass"] = _childHwnd == IntPtr.Zero ? null : Native.ClassOf(_childHwnd),
                ["targetedChild"] = _childHwnd != IntPtr.Zero,
                ["messagesAtChild"] = InWindow(events, "childmsg")
                    .Where(e => S(e, "ev") == "msg" && S(e, "role").EndsWith("-child"))
                    .Select(Name).ToList(),
                ["childTextAfter"] = _childText,
            };

            File.WriteAllText(ResultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Log("结果已写入 " + ResultPath);
        }

        private static List<Dictionary<string, object>> LoadEvents(string path)
        {
            var list = new List<Dictionary<string, object>>();
            if (!File.Exists(path)) return list;

            // 目标进程还开着这个文件（FileShare.ReadWrite），读的时候也必须允许写共享
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var d = new Dictionary<string, object>();
                    foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
                    list.Add(d);
                }
                catch { /* 最后一行可能是半行 */ }
            }
            return list;
        }

        private static IEnumerable<Dictionary<string, object>> InWindow(IEnumerable<Dictionary<string, object>> evs, string name)
        {
            if (!Windows.TryGetValue(name, out var w)) yield break;
            foreach (var e in evs)
            {
                if (!e.TryGetValue("qpc", out var q)) continue;
                long t = ((JsonElement)q).GetInt64();
                if (t >= w[0] && t <= w[1]) yield return e;
            }
        }

        private static string Name(Dictionary<string, object> e) =>
            e.TryGetValue("name", out var n) ? ((JsonElement)n).GetString() : "";

        private static long L(Dictionary<string, object> e, string key)
        {
            if (!e.TryGetValue(key, out var v)) return long.MinValue;
            if (v is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number) return je.GetInt64();
                if (je.ValueKind == JsonValueKind.True) return 1;
                if (je.ValueKind == JsonValueKind.False) return 0;
            }
            return long.MinValue;
        }

        private static string S(Dictionary<string, object> e, string key) =>
            e.TryGetValue(key, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() : null;

        private static long QpcOf(Dictionary<string, object> e)
        {
            if (!e.TryGetValue("qpc", out var v)) return 0;
            return v is JsonElement je && je.ValueKind == JsonValueKind.Number ? je.GetInt64() : 0;
        }

        private static Dictionary<string, object> AnalyzeKeyboard(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "keyboard").ToList();
            var msgs = evs.Where(e => S(e, "ev") == "msg").ToList();

            var counts = msgs.GroupBy(Name).ToDictionary(g => g.Key, g => g.Count());
            string chars = string.Concat(msgs.Where(m => Name(m) == "WM_CHAR")
                .Select(m => ((char)L(m, "ch")).ToString()));

            var charWp = msgs.Where(m => Name(m) == "WM_CHAR").Select(m => L(m, "wp")).ToList();
            var keyVk = msgs.Where(m => Name(m) == "WM_KEYDOWN").Select(m => L(m, "vk")).ToList();
            var keyLp = msgs.Where(m => Name(m) == "WM_KEYDOWN").Select(m => L(m, "lp")).ToList();
            var rawCount = evs.Count(e => S(e, "ev") == "raw");

            var (before, after) = TextAround(all, "keyboard");

            return new Dictionary<string, object>
            {
                ["counts"] = counts,
                ["chars"] = chars,
                ["charWparam"] = charWp,
                ["keydownVk"] = keyVk,
                ["keydownLparam"] = keyLp,
                ["rawInput"] = rawCount,
                ["textBefore"] = before,
                ["textAfter"] = after,
                ["addedText"] = Added(before, after),
            };
        }

        private static Dictionary<string, object> AnalyzeMove(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "mousemove").ToList();
            var moves = evs.Where(e => S(e, "ev") == "msg" && Name(e) == "WM_MOUSEMOVE").ToList();

            var xs = moves.Select(m => L(m, "x")).ToList();
            var ys = moves.Select(m => L(m, "y")).ToList();
            var mk = moves.Select(m => L(m, "mk")).ToList();

            // 期望值：主窗口客户区比例 → 从窗口客户区
            int lastCx = _masterClient.Width / 2, lastCy = _masterClient.Height / 2;
            int expX = (int)Math.Round(lastCx * (double)_slaveW / _masterClient.Width);
            int expY = (int)Math.Round(lastCy * (double)_slaveH / _masterClient.Height);

            // 只有落在我方 mark ±50ms 内的移动才算「注入产生的」，把人手噪声摘出去
            long tol = Stopwatch.Frequency / 20; // 50ms
            int attributed = 0;
            var ourPoints = new List<string>();
            if (Windows.TryGetValue("mousemove", out var win))
            {
                var marks = _moveMarks.Where(k => k >= win[0] - tol && k <= win[1]).ToList();
                foreach (var m in moves)
                {
                    long q = QpcOf(m);
                    if (marks.Any(k => Math.Abs(q - k) <= tol))
                    {
                        attributed++;
                        ourPoints.Add($"{L(m, "x")},{L(m, "y")}");
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["count"] = moves.Count,
                ["attributed"] = attributed,
                ["attributedPoints"] = ourPoints,
                ["xRange"] = xs.Count == 0 ? new int[0] : new[] { xs.Min(), xs.Max() },
                ["yRange"] = ys.Count == 0 ? new int[0] : new[] { ys.Min(), ys.Max() },
                ["distinct"] = moves.Select(m => $"{L(m, "x")},{L(m, "y")}").Distinct().ToList(),
                ["mkValues"] = mk.Distinct().ToList(),
                ["allInsideClient"] = moves.All(m => L(m, "x") >= 0 && L(m, "x") < _slaveW && L(m, "y") >= 0 && L(m, "y") < _slaveH),
                ["slaveClient"] = new[] { _slaveW, _slaveH },
                ["expectedCenter"] = new[] { expX, expY },
                ["rawInput"] = evs.Count(e => S(e, "ev") == "raw"),
            };
        }

        private static Dictionary<string, object> AnalyzeClick(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "click").ToList();
            var msgs = evs.Where(e => S(e, "ev") == "msg").ToList();

            return new Dictionary<string, object>
            {
                ["counts"] = msgs.GroupBy(Name).ToDictionary(g => g.Key, g => g.Count()),
                ["downWparam"] = msgs.Where(m => Name(m) == "WM_LBUTTONDOWN").Select(m => L(m, "wp")).ToList(),
                ["upWparam"] = msgs.Where(m => Name(m) == "WM_LBUTTONUP").Select(m => L(m, "wp")).ToList(),
                ["points"] = msgs.Where(m => Name(m) == "WM_LBUTTONDOWN" || Name(m) == "WM_LBUTTONUP")
                                 .Select(m => $"{Name(m)}@{L(m, "x")},{L(m, "y")}").ToList(),
                ["rawInput"] = evs.Count(e => S(e, "ev") == "raw"),
            };
        }

        private static Dictionary<string, object> AnalyzeWheel(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "wheel").ToList();
            var wheels = evs.Where(e => S(e, "ev") == "msg" && Name(e) == "WM_MOUSEWHEEL").ToList();

            var pts = wheels.Select(w => new { x = L(w, "x"), y = L(w, "y") }).ToList();
            bool inside = pts.All(p =>
                p.x >= _slaveOriginX && p.x <= _slaveOriginX + _slaveW &&
                p.y >= _slaveOriginY && p.y <= _slaveOriginY + _slaveH);

            return new Dictionary<string, object>
            {
                ["count"] = wheels.Count,
                ["deltas"] = wheels.Select(w => L(w, "delta")).ToList(),
                ["screenPoints"] = pts.Select(p => $"{p.x},{p.y}").ToList(),
                ["wparam"] = wheels.Select(w => L(w, "wp")).ToList(),
                ["slaveScreenClient"] = new[] { _slaveOriginX, _slaveOriginY, _slaveOriginX + _slaveW, _slaveOriginY + _slaveH },
                ["pointInsideSlaveClient"] = inside,
                ["rawInput"] = evs.Count(e => S(e, "ev") == "raw"),
            };
        }

        private static Dictionary<string, object> AnalyzeDrag(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "drag").Where(e => S(e, "ev") == "msg").ToList();
            var seq = evs.Where(e => Name(e) is "WM_LBUTTONDOWN" or "WM_MOUSEMOVE" or "WM_LBUTTONUP").ToList();

            // 压成「DOWN, MOVE×n, UP」这样的串，方便一眼看出顺序
            var compact = new List<string>();
            foreach (var e in seq)
            {
                string n = Name(e);
                if (compact.Count > 0 && compact[^1].StartsWith(n == "WM_MOUSEMOVE" ? "MOVE" : n))
                {
                    var prev = compact[^1];
                    if (n == "WM_MOUSEMOVE" && prev.StartsWith("MOVE×"))
                    {
                        int k = int.Parse(prev.Substring(5)) + 1;
                        compact[^1] = "MOVE×" + k;
                    }
                    else compact.Add(n == "WM_MOUSEMOVE" ? "MOVE×1" : n);
                }
                else
                {
                    compact.Add(n == "WM_MOUSEMOVE" ? "MOVE×1" : n);
                }
            }

            int iDown = seq.FindIndex(e => Name(e) == "WM_LBUTTONDOWN");
            int iUp = seq.FindIndex(e => Name(e) == "WM_LBUTTONUP");
            int iFirstMove = seq.FindIndex(e => Name(e) == "WM_MOUSEMOVE");
            int iLastMove = seq.FindLastIndex(e => Name(e) == "WM_MOUSEMOVE");

            bool ordered = iDown >= 0 && iUp >= 0 && iDown < iUp;
            bool downBeforeMove = iDown >= 0 && (iFirstMove < 0 || iDown < iFirstMove);
            bool upAfterMove = iUp >= 0 && (iLastMove < 0 || iUp > iLastMove);

            // 按下与抬起之间的移动条数：人手噪声可能落在窗口任意位置，但这段区间内的
            // 移动才是「拖动过程中转发了哪些移动」的证据
            int movesBetween = iDown >= 0 && iUp > iDown
                ? seq.Skip(iDown + 1).Take(iUp - iDown - 1).Count(e => Name(e) == "WM_MOUSEMOVE")
                : 0;

            var moveMk = seq.Where(e => Name(e) == "WM_MOUSEMOVE").Select(e => L(e, "mk")).ToList();

            return new Dictionary<string, object>
            {
                ["sequence"] = compact,
                ["sequenceText"] = string.Join(" → ", compact),
                ["downIndex"] = iDown,
                ["upIndex"] = iUp,
                ["downBeforeFirstMove"] = downBeforeMove,
                ["upAfterLastMove"] = upAfterMove,
                ["downUpOrdered"] = ordered,
                ["moveCount"] = seq.Count(e => Name(e) == "WM_MOUSEMOVE"),
                ["movesBetweenDownUp"] = movesBetween,
                ["moveWithLeftButton"] = moveMk.Count(v => (v & 0x1) != 0),
                ["moveMkDistinct"] = moveMk.Distinct().ToList(),
                ["rawInput"] = InWindow(all, "drag").Count(e => S(e, "ev") == "raw"),
            };
        }

        private static Dictionary<string, object> AnalyzeChinese(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "chinese").ToList();
            var (before, after) = TextAround(all, "chinese");

            List<(long qpc, long hwnd)> samples;
            lock (FgSamples) samples = FgSamples.ToList();
            if (Windows.TryGetValue("chinese", out var cw))
            {
                samples = samples.Where(s => s.qpc >= cw[0] && s.qpc <= cw[1]).ToList();
            }

            var segments = new List<object>();
            long totalAway = 0;
            int switches = 0;
            string current = null;
            long segStart = 0;

            foreach (var s in samples)
            {
                string who = s.hwnd == (long)_masterHwnd ? "master"
                    : s.hwnd == _slaveHwnd ? "slave"
                    : s.hwnd == 0 ? "none" : "other:0x" + s.hwnd.ToString("X");
                if (who != current)
                {
                    if (current != null && current != "master")
                    {
                        long ms = (long)((s.qpc - segStart) * 1000.0 / Stopwatch.Frequency);
                        segments.Add(new { what = current, ms });
                        totalAway += ms;
                        switches++;
                    }
                    current = who;
                    segStart = s.qpc;
                }
            }
            if (current != null && current != "master")
            {
                long ms = (long)((samples[^1].qpc - segStart) * 1000.0 / Stopwatch.Frequency);
                segments.Add(new { what = current, ms });
                totalAway += ms;
                switches++;
            }

            return new Dictionary<string, object>
            {
                ["textBefore"] = before,
                ["textAfter"] = after,
                ["addedText"] = Added(before, after),
                ["charsReceived"] = evs.Count(e => S(e, "ev") == "msg" && Name(e) == "WM_CHAR"),
                ["foreground"] = new
                {
                    samples = samples.Count,
                    segmentsAwayFromMaster = segments,
                    totalAwayMs = totalAway,
                    leftMasterCount = switches,
                    lastForeground = samples.Count == 0 ? "?" :
                        samples[^1].hwnd == (long)_masterHwnd ? "master" : "0x" + samples[^1].hwnd.ToString("X"),
                },
                ["rawInput"] = evs.Count(e => S(e, "ev") == "raw"),
                // 两个独立的判据分开记：剪贴板有没有真的置成目标文本，和核心日志里有没有
                // 「剪贴板中转」这一行。前者失败的话，本轮「中文没进去」不能归到核心头上。
                ["clipboardReady"] = Trace.ToString().Contains("剪贴板已置为"),
                ["coreLoggedClipboard"] = Trace.ToString().Contains("剪贴板中转"),
            };
        }

        private static Dictionary<string, object> AnalyzeMirror(List<Dictionary<string, object>> all)
        {
            var evs = InWindow(all, "mirror").ToList();
            var (before, after) = TextAround(all, "mirror");
            var chars = evs.Where(e => S(e, "ev") == "msg" && Name(e) == "WM_CHAR")
                           .Select(e => ((char)L(e, "ch")).ToString());

            return new Dictionary<string, object>
            {
                ["textBefore"] = before,
                ["textAfter"] = after,
                ["addedText"] = Added(before, after),
                ["charsReceived"] = chars.Count(),
                ["charText"] = string.Concat(chars),
                ["rawInput"] = evs.Count(e => S(e, "ev") == "raw"),
            };
        }

        private static (string before, string after) TextAround(List<Dictionary<string, object>> all, string window)
        {
            if (!Windows.TryGetValue(window, out var w)) return (null, null);

            string before = null, after = null;
            foreach (var e in all)
            {
                if (S(e, "ev") != "text") continue;
                long t = ((JsonElement)e["qpc"]).GetInt64();
                if (t < w[0]) before = S(e, "text");
                else if (t <= w[1]) after = S(e, "text");
            }
            return (before, after);
        }

        private static string Added(string before, string after)
        {
            if (after == null) return null;
            if (string.IsNullOrEmpty(before)) return after;
            return after.StartsWith(before) ? after.Substring(before.Length) : "（非追加：" + after + "）";
        }

        private static string TryHash(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(fs));
            }
            catch (Exception ex) { return "hash失败: " + ex.Message; }
        }

        private static void Log(string s) => Trace.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
    }

    /// <summary>极简步骤调度器：每 tick 推进一个「完成返回 true」的闭包。</summary>
    internal sealed class Scheduler
    {
        private readonly Queue<Func<bool>> _steps = new();
        private Func<bool> _current;

        public bool IsFinished { get; private set; }
        public event Action Finished;

        public void Add(Action action) => _steps.Enqueue(() => { action(); return true; });

        public void Add(Func<bool> step) => _steps.Enqueue(step);

        public void Wait(int ms)
        {
            long until = 0;
            bool started = false;
            _steps.Enqueue(() =>
            {
                if (!started) { until = Environment.TickCount64 + ms; started = true; }
                return Environment.TickCount64 >= until;
            });
        }

        public void WaitUntil(Func<bool> cond, int timeoutMs)
        {
            long until = 0;
            bool started = false;
            _steps.Enqueue(() =>
            {
                if (!started) { until = Environment.TickCount64 + timeoutMs; started = true; }
                return cond() || Environment.TickCount64 >= until;
            });
        }

        public void Tick()
        {
            if (IsFinished) return;
            if (_current == null)
            {
                if (_steps.Count == 0)
                {
                    IsFinished = true;
                    Finished?.Invoke();
                    return;
                }
                _current = _steps.Dequeue();
            }
            if (_current()) _current = null;
        }
    }
}
