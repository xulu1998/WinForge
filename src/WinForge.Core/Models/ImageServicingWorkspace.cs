using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace WinForge.Core.Models;

/// <summary>
/// A durable description of an offline WIM servicing session (Phase 3 Step 3.2).
/// It captures the selected edition's source identifiers (so the session can be
/// re-derived after the ISO is dismounted) plus WinForge-owned servicing state:
/// where the isolated working image and mount point live, the working image
/// container format, the current lifecycle <see cref="State"/>, and the mapping
/// between the source index and the working-image index.
///
/// <para>
/// The working image is always an isolated copy. For a selected source index N
/// (inside the original install.wim/install.esd) the servicing layer exports ONLY
/// that index into a standalone working image whose own index is 1. The source
/// <see cref="SelectedIndex"/> is preserved alongside the <see cref="WorkingIndex"/>
/// (always 1) so the distinction is never lost.
/// </para>
///
/// <para>
/// This descriptor deliberately does NOT store a temporary ISO mount drive letter.
/// The durable source is the original <see cref="SourceIsoPath"/> and the image's
/// relative path inside it (<see cref="SourceImageRelativePath"/>). All servicing
/// artifacts live under WinForge-owned directories (<see cref="WorkingDirectory"/>,
/// <see cref="WorkingImagePath"/>, <see cref="MountDirectory"/>).
/// </para>
/// </summary>
public sealed class ImageServicingWorkspace : INotifyPropertyChanged
{
    // ---- Source identifiers (mirror ImageWorkspace; never a temp mount root) ----

    /// <summary>Path to the original Windows ISO the working image was derived from.</summary>
    public string? SourceIsoPath { get; set; }

    /// <summary>
    /// Relative path of the source install image inside the ISO, e.g.
    /// <c>sources\install.wim</c> or <c>sources\install.esd</c>. Durable across
    /// ISO dismount; never a temporary mounted drive root.
    /// </summary>
    public string? SourceImageRelativePath { get; set; }

    /// <summary>Container format of the source install image.</summary>
    public WindowsImageType SourceImageType { get; set; } = WindowsImageType.Unknown;

    /// <summary>1-based index of the selected edition inside the SOURCE image.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>Display name of the selected edition, e.g. <c>Windows 11 Pro</c>.</summary>
    public string? SelectedEditionName { get; set; }

    /// <summary>Processor architecture of the selected edition (e.g. <c>x64</c>).</summary>
    public string? Architecture { get; set; }

    /// <summary>Windows build number of the selected edition, e.g. <c>26200</c>.</summary>
    public string? Build { get; set; }

    // ---- Servicing-specific state (WinForge-owned) ----

    /// <summary>
    /// Root of this servicing session's WinForge-owned working area, e.g.
    /// <c>%LOCALAPPDATA%\WinForge\Workspaces\&lt;id&gt;</c>. Contains
    /// <c>image\</c> and <c>mount\</c> sub-directories.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Absolute path of the isolated working image (always a WIM), e.g.
    /// <c>…\Workspaces\&lt;id&gt;\image\install.wim</c>. This is the only image
    /// that is ever mounted or serviced — never the source.
    /// </summary>
    public string? WorkingImagePath { get; set; }

    /// <summary>
    /// Absolute path of the dedicated, empty mount directory for this session, e.g.
    /// <c>…\Workspaces\&lt;id&gt;\mount</c>. Associated with exactly one session.
    /// </summary>
    public string? MountDirectory { get; set; }

    /// <summary>Container format of the working image. Always WIM after export.</summary>
    public WindowsImageType WorkingImageType { get; set; } = WindowsImageType.Wim;

    /// <summary>Current lifecycle <see cref="ServicingWorkspaceState"/>.</summary>
    public ServicingWorkspaceState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            // HasError is derived from State; notify so any binding stays correct.
            OnPropertyChanged(nameof(HasError));
        }
    }

    private ServicingWorkspaceState _state = ServicingWorkspaceState.NotPrepared;

    /// <summary>
    /// 1-based index of the selected edition inside the WORKING image. After a
    /// single-index export this is always 1, while the original
    /// <see cref="SelectedIndex"/> stays as the source index N.
    /// </summary>
    public int WorkingIndex { get; set; } = 1;

    /// <summary>UTC timestamp when this servicing session was created/prepared.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Human-readable description of the last failure, if <see cref="State"/> is
    /// <see cref="ServicingWorkspaceState.Failed"/>. Empty otherwise. Never holds
    /// raw DISM output verbatim — high-level only.
    /// </summary>
    public string? LastError
    {
        get => _lastError;
        set
        {
            if (_lastError == value)
            {
                return;
            }

            _lastError = value;
            OnPropertyChanged();
        }
    }

    private string? _lastError;

    /// <summary>
    /// Raised when a mutable property (<see cref="State"/>, <see cref="LastError"/>)
    /// changes in place. The servicing layer mutates <see cref="State"/> on the same
    /// workspace instance it returns, so <see cref="IAppState.CurrentServicingWorkspace"/>
    /// can receive an identical reference; this event lets <c>AppState</c> forward the
    /// transition to workflow-level listeners (e.g. <see cref="WorkflowViewModel"/>,
    /// which derives step availability from <see cref="State"/> and would otherwise
    /// never observe a Prepared→Mounted change).
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// True when <see cref="State"/> is a terminal failure that requires recovery
    /// before any further operation. Mirrors <see cref="ServicingWorkspaceState.Failed"/>.
    /// </summary>
    public bool HasError => State == ServicingWorkspaceState.Failed;

    /// <summary>
    /// Normalizes a relative path to a durable, Windows-style form (collapses
    /// <c>/</c> and <c>\</c> to a single <c>\</c>, trims separators). Reuses the
    /// same rule as <see cref="ImageWorkspace.NormalizeRelativePath"/>.
    /// </summary>
    public static string NormalizeRelativePath(string? relativePath)
        => ImageWorkspace.NormalizeRelativePath(relativePath);

    /// <summary>
    /// Returns the expected working-image file name for a given source type:
    /// <c>install.wim</c> for both WIM and ESD sources, because the export target
    /// is always a WIM.
    /// </summary>
    public static string WorkingImageFileName(WindowsImageType sourceType)
        => "install.wim";
}
