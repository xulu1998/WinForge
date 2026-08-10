namespace WinForge.Core.Models;

/// <summary>
/// A single progress report emitted by <see cref="IBuildService.BuildAsync"/>.
/// The <see cref="Phase"/> is the authoritative build state; <see cref="Message"/>
/// is a high-level, English, log-style note (the UI localizes a status caption
/// from <see cref="Phase"/> independently). <see cref="Percent"/> is a coarse
/// indicator (0–100); a negative value signals an indeterminate phase.
/// </summary>
public sealed class BuildProgress
{
    /// <summary>The authoritative build phase this report represents.</summary>
    public BuildState Phase { get; init; } = BuildState.NotStarted;

    /// <summary>High-level, English, log-style note for this phase.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Coarse progress percentage (0–100). Negative means indeterminate.</summary>
    public int Percent { get; init; } = -1;

    /// <summary>True when <see cref="Percent"/> is not a meaningful value.</summary>
    public bool IsIndeterminate => Percent < 0;

    public static BuildProgress Of(BuildState phase, string message, int percent = -1)
        => new() { Phase = phase, Message = message, Percent = percent };
}
