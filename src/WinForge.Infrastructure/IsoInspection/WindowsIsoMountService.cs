using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.IsoInspection;

/// <summary>
/// Windows-specific, read-only ISO mount backed by the built-in
/// <c>Mount-DiskImage</c> / <c>Dismount-DiskImage</c> PowerShell cmdlets. ISO
/// images mount read-only by nature, and every mount is paired with a dismount.
/// No DISM servicing, WIM handling, or content modification occurs here. The
/// PowerShell implementation detail is confined to Infrastructure so it never
/// leaks into Core or the ViewModels.
/// </summary>
public sealed class WindowsIsoMountService : IIsoMountService
{
    public Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default)
        => RunPowerShellAsync(BuildMountScript(isoPath), cancellationToken);

    public Task DismountAsync(string isoPath, CancellationToken cancellationToken = default)
        => RunPowerShellAsync(BuildDismountScript(isoPath), cancellationToken);

    private static string BuildMountScript(string isoPath)
    {
        var safe = EscapeSingleQuoted(isoPath);
        // Mount read-only and resolve the drive letter that Windows assigns.
        return "$img = Mount-DiskImage -ImagePath '" + safe + "' -PassThru; " +
               "$vol = $img | Get-Volume; " +
               "$letter = $vol.DriveLetter; " +
               "if ([string]::IsNullOrEmpty($letter)) { throw 'ISO mounted but no drive letter was assigned.' } " +
               "$letter + ':\\'";
    }

    private static string BuildDismountScript(string isoPath)
    {
        var safe = EscapeSingleQuoted(isoPath);
        // -ErrorAction SilentlyContinue makes a dismount on an image that is not
        // currently mounted a safe no-op instead of a thrown error, so best-effort
        // cleanup (e.g. after a cancelled mount) never surfaces a spurious failure.
        return "Dismount-DiskImage -ImagePath '" + safe + "' -ErrorAction SilentlyContinue | Out-Null; 'ok'";
    }

    private static string EscapeSingleQuoted(string value)
        => (value ?? string.Empty).Replace("'", "''");

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        // Encode the script as base64 (UTF-16LE) so no shell quoting/escaping is
        // needed and hostile paths cannot inject commands.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start powershell.exe for ISO mount.");
        }

        // Best-effort cancellation: terminate the process if the token fires.
        using (cancellationToken.Register(() =>
               {
                   try { process.Kill(); }
                   catch { /* process may already be gone */ }
               }))
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ISO mount operation failed (exit {process.ExitCode}): {stderr.Trim()}");
            }

            return stdout.Trim();
        }
    }
}
