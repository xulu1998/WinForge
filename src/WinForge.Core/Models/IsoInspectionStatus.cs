namespace WinForge.Core.Models;

/// <summary>
/// Outcome of an ISO inspection run. <see cref="NotInspected"/> is the initial
/// state, <see cref="Inspecting"/> is transient (owned by the ViewModel), and
/// the final result is <see cref="Completed"/> or <see cref="Failed"/>.
/// </summary>
public enum IsoInspectionStatus
{
    NotInspected,
    Inspecting,
    Completed,
    Failed
}
