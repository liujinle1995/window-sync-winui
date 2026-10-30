using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LeXiangSync.Models;

/// <summary>
/// 窗口指纹：方案只记录窗口标题，加载时再按标题回查当前句柄，
/// 因此进程重启后方案依然可用。
/// </summary>
public sealed class WindowFingerprint
{
    /// <summary>窗口完整标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>所属进程名，仅用于展示与辅助匹配。</summary>
    public string? ProcessName { get; set; }

    /// <summary>
    /// 同进程、同标题窗口中的序号（从 0 开始）。游戏多开时标题完全相同，
    /// 只靠标题永远只能匹配到第一个，用序号即可区分第 N 个实例。
    /// </summary>
    public int Index { get; set; }

    /// <summary>界面展示用的指纹文案：`Client.exe #3`（序号按人习惯从 1 起）。</summary>
    public string Display => string.IsNullOrWhiteSpace(ProcessName)
        ? $"#{Index + 1}"
        : $"{ProcessName} #{Index + 1}";

    public override string ToString() => Title;
}

/// <summary>一份已保存的同步方案。</summary>
public sealed partial class SyncPlan : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public WindowFingerprint Master { get; set; } = new();
    public List<WindowFingerprint> Slaves { get; set; } = new();
    public bool SyncMouse { get; set; } = true;
    public bool SyncKeyboard { get; set; } = true;
    public bool SyncClipboard { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    // ── 旧版方案兼容字段（v0.7 之前只存标题数组） ──────────────────────────
    public string? MasterTitle { get; set; }
    public List<string>? SlaveTitles { get; set; }

    // ── 界面展示（不参与持久化） ─────────────────────────────────────────

    /// <summary>是否为当前生效方案。界面高亮用，不写进 JSON。</summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>主窗口指纹列：`梦幻西游 · 角色A / Client.exe #1`。</summary>
    [JsonIgnore]
    public string MasterFingerprintText => $"{Master.Title} / {Master.Display}";

    [JsonIgnore]
    public string SlaveCountText => $"{Slaves.Count} 个从窗口";

    /// <summary>
    /// 文本镜像：全局开关（不随方案持久化），列表展示时由页面从引擎同步过来，
    /// 让「同步项」列能如实反映当前是否带镜像。
    /// </summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial bool MirrorEnabled { get; set; }

    /// <summary>同步项列。方案只持久化鼠标 / 键盘 / 剪贴板，文本镜像是全局开关。</summary>
    [JsonIgnore]
    public string SyncItemsText
    {
        get
        {
            var items = new List<string>();
            if (SyncMouse) items.Add("鼠标");
            if (SyncKeyboard) items.Add("键盘");
            if (SyncClipboard) items.Add("剪贴板");
            if (MirrorEnabled) items.Add("镜像");
            return items.Count == 0 ? "未开启" : string.Join(" · ", items);
        }
    }

    /// <summary>方案列次行：`更新于 10-28 21:14`。</summary>
    [JsonIgnore]
    public string UpdatedText => $"更新于 {CreatedAt:MM-dd HH:mm}";

    /// <summary>方案列次行（含生效前缀）：`已生效 · 更新于 10-28 21:14`。</summary>
    [JsonIgnore]
    public string StatusLineText => IsActive ? $"已生效 · {UpdatedText}" : UpdatedText;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(StatusLineText));

    partial void OnMirrorEnabledChanged(bool value) => OnPropertyChanged(nameof(SyncItemsText));
}
