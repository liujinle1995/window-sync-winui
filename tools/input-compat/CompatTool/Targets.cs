using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CompatTool
{
    /// <summary>
    /// 目标窗口宿主。每个角色一个进程、一个顶层窗口 —— 这样最接近真实的「从窗口」，
    /// 也避免 WinForms / WPF 两套消息泵挤在一个进程里互相干扰。
    /// </summary>
    internal static class TargetHost
    {
        public static int Run(Args a)
        {
            string role = a.Get("role", "win32");
            string log = a.Get("log");
            string meta = a.Get("meta");
            var pos = a.GetPoint("pos", 900, 80);
            var size = a.GetSize("size", 680, 520);

            Logger.Open(log);

            switch (role)
            {
                case "win32": return RunWin32(meta, pos, size);
                case "winforms": return RunWinForms(meta, pos, size);
                case "wpf": return RunWpf(meta, pos, size);
                case "rawinput": return RunRawInput(meta, pos, size);
                default:
                    Logger.Write(new Dictionary<string, object> { ["ev"] = "error", ["error"] = "未知 role: " + role });
                    return 2;
            }
        }

        // ── 公共：把窗口身份写成 meta，驱动据此拿 hwnd / 客户区尺寸 ──────
        private static void WriteMeta(string path, string role, IntPtr hwnd, string title)
        {
            Native.GetClientRect(hwnd, out var client);
            Native.GetWindowRect(hwnd, out var win);
            var origin = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen(hwnd, ref origin);

            Logger.WriteMeta(path, new Dictionary<string, object>
            {
                ["role"] = role,
                ["pid"] = Environment.ProcessId,
                ["hwnd"] = (long)hwnd,
                ["title"] = title,
                ["className"] = Native.WindowClass(hwnd),
                ["clientW"] = client.Right - client.Left,
                ["clientH"] = client.Bottom - client.Top,
                ["clientOriginX"] = origin.X,
                ["clientOriginY"] = origin.Y,
                ["winX"] = win.Left,
                ["winY"] = win.Top,
                ["winW"] = win.Right - win.Left,
                ["winH"] = win.Bottom - win.Top,
                ["dpi"] = Native.GetDpiForWindow(hwnd),
            });
        }

        // ── ① Win32 自绘窗口 ─────────────────────────────────────────────
        private static Native.WndProc _wnd32Proc; // 必须用字段保住委托，否则被 GC 回收后直接崩

        private static int RunWin32(string meta, Point pos, Size size)
        {
            const string cls = "CompatWnd32";
            const string title = "COMPAT-WIN32";

            _wnd32Proc = (h, msg, wp, lp) =>
            {
                switch ((int)msg)
                {
                    case Native.WM_KEYDOWN:
                    case Native.WM_KEYUP:
                    case Native.WM_SYSKEYDOWN:
                    case Native.WM_SYSKEYUP:
                    case Native.WM_CHAR:
                    case Native.WM_INPUT:
                    case Native.WM_MOUSEMOVE:
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_LBUTTONUP:
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_RBUTTONUP:
                    case Native.WM_MOUSEWHEEL:
                        Logger.Msg("win32", msg, wp, lp);
                        break;
                }

                if (msg == 0x0010 /* WM_CLOSE */) Native.PostQuitMessage(0);
                return Native.DefWindowProcW(h, msg, wp, lp);
            };

            var wc = new Native.WNDCLASSW
            {
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wnd32Proc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = cls,
                hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            };
            Native.RegisterClassW(ref wc);

            var hwnd = Native.CreateWindowExW(0, cls, title, Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE,
                pos.X, pos.Y, size.Width, size.Height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            WriteMeta(meta, "win32", hwnd, title);

            while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                // TranslateMessage 必须有：PostMessage 过来的 WM_KEYDOWN 只有经过它才会变成 WM_CHAR
                Native.TranslateMessage(ref m);
                Native.DispatchMessageW(ref m);
            }
            return 0;
        }

        // ── ② WinForms + TextBox ────────────────────────────────────────

        /// <summary>
        /// 带日志的 TextBox。要点：WinForms 的 TextBox 是一个**子窗口**（Edit 类）。
        /// 只往顶层 Form PostMessage 的话，消息停在 Form 的 WndProc，进不到这个子窗口 ——
        /// 所以必须单独统计「子控件自己收到了什么」，否则「Form 收到 WM_CHAR」会被
        /// 误读成「文本真的进去了」。
        /// </summary>
        private sealed class LoggingTextBox : TextBox
        {
            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_KEYDOWN:
                    case Native.WM_KEYUP:
                    case Native.WM_CHAR:
                    case Native.WM_SYSKEYDOWN:
                    case Native.WM_SYSKEYUP:
                        Logger.Msg("winforms-child", (uint)m.Msg, m.WParam, m.LParam, Text);
                        break;
                }
                base.WndProc(ref m);
            }
        }

        private sealed class WinFormsTarget : Form
        {
            private readonly TextBox _text;
            private readonly string _meta;
            private readonly System.Windows.Forms.Timer _snap;

            public WinFormsTarget(string meta, Point pos, Size size)
            {
                _meta = meta;
                Text = "COMPAT-WINFORMS";
                StartPosition = FormStartPosition.Manual;
                Location = pos;
                ClientSize = size;
                BackColor = Color.WhiteSmoke;

                _text = new LoggingTextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 12f),
                    ScrollBars = ScrollBars.Vertical,
                };
                _text.TextChanged += (_, _) => Logger.Write(new Dictionary<string, object>
                {
                    ["role"] = "winforms",
                    ["ev"] = "text",
                    ["text"] = _text.Text,
                });
                Controls.Add(_text);

                // 周期性快照：文本没变化时 TextChanged 不会触发，光靠它拿不到「场景开始前是什么」。
                // 每 250ms 落一条 text 事件，驱动就能用 QPC 窗口裁出 before / after。
                _snap = new System.Windows.Forms.Timer { Interval = 250 };
                _snap.Tick += (_, _) => Snapshot();
                _snap.Start();
            }

            private void Snapshot() => Logger.Write(new Dictionary<string, object>
            {
                ["role"] = "winforms",
                ["ev"] = "text",
                ["text"] = _text.Text,
            });

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Activate();
                _text.Focus();
                Snapshot();
                WriteMeta(_meta, "winforms", Handle, Text);
                Logger.Write(new Dictionary<string, object>
                {
                    ["role"] = "winforms",
                    ["ev"] = "ready",
                    ["hwnd"] = (long)Handle,
                    ["focused"] = (long)Native.GetFocus(),
                });
            }

            protected override void OnActivated(EventArgs e)
            {
                base.OnActivated(e);
                _text.Focus();
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_KEYDOWN:
                    case Native.WM_KEYUP:
                    case Native.WM_CHAR:
                    case Native.WM_SYSKEYDOWN:
                    case Native.WM_SYSKEYUP:
                    case Native.WM_MOUSEMOVE:
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_LBUTTONUP:
                    case Native.WM_MOUSEWHEEL:
                    case Native.WM_INPUT:
                        Logger.Msg("winforms", (uint)m.Msg, m.WParam, m.LParam, _text.Text);
                        break;
                }
                base.WndProc(ref m);
            }
        }

        private static int RunWinForms(string meta, Point pos, Size size)
        {
            Application.Run(new WinFormsTarget(meta, pos, size));
            return 0;
        }

        // ── ③ WPF + TextBox ─────────────────────────────────────────────
        private static int RunWpf(string meta, Point pos, Size size)
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };

            var text = new System.Windows.Controls.TextBox
            {
                AcceptsReturn = true,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 16,
                Margin = new System.Windows.Thickness(4),
            };

            var window = new System.Windows.Window
            {
                Title = "COMPAT-WPF",
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = pos.X,
                Top = pos.Y,
                Width = size.Width,
                Height = size.Height,
                Background = System.Windows.Media.Brushes.WhiteSmoke,
                Content = text,
            };

            text.TextChanged += (_, _) => Logger.Write(new Dictionary<string, object>
            {
                ["role"] = "wpf",
                ["ev"] = "text",
                ["text"] = text.Text,
            });

            // 同 WinForms：文本没动也要有采样点，驱动才有 before / after 可裁
            var snap = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            snap.Tick += (_, _) => Logger.Write(new Dictionary<string, object>
            {
                ["role"] = "wpf",
                ["ev"] = "text",
                ["text"] = text.Text,
            });
            snap.Start();

            window.ContentRendered += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;

                // WPF 的 Left/Top 是 DIP，换成物理像素定位，保证和 Win32 目标处在同一坐标系
                Native.SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, size.Width, size.Height, 0x0004 /* NOSIZE? 用 SIZE 更稳 */);

                var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                src?.AddHook(WpfHook);

                WriteMeta(meta, "wpf", hwnd, "COMPAT-WPF");
                text.Focus();
                System.Windows.Input.Keyboard.Focus(text);
            };

            window.Activated += (_, _) =>
            {
                text.Focus();
                System.Windows.Input.Keyboard.Focus(text);
            };

            app.Run(window);
            return 0;
        }

        private static IntPtr WpfHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Native.WM_KEYDOWN:
                case Native.WM_KEYUP:
                case Native.WM_CHAR:
                case Native.WM_SYSKEYDOWN:
                case Native.WM_SYSKEYUP:
                case Native.WM_MOUSEMOVE:
                case Native.WM_LBUTTONDOWN:
                case Native.WM_LBUTTONUP:
                case Native.WM_MOUSEWHEEL:
                case Native.WM_INPUT:
                    Logger.Msg("wpf", (uint)msg, wParam, lParam);
                    break;
            }
            return IntPtr.Zero;
        }

        // ── ④ Raw Input 监听窗口 ────────────────────────────────────────
        private static Native.WndProc _rawProc;

        private static int RunRawInput(string meta, Point pos, Size size)
        {
            const string cls = "CompatRawInput";
            const string title = "COMPAT-RAWINPUT";
            IntPtr hwnd = IntPtr.Zero;

            _rawProc = (h, msg, wp, lp) =>
            {
                if (msg == Native.WM_INPUT)
                {
                    LogRawInput(wp, lp);
                }
                else
                {
                    switch ((int)msg)
                    {
                        case Native.WM_KEYDOWN:
                        case Native.WM_KEYUP:
                        case Native.WM_CHAR:
                        case Native.WM_MOUSEMOVE:
                        case Native.WM_LBUTTONDOWN:
                        case Native.WM_LBUTTONUP:
                        case Native.WM_MOUSEWHEEL:
                            Logger.Msg("rawinput", msg, wp, lp);
                            break;
                    }
                }

                if (msg == 0x0010) Native.PostQuitMessage(0);
                return Native.DefWindowProcW(h, msg, wp, lp);
            };

            var wc = new Native.WNDCLASSW
            {
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_rawProc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = cls,
                hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            };
            Native.RegisterClassW(ref wc);

            hwnd = Native.CreateWindowExW(0, cls, title, Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE,
                pos.X, pos.Y, size.Width, size.Height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            var devices = new[]
            {
                // 键盘：Usage Page 1 / Usage 6；鼠标：1 / 2
                // RIDEV_INPUTSINK 让窗口在**非前台**时也能收到 WM_INPUT —— 这正是要验证的点
                new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
                new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            bool ok = Native.RegisterRawInputDevices(devices, (uint)devices.Length,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));

            WriteMeta(meta, "rawinput", hwnd, title);
            Logger.Write(new Dictionary<string, object>
            {
                ["role"] = "rawinput",
                ["ev"] = "register",
                ["ok"] = ok,
                ["err"] = System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
            });

            while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref m);
                Native.DispatchMessageW(ref m);
            }
            return 0;
        }

        private static void LogRawInput(IntPtr wParam, IntPtr lParam)
        {
            uint size = 0;
            uint headerSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            Native.GetRawInputData(lParam, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = size;
                if (Native.GetRawInputData(lParam, Native.RID_INPUT, buf, ref got, headerSize) != got) return;

                uint type = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                int offset = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));

                var d = new Dictionary<string, object>
                {
                    ["role"] = "rawinput",
                    ["ev"] = "raw",
                    ["sink"] = (long)wParam == Native.RIM_INPUTSINK,
                };

                if (type == 1) // RIM_TYPEMOUSE
                {
                    var m = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RAWMOUSE>(buf + offset);
                    d["ri"] = "mouse";
                    d["flags"] = m.usFlags;
                    d["btn"] = m.usButtonFlags;
                    d["dx"] = m.lLastX;
                    d["dy"] = m.lLastY;
                }
                else if (type == 2) // RIM_TYPEKEYBOARD
                {
                    var k = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RAWKEYBOARD>(buf + offset);
                    d["ri"] = "kbd";
                    d["vkey"] = k.VKey;
                    d["make"] = k.MakeCode;
                    d["flags"] = k.Flags;
                    d["up"] = (k.Flags & 0x01) != 0;
                    d["wmsg"] = Native.MessageName(k.Message);
                }
                else
                {
                    d["ri"] = "other:" + type;
                }

                Logger.Write(d);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            }
        }
    }
}
