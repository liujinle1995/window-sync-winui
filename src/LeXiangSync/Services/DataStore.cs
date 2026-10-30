using System.Collections.ObjectModel;
using System.Text.Json;
using LeXiangSync.Models;

namespace LeXiangSync.Services;

/// <summary>
/// 本地持久化。数据目录：<c>%LOCALAPPDATA%\LeXiangSync</c>，
/// 相比原 Web 版的 localStorage 可被用户直接备份、也便于方案导入导出。
/// </summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;

    public string Directory => _directory;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// 方案集合是全局唯一实例：配置页保存、方案页删除/导入都直接操作它，
    /// 两页通过同一个可观察集合自动保持一致，不再各自持有快照。
    /// </summary>
    public ObservableCollection<SyncPlan> Plans { get; private set; } = new();

    /// <summary>多开启动配置（沙箱、按钮坐标、排版），单独一个文件便于手动备份。</summary>
    public GameLaunchConfig Launch { get; private set; } = new();

    public DataStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LeXiangSync");
        System.IO.Directory.CreateDirectory(_directory);
        Load();
    }

    private void Load()
    {
        Settings = Read("settings.json", () => new AppSettings());
        Plans = new ObservableCollection<SyncPlan>(Read("plans.json", () => new List<SyncPlan>()));
        Launch = Read("launch.json", () => new GameLaunchConfig());
    }

    // ── 读写 ──────────────────────────────────────────────────────────────

    private T Read<T>(string fileName, Func<T> fallback) where T : class
    {
        var path = Path.Combine(_directory, fileName);
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Json) ?? fallback();
        }
        catch (Exception)
        {
            // 配置文件损坏时不应导致应用无法启动，直接回退默认值
            return fallback();
        }
    }

    /// <summary>
    /// 原子写入：先写临时文件再替换，避免写一半崩溃导致配置丢失。
    /// <para>
    /// 临时名带进程号：同时开了两个实例时不会抢同一个 <c>.tmp</c>（抢到的那次会抛 IOException，
    /// 而调用点遍布各处 —— 连视图模型构造函数里都有 —— 冒出去就是启动直接崩。
    /// </para>
    /// <para>
    /// 写失败也不往外抛：这次不落盘而已，内存里的状态照用，下次改配置再试。
    /// </para>
    /// </summary>
    private void Write<T>(string fileName, T value)
    {
        var path = Path.Combine(_directory, fileName);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";

        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 清不掉就留着，不值得为它再抛一次
        }
    }

    public void SaveSettings() => Write("settings.json", Settings);
    public void SavePlans() => Write("plans.json", Plans);
    public void SaveLaunch() => Write("launch.json", Launch);

    // ── 方案导入导出 ──────────────────────────────────────────────────────

    /// <summary>把方案导出为 JSON 文件（供备份 / 换机迁移）。</summary>
    public void ExportPlans(string filePath, IEnumerable<SyncPlan> plans)
    {
        var payload = new PlanArchive { Version = 1, Plans = plans.ToList() };
        File.WriteAllText(filePath, JsonSerializer.Serialize(payload, Json));
    }

    /// <summary>从 JSON 文件导入方案，返回实际导入条数。</summary>
    public int ImportPlans(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var archive = JsonSerializer.Deserialize<PlanArchive>(json, Json)
                      ?? throw new InvalidDataException("文件内容为空");
        var imported = archive.Plans ?? throw new InvalidDataException("文件格式不是有效的方案存档");

        var existing = Plans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in imported)
        {
            plan.Id = Guid.NewGuid().ToString("N");
            // 同名方案重命名后追加，不覆盖用户已有数据
            if (!existing.Add(plan.Name)) plan.Name = $"{plan.Name} (导入 {DateTime.Now:MMddHHmm})";
            Plans.Add(plan);
        }
        SavePlans();
        return imported.Count;
    }

    private sealed class PlanArchive
    {
        public int Version { get; set; }
        public List<SyncPlan> Plans { get; set; } = new();
    }
}
