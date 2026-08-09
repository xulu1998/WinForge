using System.IO;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Step 3.1 closeout — regression guard for application elevation.
///
/// Real desktop validation showed that the Phase 2 DISM inspection path fails
/// with DISM exit code 740 (ERROR_ELEVATION_REQUIRED) when WinForge.App.exe is
/// launched without administrator rights. WinForge.App therefore declares the
/// elevation requirement directly in its embedded application manifest
/// (<c>requestedExecutionLevel level="requireAdministrator"</c>).
///
/// This test is a configuration-presence guard only: it reads the SOURCE
/// application manifest that gets embedded into WinForge.App.exe and verifies
/// the elevation requirement is present, so the setting cannot silently
/// disappear in a future edit. It does NOT attempt to launch the EXE or perform
/// any runtime UAC check.
/// </summary>
public class AppManifestElevationTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void ApplicationManifest_RequiresAdministrator()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var manifestPath = Path.Combine(repoRoot!, "src", "WinForge.App", "app.manifest");
        Assert.True(
            File.Exists(manifestPath),
            $"Expected the WinForge.App application manifest at '{manifestPath}'.");

        var content = File.ReadAllText(manifestPath);

        Assert.True(
            content.Contains("level=\"requireAdministrator\""),
            "WinForge.App must declare requestedExecutionLevel level=\"requireAdministrator\" in its embedded manifest.");

        Assert.True(
            content.Contains("uiAccess=\"false\""),
            "uiAccess must remain false for the WinForge.App manifest.");
    }
}
