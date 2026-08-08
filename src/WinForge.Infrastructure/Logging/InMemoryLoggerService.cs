using System;
using System.Collections.Generic;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Logging;

/// <summary>
/// In-memory implementation of <see cref="ILoggerService"/>. Entries are stored
/// in a plain <see cref="List{T}"/> guarded by a lock, so the logger is safe to
/// call from any thread — the UI thread, a background <see cref="Task"/>, or a
/// future DISM/Process worker. <see cref="Entries"/> returns a point-in-time
/// snapshot; the <see cref="EntryAdded"/> event is raised on the calling thread.
///
/// This type deliberately avoids WPF types (e.g. <c>ObservableCollection</c>)
/// and the Dispatcher, so Infrastructure never depends on the UI thread. The
/// WPF <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> used
/// for live binding lives only in <c>LogsViewModel</c> (the App project), which
/// marshals cross-thread updates back to the UI thread. A file/ETW sink can
/// replace this implementation later without touching Core or the UI.
/// </summary>
public sealed class InMemoryLoggerService : ILoggerService
{
    // Cap to avoid unbounded memory growth during long sessions.
    private const int MaxEntries = 2000;

    private readonly List<LogEntry> _entries = new();
    private readonly object _sync = new();

    /// <summary>
    /// A point-in-time, thread-safe snapshot of all recorded entries in
    /// insertion order. Each call returns a fresh copy; callers must not assume
    /// identity with the internal store, nor mutate it.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message ?? string.Empty);

        // Update the store under the lock, then notify subscribers *outside* the
        // lock. Notifying inside the lock risks re-entrancy and deadlocks if a
        // subscriber logs again while the lock is held.
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
