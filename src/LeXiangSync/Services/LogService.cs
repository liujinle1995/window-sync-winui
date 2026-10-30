using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LeXiangSync.Models;
using Microsoft.UI.Dispatching;

namespace LeXiangSync.Services;

/// <summary>环形日志缓冲：UI 与 Rust 核心共享同一出口，自动封送到 UI 线程。</summary>
public sealed class LogService : ObservableObject
{
    private const int Capacity = 300;

    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>最新一条日志，用于底部状态栏单行显示。</summary>
    public LogEntry? Latest => Entries.Count > 0 ? Entries[^1] : null;

    public LogService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public void Add(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry { Message = message, Level = level };
        if (_dispatcher.HasThreadAccess) Append(entry);
        else _dispatcher.TryEnqueue(() => Append(entry));
    }

    public void Add(Exception ex, string prefix = "") =>
        Add($"{prefix}{ex.Message}".Trim(), LogLevel.Error);

    public void Clear()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Clear);
            return;
        }
        Entries.Clear();
        OnPropertyChanged(nameof(Latest));
    }

    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        OnPropertyChanged(nameof(Latest));
    }
}
