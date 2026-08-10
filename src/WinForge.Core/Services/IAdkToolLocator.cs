namespace WinForge.Core.Services;

/// <summary>
/// Locates the Windows ADK <c>oscdimg.exe</c> tool used to build bootable ISOs.
/// The implementation searches the documented ADK install locations. The build
/// pipeline refuses to start (with a clear user message) when the tool is absent.
/// </summary>
public interface IAdkToolLocator
{
    /// <summary>Returns the full path to <c>oscdimg.exe</c>, or null when not found.</summary>
    string? FindOscdimg();

    /// <summary>True when <c>oscdimg.exe</c> can be located.</summary>
    bool IsAvailable();
}
