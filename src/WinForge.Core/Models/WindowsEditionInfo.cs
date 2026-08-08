namespace WinForge.Core.Models;

/// <summary>
/// Metadata describing a single Windows edition found inside an image.
/// Phase 1 only defines the model; real values are populated in Phase 2+.
/// </summary>
public sealed class WindowsEditionInfo
{
    public int Index { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Architecture { get; set; }
}
