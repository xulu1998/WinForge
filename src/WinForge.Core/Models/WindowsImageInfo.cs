using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Inspected metadata of a Windows image source. Phase 1 establishes the model
/// skeleton only. Real inspection (architecture, version, editions, …) is
/// implemented in Phase 2 — ISO Inspection. Until then, unknown fields stay null
/// or empty and the UI shows "Not detected".
/// </summary>
public sealed class WindowsImageInfo
{
    public string? SourcePath { get; set; }
    public WindowsImageType ImageType { get; set; } = WindowsImageType.Unknown;
    public string? Architecture { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
    public long Size { get; set; }
    public List<WindowsEditionInfo> Editions { get; set; } = new();
}
