using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CompatTool
{
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern bool ImmDisableIME(uint threadId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr LoadKeyboardLayoutW(string id, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
        /// <summary>
        /// 只截图、不注入任何输入：用来給第三方程序（游戏、Electron 应用）留「注入前长什么样」的底片。
        /// 用法：CompatTool shot --hwnd 0x1234 --path out.png
        /// </summary>
        private static int Shot(Args a)
        {
            string hs = a.Get("hwnd");
            if (string.IsNullOrEmpty(hs)) { Console.Error.WriteLine("缺少 --hwnd"); return 2; }
            long h = hs.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(hs.Substring(2), 16)
                : long.Parse(hs);
            string path = a.Get("path", "shot.png");

            if (!Native.IsWindow((IntPtr)h)) { Console.Error.WriteLine("窗口句柄无效"); return 2; }
            if (!Native.GetWindowRect((IntPtr)h, out var wr)) { Console.Error.WriteLine("取窗口矩形失败"); return 2; }

            var rect = new System.Drawing.Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            using var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, rect.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"shot {rect.Width}x{rect.Height} at {rect.X},{rect.Y} -> {path}");
            return 0;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // 关掉本进程的输入法（TSF）：本机开着微软拼音时，SendInput 的 VK_PACKET 会被 IME
            // 吃掉并做成候选字（实测只落下一个「阿」），拿不到原始 WM_CHAR，测不出真实行为。
            try { ImmDisableIME(0xFFFFFFFF); } catch { }
            // 再把键盘布局切到英文（美式）：只做 ImmDisableIME 还不够，本机默认输入法是微软拼音，
            // TSF 仍会把 VK_PACKET 做成候选字。切成 en-US 之后才是真正的「无输入法」环境。
            try { ActivateKeyboardLayout(LoadKeyboardLayoutW("00000409", 0x00000001), 0); } catch { }

            // 必须在任何窗口创建之前设置，否则 WinForms 会按虚拟化后的像素算客户区
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            var a = Args.Parse(args);
            string mode = a.Positional.Count > 0 ? a.Positional[0] : "";

            switch (mode)
            {
                case "target": return TargetHost.Run(a);
                case "driver": return Driver.Run(a);
                case "external": return Driver.RunExternal(a);
                case "probe": return Probe.Run(a);
                case "shot": return Shot(a);
                default:
                    Console.Error.WriteLine("用法: CompatTool target --role <win32|winforms|wpf|rawinput> --log <jsonl> --meta <json> [--pos x,y] [--size w,h]");
                    Console.Error.WriteLine("      CompatTool driver --role <同上> --dll <window_sync_core.dll> --out <目录>");
                    return 2;
            }
        }
    }

    /// <summary>极简参数解析：位置参数 + <c>--key value</c>。</summary>
    internal sealed class Args
    {
        public List<string> Positional { get; } = new();
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(string[] argv)
        {
            var a = new Args();
            for (int i = 0; i < argv.Length; i++)
            {
                var s = argv[i];
                if (s.StartsWith("--"))
                {
                    string key = s.Substring(2);
                    string val = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
                    a._map[key] = val;
                }
                else a.Positional.Add(s);
            }
            return a;
        }

        public string Get(string key, string fallback = null) =>
            _map.TryGetValue(key, out var v) ? v : fallback;

        public System.Drawing.Point GetPoint(string key, int dx, int dy)
        {
            var v = Get(key);
            if (string.IsNullOrEmpty(v)) return new System.Drawing.Point(dx, dy);
            var parts = v.Split(',');
            return new System.Drawing.Point(int.Parse(parts[0]), int.Parse(parts[1]));
        }

        public System.Drawing.Size GetSize(string key, int dw, int dh)
        {
            var v = Get(key);
            if (string.IsNullOrEmpty(v)) return new System.Drawing.Size(dw, dh);
            var parts = v.Split(',');
            return new System.Drawing.Size(int.Parse(parts[0]), int.Parse(parts[1]));
        }
    }
}
