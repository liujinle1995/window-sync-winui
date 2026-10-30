namespace LeXiangSync.Helpers;

/// <summary>按进程名给出一个形象化的图标字符（沿用旧版启发式规则）。</summary>
public static class ProcessIcon
{
    public static string Get(string? name)
    {
        var n = name?.ToLowerInvariant() ?? string.Empty;

        if (Contains(n, "chrome", "edge", "firefox", "browser")) return "\U0001F310"; // 🌐
        if (Contains(n, "code", "studio", "idea")) return "\U0001F4BB";               // 💻
        if (Contains(n, "note", "word", "写字板")) return "\U0001F4DD";                // 📝
        if (Contains(n, "excel", "wps")) return "\U0001F4CA";                         // 📊
        if (Contains(n, "game", "steam", "游戏")) return "\U0001F3AE";                 // 🎮
        if (Contains(n, "qq", "wechat", "微信")) return "\U0001F4AC";                  // 💬

        return "\U0001FA9F"; // 🪟
    }

    private static bool Contains(string source, params string[] parts)
    {
        foreach (var part in parts)
        {
            if (source.Contains(part, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
