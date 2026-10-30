namespace LeXiangSync.Models;

/// <summary>启动同步所需的配置。</summary>
public sealed class SyncConfig
{
    public long MasterHwnd { get; set; }
    public List<long> SlaveHwnds { get; set; } = new();
    public bool SyncMouse { get; set; } = true;
    public bool SyncKeyboard { get; set; } = true;
    public bool SyncClipboard { get; set; }

    /// <summary>文本镜像：把主窗口输入的中文 / 粘贴内容同步到从窗口。</summary>
    public bool TextMirror { get; set; }
}

/// <summary>引擎当前状态（由 Rust 侧推送或查询得到）。</summary>
public sealed class SyncStatus
{
    public bool Active { get; set; }
    public long MasterHwnd { get; set; }
    public int SlaveCount { get; set; }
    public bool SyncMouse { get; set; }
    public bool SyncKeyboard { get; set; }
    public bool SyncClipboard { get; set; }
    public bool TextMirror { get; set; }
}
