using System;
using WinForge.Core.Services;

namespace WinForge.RealCapture;

/// <summary>
/// Console logger for the capture CLI. Mirrors the <see cref="ILoggerService"/>
/// contract so the exact same production services used by WinForge can be
/// composed without an App/WPF dependency.
/// </summary>
public sealed class ConsoleLoggerService : ILoggerService
{
    private readonly System.Collections.Generic.List<LogEntry> _entries = new();
    private readonly object _sync = new();

    public System.Collections.Generic.IReadOnlyList<LogEntry> Entries
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
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_sync)
        {
            _entries.Add(entry);
        }

        var prefix = level switch
        {
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN ",
            LogLevel.Debug => "DEBUG",
            _ => "INFO ",
        };

        var color = level switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray,
        };

        Console.ForegroundColor = color;
        Console.WriteLine($"[{prefix}] {message}");
        Console.ResetColor();
        EntryAdded?.Invoke(this, entry);
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
}
