namespace WinForge.App.Services;

/// <summary>
/// Opens a folder in the OS shell (e.g. Windows Explorer) so the user can inspect
/// a produced artifact. Lives in the App project because it launches a process;
/// Core never references it. The concrete implementation is Windows-only.
/// </summary>
public interface IFileLauncher
{
    /// <summary>Opens the given folder in the system file browser.</summary>
    void OpenFolder(string folderPath);
}
