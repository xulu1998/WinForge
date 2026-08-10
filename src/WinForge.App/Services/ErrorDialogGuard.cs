using System;
using System.Collections.Generic;
using System.Threading;

namespace WinForge.App.Services;

/// <summary>
/// Coalesces repeated fatal-error dialogs so a single root cause produces at most
/// one user-visible error path.
///
/// <para>
/// Without this guard, an error that repeats on every dispatcher iteration (for
/// example a binding or render that keeps throwing after entering a step) would
/// spawn an unbounded storm of MessageBoxes. That storm both harasses the user and
/// can escalate into a process-terminating stack overflow (Windows exception code
/// 0xc00000fd). The first occurrence of each distinct error is still surfaced to
/// the user and is ALWAYS logged; only rapid repeats and the total dialog count are
/// throttled. This never swallows the underlying exception — callers must still log it.
/// </para>
/// </summary>
public static class ErrorDialogGuard
{
    // Rapid repeats of the SAME fingerprint within this window are coalesced.
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(500);

    // Hard cap on how many error dialogs a single process will ever show. Once
    // reached, further errors are logged only (no additional dialogs).
    private const int MaxDialogs = 3;

    private static readonly Dictionary<string, DateTime> _lastShown = new();
    private static int _shown;
    private static readonly object _gate = new();

    /// <summary>
    /// Returns <c>true</c> when the error identified by <paramref name="fingerprint"/>
    /// should be shown to the user. The caller MUST still log the error regardless of
    /// the return value. Thread-safe.
    /// </summary>
    public static bool ShouldShow(string? fingerprint)
    {
        var key = fingerprint ?? "unknown";
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (_shown >= MaxDialogs)
            {
                return false;
            }

            if (_lastShown.TryGetValue(key, out var previous) && now - previous < Cooldown)
            {
                return false;
            }

            _lastShown[key] = now;
            _shown++;
            return true;
        }
    }

    /// <summary>Test / diagnostic helper: reset all throttling state.</summary>
    public static void Reset()
    {
        lock (_gate)
        {
            _lastShown.Clear();
            _shown = 0;
        }
    }
}
