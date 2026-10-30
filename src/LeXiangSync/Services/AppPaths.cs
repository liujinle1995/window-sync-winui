using System.Reflection;

namespace LeXiangSync.Services;

/// <summary>
/// 随程序分发的资源文件（图标、提示音）所在目录。
///
/// 单文件发布（PublishSingleFile + IncludeAllContentForSelfExtract）时，这些内容会被
/// 解压到 <c>%TEMP%\.net\&lt;app&gt;\&lt;hash&gt;\</c>，而 AppContext.BaseDirectory 仍然指向
/// exe 所在目录 —— 两个路径不再相同。所以按「exe 目录 → 程序集所在目录」依次找，
/// 谁真的有 Assets 就用谁，普通（非单文件）模式下顺序不变。
/// </summary>
internal static class AppPaths
{
    public static string AssetsDirectory { get; } = ResolveAssetsDirectory();

    /// <summary>取 Assets 下的某个文件，例如 Asset("appicon.ico")。</summary>
    public static string Asset(string relativePath) => Path.Combine(AssetsDirectory, relativePath);

    private static string ResolveAssetsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "Assets"),
        };

        return Array.Find(candidates, Directory.Exists) ?? candidates[0];
    }
}
