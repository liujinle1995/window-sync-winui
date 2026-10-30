namespace LeXiangSync.Helpers;

/// <summary>
/// 进程分组在左侧进程树里显示的名字。
/// <para>
/// 设计稿的组名是「梦幻西游 (2)」「记事本 (1)」这种用户认得的名字，而不是
/// 「Client.exe (2)」—— 多开场景下同一个 exe 会开很多实例，光看 exe 名根本分不清是哪个游戏。
/// 解析顺序：
/// 1. 已知软件的中文名（微信 / 记事本 / QQ …），覆盖最常见的一批；
/// 2. 该 exe 全部窗口标题的公共首段（`梦幻西游 · 角色A` → `梦幻西游`）；
/// 3. 都没有就退回 exe 名（去掉扩展名），至少比带 `.exe` 好看。
/// </para>
/// </summary>
public static class DisplayNames
{
    /// <summary>常见客户端的友好名。键是不带扩展名的小写 exe 名。</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wechat"] = "微信",
        ["weixin"] = "微信",
        ["wechatappex"] = "微信",
        ["qq"] = "QQ",
        ["tim"] = "TIM",
        ["dingtalk"] = "钉钉",
        ["feishu"] = "飞书",
        ["lark"] = "飞书",
        ["wxwork"] = "企业微信",
        ["notepad"] = "记事本",
        ["notepad++"] = "Notepad++",
        ["mspaint"] = "画图",
        ["calc"] = "计算器",
        ["explorer"] = "资源管理器",
        ["cmd"] = "命令提示符",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["windowsterminal"] = "终端",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["code"] = "VS Code",
        ["devenv"] = "Visual Studio",
        ["taskmgr"] = "任务管理器",
        ["regedit"] = "注册表编辑器",
        ["mstsc"] = "远程桌面",
        ["steam"] = "Steam",
        ["discord"] = "Discord",
        ["telegram"] = "Telegram",
    };

    /// <summary>标题里用来切「应用名 · 实例」的分隔符，长的放前面，避免被短的分隔符先切开。</summary>
    private static readonly string[] Separators = { " · ", " — ", " – ", " - ", " | ", "｜" };

    /// <summary>解析一个 exe 的展示名。</summary>
    /// <param name="exeName">进程名，如 <c>Client.exe</c>。</param>
    /// <param name="titles">该 exe 下所有可见窗口的标题。</param>
    public static string Get(string exeName, IReadOnlyList<string> titles)
    {
        var baseName = StripExtension(exeName);

        if (Known.TryGetValue(baseName, out var known)) return known;

        if (TryCommonTitlePrefix(titles, out var prefix)) return prefix;

        return string.IsNullOrWhiteSpace(baseName) ? exeName : baseName;
    }

    /// <summary>去掉扩展名；没有扩展名就原样返回。</summary>
    private static string StripExtension(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return string.Empty;

        var dot = exeName.LastIndexOf('.');
        return dot > 0 ? exeName[..dot] : exeName;
    }

    /// <summary>
    /// 所有标题切成首段后是否一致；一致且长度合理才采用。
    /// 标题为空、切出来是「无标题」这类占位词、或首段太长（说明没按约定命名）都放弃。
    /// </summary>
    private static bool TryCommonTitlePrefix(IReadOnlyList<string> titles, out string prefix)
    {
        prefix = string.Empty;
        if (titles.Count == 0) return false;

        string? candidate = null;
        foreach (var title in titles)
        {
            var head = Head(title);
            if (head.Length < 2 || head.Length > 24) return false;

            if (candidate is null) candidate = head;
            else if (!string.Equals(candidate, head, StringComparison.Ordinal)) return false;
        }

        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate is "无标题" or "Untitled" or "新建文本文档") return false;

        prefix = candidate;
        return true;
    }

    /// <summary>取标题的第一段。</summary>
    private static string Head(string title)
    {
        var value = title.Trim();
        if (value.Length == 0) return string.Empty;

        foreach (var separator in Separators)
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0) return value[..index].Trim();
        }

        return value;
    }
}
