namespace WinForge.Core.Models;

/// <summary>
/// Readiness of a durable <see cref="ImageWorkspace"/>. The application reads the
/// status (not a UI string) to decide how to present the selected image.
/// </summary>
public enum ImageWorkspaceStatus
{
    /// <summary>Essential durable identifiers are missing (no selection, failed metadata, unknown image type, …).</summary>
    NotReady,

    /// <summary>All essential durable identifiers exist and the descriptor is valid for downstream use.</summary>
    Ready,

    /// <summary>A selection was attempted but rejected (e.g. selected index is not present in the inspected image).</summary>
    Invalid
}
