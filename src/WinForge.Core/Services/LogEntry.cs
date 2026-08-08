using System;

namespace WinForge.Core.Services;

/// <summary>
/// A single structured log entry produced by the application.
/// </summary>
/// <param name="Timestamp">When the entry was recorded (UTC offset).</param>
/// <param name="Level">Severity of the entry.</param>
/// <param name="Message">Human-readable message.</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message);
