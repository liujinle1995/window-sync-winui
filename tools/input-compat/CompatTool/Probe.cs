using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CompatTool
{
    /// <summary>
    /// 独立探针：直接在**不经过 window-sync 核心**的情况下装一对低级钩子，
    /// 看 SendInput 造出来的事件在钩子眼里长什么样（vkCode / mouseData / injected 标志）。
    /// <para>
    /// 用途是把「核心看到的东西」和「系统实际给出的事件」分开 —— 核心只是钩子的消费者，
    /// 一旦核心的判断和这里对不上，就能确定问题在核心的比较逻辑，而不是输入没造出来。
    /// </para>
    /// </summary>
    internal static class Probe
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public Native.POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static HookProc _kbProc;   // 委托必须用字段留住
        private static HookProc _msProc;
        private static IntPtr _kbHook, _msHook;

        /// <summary>
        /// 在**当前进程**里装一条记录用的鼠标钩子，把关键事件写进驱动日志。
        /// 用途：判断「核心到底有没有收到滚轮/点击」，把「钩子没收到」和「核心收到了没转发」分开。
        /// </summary>
        public static void AttachInlineMouseHook(Action<string> log)
        {
            _msProc = (code, wp, lp) =>
            {
                if (code >= 0)
                {
                    uint msg = (uint)wp;
                    if (msg == 0x020A || msg == 0x0201 || msg == 0x0202)
                    {
                        var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
                        log($"[hook] {Native.MessageName(msg)} screen=({ms.pt.X},{ms.pt.Y}) mouseData=0x{ms.mouseData:X8} flags=0x{ms.flags:X}");
                    }
                }
                return CallNextHookEx(_msHook, code, wp, lp);
            };

            _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);
            log($"[hook] 装上鼠标调试钩子: {( _msHook != IntPtr.Zero ? "ok" : "失败 err=" + Marshal.GetLastWin32Error())}");
        }

        private sealed class LoggingTextBox : TextBox
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_CHAR || m.Msg == Native.WM_KEYDOWN || m.Msg == Native.WM_KEYUP)
                {
                    Logger.Write(new Dictionary<string, object>
                    {
                        ["role"] = "probe",
                        ["ev"] = "box-msg",
                        ["name"] = Native.MessageName((uint)m.Msg),
                        ["wp"] = (long)m.WParam,
                        ["ch"] = (int)((long)m.WParam & 0xFFFF),
                    });
                }
                base.WndProc(ref m);
            }
        }

        public static int Run(Args a)
        {            string log = a.Get("log", "probe.jsonl");
            Logger.Open(log);

            var form = new Form
            {
                Text = "COMPAT-PROBE",
                StartPosition = FormStartPosition.Manual,
                Location = new Point(60, 60),
                ClientSize = new Size(420, 220),
                BackColor = Color.White,
            };
            var box = new LoggingTextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Font = new Font("Consolas", 14f),
            };
            form.Controls.Add(box);
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(12, 8, 12, 4),
                Text = "COMPAT-PROBE：独立探针（输入框用来验证 Unicode 注入能不能落到 WinForms TextBox）",
            });

            form.Shown += (_, _) =>
            {
                Native.ForceForeground(form.Handle);
                // 关掉输入法上下文：本机开着微软拼音时，VK_PACKET 会被 IME 吃掉、只剩一个候选字
                Native.ImmAssociateContext(box.Handle, IntPtr.Zero);
                box.Focus();

                _msProc = (code, wp, lp) =>
                {
                    if (code >= 0)
                    {
                        var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
                        Logger.Write(new Dictionary<string, object>
                        {
                            ["role"] = "probe",
                            ["ev"] = "hook-mouse",
                            ["name"] = Native.MessageName((uint)wp),
                            ["x"] = ms.pt.X,
                            ["y"] = ms.pt.Y,
                            ["mouseData"] = ms.mouseData,
                            ["delta"] = unchecked((short)(ms.mouseData >> 16)),
                            ["flags"] = ms.flags,
                            ["injected"] = (ms.flags & 0x01) != 0,
                        });
                    }
                    return CallNextHookEx(_msHook, code, wp, lp);
                };

                _kbProc = (code, wp, lp) =>
                {
                    if (code >= 0)
                    {
                        var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lp);
                        Logger.Write(new Dictionary<string, object>
                        {
                            ["role"] = "probe",
                            ["ev"] = "hook-key",
                            ["name"] = Native.MessageName((uint)wp),
                            ["vk"] = kb.vkCode,
                            ["scan"] = kb.scanCode,
                            ["flags"] = kb.flags,
                            ["injected"] = (kb.flags & 0x10) != 0,
                            ["unicode"] = (kb.flags & 0x04) != 0,
                        });
                    }
                    return CallNextHookEx(_kbHook, code, wp, lp);
                };

                if (a.Get("nohooks") != "true")
                {
                    _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);
                    _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);
                }
                Logger.Write(new Dictionary<string, object>
                {
                    ["role"] = "probe",
                    ["ev"] = "hooks",
                    ["mouse"] = _msHook != IntPtr.Zero,
                    ["keyboard"] = _kbHook != IntPtr.Zero,
                    ["nohooks"] = a.Get("nohooks"),
                    ["err"] = Marshal.GetLastWin32Error(),
                });

                var t = new System.Windows.Forms.Timer { Interval = 10 };
                long t0 = Environment.TickCount64;
                int phase = 0;
                t.Tick += (_, _) =>
                {
                    long dt = Environment.TickCount64 - t0;
                    if (phase == 0 && dt > 600)
                    {
                        phase = 1;
                        Logger.Write(new Dictionary<string, object> { ["role"] = "probe", ["ev"] = "phase", ["what"] = "send", ["at"] = dt });
                        Native.MoveTo(form.Left + 200, form.Top + 120);
                        Native.KeyPress(0x41);        // A
                        Native.KeyDown(0x11);         // Ctrl（看钩子给的 vk）
                        Native.KeyDown(0x11, up: true);
                        Native.MouseButton(Native.MOUSEEVENTF_LEFTDOWN);
                        Native.MouseButton(Native.MOUSEEVENTF_LEFTUP);
                        Logger.Write(new Dictionary<string, object> { ["role"] = "probe", ["ev"] = "phase", ["what"] = "wheel", ["ret"] = Native.Wheel(120) });
                    }
                    else if (phase == 1 && dt > 1200)
                    {
                        phase = 3;
                        form.Activate();
                        box.Focus();
                        Logger.Write(new Dictionary<string, object>
                        {
                            ["role"] = "probe", ["ev"] = "phase", ["what"] = "type-unicode",
                            ["focus"] = (long)Native.GetFocus(), ["box"] = (long)box.Handle,
                        });
                        Native.TypeUnicode("abc中", 30);
                        Logger.Write(new Dictionary<string, object>
                        {
                            ["role"] = "probe", ["ev"] = "text", ["text"] = box.Text,
                        });
                    }
                    else if (phase == 1 && dt > 3500)
                    {
                        phase = 2;
                        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
                        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
                        Logger.Write(new Dictionary<string, object> { ["role"] = "probe", ["ev"] = "done" });
                        t.Stop();
                        form.Close();
                    }
                };
                t.Start();
            };

            Application.Run(form);
            return 0;
        }
    }
}
