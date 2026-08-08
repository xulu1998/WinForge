using System.ComponentModel;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Mutable, observable application state shared across the UI. Implementations
/// must raise <see cref="INotifyPropertyChanged.PropertyChanged"/> so views can
/// react to changes (e.g. source image selection).
/// </summary>
public interface IAppState : INotifyPropertyChanged
{
    /// <summary>Path to the selected Windows ISO/install source, or null.</summary>
    string? SourceImagePath { get; set; }

    /// <summary>Currently selected Windows edition, or null.</summary>
    WindowsEditionInfo? SelectedEdition { get; set; }

    /// <summary>
    /// The active build configuration (skeleton in Phase 1). Presets are loaded
    /// into this model later; they are data, not separate code paths.
    /// </summary>
    BuildPlan Configuration { get; }

    /// <summary>Human-readable label for the active configuration preset.</summary>
    string ConfigurationLabel { get; }

    /// <summary>Current build status.</summary>
    BuildStatus BuildStatus { get; set; }
}
