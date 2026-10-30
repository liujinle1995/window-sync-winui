namespace LeXiangSync.Models;

/// <summary>
/// 窗口背景材质。仅 Windows 11 22000+ 生效，Windows 10 自动降级为纯色背景。
/// </summary>
public enum AppBackdrop
{
    /// <summary>Mica：从桌面壁纸取色的极淡底纹，最适合常驻主窗口。</summary>
    Mica = 0,

    /// <summary>Mica Alt：对比更强一档的 Mica，层次更清楚。</summary>
    MicaAlt = 1,

    /// <summary>亚克力：磨砂半透明，比 Mica 更明显。</summary>
    Acrylic = 2,

    /// <summary>关闭材质，使用纯色背景。</summary>
    None = 3,
}
