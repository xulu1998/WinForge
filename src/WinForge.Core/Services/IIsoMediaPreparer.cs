using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Copies the original ISO media tree into an isolated WinForge-owned build
/// workspace and replaces its install image payload with the customized final
/// WIM. The original ISO is read (mounted read-only) and never modified. The
/// dual-boot files required by oscdimg are validated in the copied tree so a
/// missing file fails the build with a clear error rather than producing a
/// non-bootable ISO.
/// </summary>
public interface IIsoMediaPreparer
{
    /// <summary>
    /// Copies the media tree and replaces the install image. On success
    /// <see cref="MediaPrepareResult.MediaRoot"/> and
    /// <see cref="MediaPrepareResult.InstallImagePath"/> identify the prepared tree
    /// and <see cref="MediaPrepareResult.BootFilesPresent"/> reports whether the
    /// BIOS/UEFI boot files are present.
    /// </summary>
    Task<MediaPrepareResult> PrepareAsync(MediaPrepareRequest request, CancellationToken cancellationToken = default);
}
