using System.Diagnostics;

namespace WinForge.App.Services;

/// <summary>
/// Windows implementation that opens a folder via the shell. Opening the folder
/// is a convenience affordance, never a required workflow step, so failures
/// (e.g. no shell in a headless environment) are swallowed rather than surfaced.
/// </summary>
public sealed class WindowsFileLauncher : IFileLauncher
{
    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Shell may be unavailable (headless/test). Opening a folder is optional.
        }
    }
}
