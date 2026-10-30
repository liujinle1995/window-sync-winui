using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CompatTool
{
    /// <summary>
    /// JSONL 事件日志：一行一个事件，带进程内可比的 QPC 时间戳。
    /// <para>
    /// 时间戳用 <see cref="Stopwatch.GetTimestamp"/>（QueryPerformanceCounter），它是**系统级**计数器，
    /// 同一个 QPC 值在驱动进程和目标进程里含义相同 —— 这样就不需要两个进程之间做时间同步，
    /// 驱动只要拿「场景开始 / 结束」两个 QPC 值去筛日志行即可。
    /// </para>
    /// </summary>
    internal static class Logger
    {
        private static readonly object Gate = new();
        private static StreamWriter _writer;

        public static void Open(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // FileShare.ReadWrite：驱动要边跑边读
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public static long Qpc() => Stopwatch.GetTimestamp();

        public static void Write(Dictionary<string, object> fields)
        {
            fields["qpc"] = Qpc();
            string line = JsonSerializer.Serialize(fields);
            lock (Gate)
            {
                _writer?.WriteLine(line);
            }
        }

        public static void WriteMeta(string path, Dictionary<string, object> fields)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>记录一条从 WndProc 观察到的窗口消息。</summary>
        public static void Msg(string role, uint msg, IntPtr wp, IntPtr lp, string text = null)
        {
            var d = Native.DescribeMessage(msg, wp, lp);
            d["role"] = role;
            d["ev"] = "msg";
            if (text != null) d["text"] = text;
            Write(d);
        }
    }
}
