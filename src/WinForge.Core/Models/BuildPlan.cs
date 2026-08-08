using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// The configuration model that drives a customized Windows build. In Phase 1
/// this is a skeleton: it can be instantiated and carries a preset name plus a
/// generic settings bag. Later phases (especially Phase 5 — Configuration
/// Engine) expand it with strongly-typed customization areas. Presets are data
/// loaded into this model, never separate execution paths.
/// </summary>
public sealed class BuildPlan
{
    public string? PresetName { get; set; }
    public string? TargetEdition { get; set; }
    public Dictionary<string, object> Settings { get; set; } = new();
}
