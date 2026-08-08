namespace WinForge.Core.Models;

/// <summary>
/// Coarse classification of a selected ISO after read-only inspection.
/// Step 2.1 deliberately does NOT claim edition / SKU / version recognition —
/// if the evidence is insufficient the result is <see cref="Unknown"/> rather
/// than a guess.
/// </summary>
public enum IsoDetectedType
{
    Unknown,
    WindowsIsoCandidate
}
