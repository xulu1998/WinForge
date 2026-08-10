namespace WinForge.Core.Models;

/// <summary>
/// Durable, explicit lifecycle state of a Build / ISO export operation. The
/// states form a strict state machine driven by <see cref="IBuildService"/>;
/// the UI and <see cref="IAppState.BuildStatus"/> are derived purely from this
/// value so a build can never be reported successful while an earlier terminal
/// failure or cancellation actually occurred.
///
/// <para>Valid transitions:</para>
/// NotStarted → Preflight → CommittingImage → ExportingImage → PreparingMedia
///   → BuildingIso → Verifying → Completed
/// any runnable state (Preflight … Verifying) → Failed
/// any runnable state (Preflight … Verifying) → Cancelled
/// </summary>
public enum BuildState
{
    /// <summary>No build has been started.</summary>
    NotStarted,

    /// <summary>Validating inputs, source media, working image, and tooling.</summary>
    Preflight,

    /// <summary>Committing the customized working WIM (DISM /Unmount-Image /Commit).</summary>
    CommittingImage,

    /// <summary>Exporting the committed working image into a clean final install.wim.</summary>
    ExportingImage,

    /// <summary>Copying the original ISO media tree and replacing the install image.</summary>
    PreparingMedia,

    /// <summary>Rebuilding the bootable ISO (oscdimg).</summary>
    BuildingIso,

    /// <summary>Verifying the produced ISO and that no image remains mounted.</summary>
    Verifying,

    /// <summary>The build completed and the final ISO is at the chosen output path.</summary>
    Completed,

    /// <summary>The build failed at a specific phase; see the result for details.</summary>
    Failed,

    /// <summary>The build was cancelled; partial output was cleaned where safe.</summary>
    Cancelled
}

/// <summary>
/// Product policy for what editions the output ISO contains. WinForge documents
/// exactly one policy for this phase (see ADR-038): a single customized edition.
/// </summary>
public enum BuildMode
{
    /// <summary>
    /// The output ISO contains ONLY the customized selected edition. Simpler,
    /// deterministic, and easier to verify; it aligns with the isolated
    /// single-index working-image architecture.
    /// </summary>
    SingleCustomizedEdition
}

/// <summary>
/// How a build should react when the requested output path already exists.
/// </summary>
public enum BuildOverwritePolicy
{
    /// <summary>Refuse to build; report a clear error.</summary>
    Fail,

    /// <summary>Generate a unique name (e.g. append <c>(1)</c>, <c>(2)</c>).</summary>
    GenerateUniqueName,

    /// <summary>Overwrite the existing file (only when the user explicitly opts in).</summary>
    Overwrite
}
