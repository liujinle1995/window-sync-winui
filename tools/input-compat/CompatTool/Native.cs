using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CompatTool
{
    /// <summary>本工具用到的 Win32 互操作。全部集中在这里，方便对着文档核对签名。</summary>
    internal static class Native
    {
        // ── 消息号 ───────────────────────────────────────────────────────
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_CHAR = 0x0102;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_INPUT = 0x00FF;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_DPICHANGED = 0x02E0;

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        public const uint MK_LBUTTON = 0x0001;
        public const uint MK_RBUTTON = 0x0002;
        public const uint MK_MBUTTON = 0x0010;

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        // ── 窗口 / 前台 ──────────────────────────────────────────────────
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetFocus();
        [DllImport("imm32.dll")] public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);

        /// <summary>
        /// WM_GETTEXT 是**内核帮忙跨进程封送**的系统消息，所以别的进程里标准控件（Edit 等）
        /// 的文本可以直接这样读回来 —— 这也是给 WinForms / Win32 目标做「文本真的进去了吗」断言的依据。
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

        public const uint WM_GETTEXT = 0x000D;

        /// <summary>沿 FindWindowExW 往下找到最深的子窗口（WinForms 的 TextBox 就是 Form 下的一个 Edit）。</summary>
        public static IntPtr DeepestChild(IntPtr parent)
        {
            var cur = parent;
            for (int depth = 0; depth < 8; depth++)
            {
                var child = FindWindowExW(cur, IntPtr.Zero, null, null);
                if (child == IntPtr.Zero) break;
                cur = child;
            }
            return cur;
        }

        public static string ReadText(IntPtr hWnd, int max = 4096)
        {
            if (hWnd == IntPtr.Zero) return null;
            var sb = new StringBuilder(max);
            SendMessageW(hWnd, WM_GETTEXT, (IntPtr)max, sb);
            return sb.ToString();
        }

        public static string ClassOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>把窗口硬推到前台。跨进程调用 SetForegroundWindow 常被前台锁挡下，先 AttachThreadInput。</summary>
        public static bool ForceForeground(IntPtr hWnd)
        {
            if (GetForegroundWindow() == hWnd) return true;

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try
            {
                ShowWindow(hWnd, SW_RESTORE);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
            return GetForegroundWindow() == hWnd;
        }

        // ── SendInput ────────────────────────────────────────────────────
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        // ── 合成真实输入的小工具 ─────────────────────────────────────────
        //
        // 这里全部走 SendInput —— 只有「真实输入」才会被 WH_KEYBOARD_LL / WH_MOUSE_LL 看到，
        // 再由 DLL 转发给从窗口。SetCursorPos 也算真实输入（会进钩子），但不会产生按键。

        public static uint KeyDown(ushort vk, bool up = false, bool extended = false)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)MapVirtualKeyW(vk, 0),
                        dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? 0x0001u : 0u),
                    }
                }
            };
            return SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>按一下并松开（中间不等待，间隔由调用方控制）。</summary>
        public static uint KeyPress(ushort vk)
        {
            var inputs = new[]
            {
                new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0) } } },
                new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0), dwFlags = KEYEVENTF_KEYUP } } },
            };
            return SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>把光标移到屏幕绝对坐标（真实输入，会进鼠标钩子）。</summary>
        public static uint MoveTo(int screenX, int screenY)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

            int nx = (int)Math.Round((screenX - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ny = (int)Math.Round((screenY - vy) * 65535.0 / Math.Max(1, vh - 1));

            var inp = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = nx,
                        dy = ny,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    }
                }
            };
            return SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static uint MouseButton(uint flag)
        {
            var inp = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } }
            };
            return SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>逐字注入 Unicode（KEYEVENTF_UNICODE）—— 中文/emoji 走这条路，不经过输入法。</summary>
        public static void TypeUnicode(string text, int perCharMs = 20)
        {
            foreach (char ch in text)
            {
                var down = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)ch, dwFlags = KEYEVENTF_UNICODE } }
                };
                var up = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
                };
                SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
                if (perCharMs > 0) System.Threading.Thread.Sleep(perCharMs);
            }
        }

        /// <summary>
        /// 注入一个滚轮刻度。
        /// <para>
        /// 必须和一个「相对移动」打包在同一次 SendInput 里：本机实测，**单独**发一个
        /// MOUSEEVENTF_WHEEL（前面没有真实的移动事件落地）时，这个滚轮连 WH_MOUSE_LL 钩子都进不去，
        /// 主窗口也收不到。而且偏移量不能是 0 —— 光标本来就在那一点时不会产生移动事件，等于没发。
        /// 所以这里固定先走 1px 再滚。这是 SendInput 滚轮的老毛病，不是被测程序的问题。
        /// </para>
        /// </summary>
        public static uint Wheel(int delta)
        {
            var inputs = new[]
            {
                new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = MOUSEEVENTF_MOVE } } },
                new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL } } },
            };
            return SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // ── 原始窗口类 / 消息泵 ──────────────────────────────────────────
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSW
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassW(ref WNDCLASSW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);

        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);

        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        public const uint WS_VISIBLE = 0x10000000;
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_CLIPCHILDREN = 0x02000000;
        public const uint IDC_ARROW = 32512;

        // ── Raw Input ────────────────────────────────────────────────────
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const int RID_INPUT = 0x10000003;
        public const int RIM_INPUT = 0;
        public const int RIM_INPUTSINK = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(IntPtr rawInput, int command, IntPtr data, ref uint size, uint headerSize);

        // ── 日志需要的解码小工具 ─────────────────────────────────────────
        public static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
        public static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
        public static short HiWordSigned(IntPtr v) => unchecked((short)(((long)v >> 16) & 0xFFFF));

        public static string WindowTitle(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string WindowClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>把一条窗口消息解成方便断言的字段。</summary>
        public static Dictionary<string, object> DescribeMessage(uint msg, IntPtr wp, IntPtr lp)
        {
            var d = new Dictionary<string, object>
            {
                ["msg"] = (int)msg,
                ["name"] = MessageName(msg),
                ["wp"] = (long)wp,
                ["lp"] = (long)lp,
            };

            switch ((int)msg)
            {
                case WM_MOUSEMOVE:
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                    d["x"] = LoWord(lp);
                    d["y"] = HiWord(lp);
                    d["mk"] = (long)wp & 0xFFFF;
                    break;

                case WM_MOUSEWHEEL:
                    d["x"] = LoWord(lp);
                    d["y"] = HiWord(lp);
                    d["delta"] = (int)HiWordSigned(wp);
                    break;

                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                    d["vk"] = (long)wp & 0xFFFF;
                    d["repeat"] = (long)lp & 0xFFFF;
                    d["scan"] = (((long)lp >> 16) & 0xFF);
                    break;

                case WM_CHAR:
                    d["ch"] = (int)((long)wp & 0xFFFF);
                    d["chText"] = ((char)((long)wp & 0xFFFF)).ToString();
                    break;
            }

            return d;
        }

        public static string MessageName(uint msg) => msg switch
        {
            WM_KEYDOWN => "WM_KEYDOWN",
            WM_KEYUP => "WM_KEYUP",
            WM_CHAR => "WM_CHAR",
            WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
            WM_SYSKEYUP => "WM_SYSKEYUP",
            WM_INPUT => "WM_INPUT",
            WM_MOUSEMOVE => "WM_MOUSEMOVE",
            WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
            WM_LBUTTONUP => "WM_LBUTTONUP",
            WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
            WM_RBUTTONUP => "WM_RBUTTONUP",
            WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
            WM_MBUTTONUP => "WM_MBUTTONUP",
            WM_MOUSEWHEEL => "WM_MOUSEWHEEL",
            WM_DPICHANGED => "WM_DPICHANGED",
            _ => "0x" + msg.ToString("X4"),
        };
    }
}
