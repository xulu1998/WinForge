using System;
using System.Collections.Generic;

namespace WinForge.Core.Services;

/// <summary>
/// Minimal logging contract used across WinForge. Implementations decide where
/// entries are stored (in-memory, file, etc.). Intentionally small to avoid a
/// heavy third-party logging dependency.
/// </summary>
public interface ILoggerService
{
    /// <summary>All recorded entries, in insertion order.</summary>
    IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>Raised when a new entry is recorded.</summary>
    event EventHandler<LogEntry>? EntryAdded;

    void Log(LogLevel level, string message);
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
