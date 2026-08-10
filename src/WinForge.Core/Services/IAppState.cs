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
    /// The durable selected-image workspace, or null when no edition has been
    /// targeted. It references the original ISO and the image's relative path
    /// inside it — never a temporary mounted drive. Selecting an edition (or
    /// changing it) creates/updates this; selecting a new ISO resets it so no
    /// stale index from a previous ISO survives.
    /// </summary>
    ImageWorkspace? CurrentImageWorkspace { get; set; }

    /// <summary>
    /// The active offline image servicing session (Phase 3 Step 3.2), or null
    /// when none exists. Represents the isolated working image and its mount
    /// lifecycle. Lifecycle rules: selecting a new ISO or a different edition
    /// invalidates a non-mounted prepared workspace; an actively mounted session
    /// must be unmounted/discarded before the source ISO or edition may change —
    /// it is never silently forgotten or destroyed.
    /// </summary>
    ImageServicingWorkspace? CurrentServicingWorkspace { get; set; }

    /// <summary>
    /// The active declarative customization plan (Phase 3 Step 3.3), or null
    /// when none has been assembled. It is built from discoveries against the
    /// mounted working image and executed only while the servicing workspace is
    /// Mounted. All operations target the isolated working image only.
    /// </summary>
    CustomizationPlan? CurrentCustomizationPlan { get; set; }

    /// <summary>
    /// Runtime state of the customization engine, surfaced so the UI can show a
    /// busy / ready / result state and disable controls during discovery/execution.
    /// </summary>
    CustomizationExecutionState CustomizationExecutionState { get; set; }

    /// <summary>
    /// The most recent discovery inventory against the mounted working image, or
    /// null before any discovery pass. Avoids dumping huge raw DISM text into
    /// AppState — only structured, categorized items are stored.
    /// </summary>
    DiscoveryInventory? DiscoveredInventory { get; set; }

    /// <summary>
    /// The active build configuration (skeleton in Phase 1). Presets are loaded
    /// into this model later; they are data, not separate code paths.
    /// </summary>
    BuildPlan Configuration { get; }

    /// <summary>Human-readable label for the active configuration preset.</summary>
    string ConfigurationLabel { get; }

    /// <summary>Current build status (durable Build / ISO export lifecycle state).</summary>
    BuildState BuildStatus { get; set; }
}
