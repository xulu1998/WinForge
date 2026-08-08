using System;
using System.Collections.ObjectModel;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Logging;

/// <summary>
/// In-memory implementation of <see cref="ILoggerService"/>. Keeps a bounded
/// rolling list of entries and raises <see cref="ILoggerService.EntryAdded"/>
/// for live UI binding. Entries are stored in an
/// <see cref="ObservableCollection{T}"/> so WPF lists update in real time.
/// A file-backed logger can replace this later without changing Core or UI.
/// </summary>
public sealed class InMemoryLoggerService : ILoggerService
{
    // Cap to avoid unbounded memory growth during long sessions.
    private const int MaxEntries = 2000;

    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly object _sync = new();

    public System.Collections.Generic.IReadOnlyList<LogEntry> Entries => _entries;

    public event EventHandler<LogEntry>? EntryAdded;

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message ?? string.Empty);

        lock (_sync)
        {
            _entries.Add(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
}
