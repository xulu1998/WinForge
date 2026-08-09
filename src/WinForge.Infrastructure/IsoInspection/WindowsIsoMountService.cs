using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.IsoInspection;

/// <summary>
/// Windows-specific, read-only ISO mount backed by the built-in
/// <c>Mount-DiskImage</c> / <c>Get-DiskImage</c> / <c>Get-Volume</c> /
/// <c>Dismount-DiskImage</c> PowerShell cmdlets. ISO images mount read-only by
/// nature, and every mount is paired with a dismount. No DISM servicing, WIM
/// handling, or content modification occurs here. The PowerShell implementation
/// detail is confined to Infrastructure so it never leaks into Core or the
/// ViewModels.
/// </summary>
/// <remarks>
/// Robustness notes (root-caused from real-desktop intermittent failures where
/// the identical manual PowerShell command succeeded):
/// <list type="bullet">
///   <item><description>Attach and drive-letter resolution are separate
///     operations. <c>Mount-DiskImage</c> can return successfully while Windows
///     has not yet assigned a drive letter, so we poll <c>Get-Volume</c> in a
///     bounded, cancellation-aware retry instead of assuming the letter is
///     available immediately.</description></item>
///   <item><description>Scripts set <c>$ErrorActionPreference='Stop'</c> and use
///     <c>-ErrorAction Stop</c> so non-terminating cmdlet errors become real
///     failures instead of silent partial results.</description></item>
///   <item><description>If the image attaches but no drive letter ever appears,
///     we best-effort dismount so the ISO is never left attached.</description></item>
///   <item><description>CLIXML / progress noise is stripped from the diagnostic
///     text surfaced to the user; exit code, meaningful error text, and
///     stdout/stderr are preserved for technical diagnostics.</description></item>
/// </list>
/// </remarks>
public sealed class WindowsIsoMountService : IIsoMountService
{
    // Bounded readiness retry. Windows typically assigns the letter within a
    // fraction of a second; 10s is a generous ceiling that still fails fast.
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultPollDelay = TimeSpan.FromMilliseconds(250);

    private readonly IPowerShellRunner _runner;
    private readonly ILoggerService? _logger;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _pollDelay;

    public WindowsIsoMountService(
        IPowerShellRunner? runner = null,
        ILoggerService? logger = null,
        TimeSpan? readinessTimeout = null,
        TimeSpan? pollDelay = null)
    {
        _runner = runner ?? new RealPowerShellRunner();
        _logger = logger;
        _readinessTimeout = readinessTimeout ?? DefaultReadinessTimeout;
        _pollDelay = pollDelay ?? DefaultPollDelay;
    }

    public async Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        _logger?.Info($"ISO mount requested: {isoPath}");

        // 1) Attach the disk image. Fail fast on attach errors.
        PowerShellResult attach;
        try
        {
            attach = await _runner.RunAsync(BuildMountScript(isoPath), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before/while attaching: nothing to clean up.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"ISO mount failed (attach error): {ex.Message}");
            throw;
        }

        if (attach.ExitCode != 0)
        {
            var msg = BuildFailureMessage("mount", attach.ExitCode, attach);
            _logger?.Error($"ISO mount failed (attach): {msg}");
            throw new InvalidOperationException(msg);
        }

        _logger?.Info($"disk image attached: {isoPath}");

        // 2) Wait (bounded, cancellation-aware) for the drive letter to appear.
        _logger?.Info($"waiting for volume: {isoPath}");
        string? root = null;
        var sw = Stopwatch.StartNew();
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resolve = await _runner.RunAsync(BuildResolveScript(isoPath), cancellationToken);
                var candidate = (resolve.StandardOutput ?? string.Empty).Trim();
                if (candidate.Length != 0 && candidate.EndsWith(":\\", StringComparison.Ordinal))
                {
                    root = candidate;
                    break;
                }

                if (sw.Elapsed >= _readinessTimeout)
                {
                    break;
                }

                await Task.Delay(_pollDelay, cancellationToken);
            }
            while (sw.Elapsed < _readinessTimeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting: do not leave the ISO attached.
            await DismountBestEffortAsync(isoPath);
            throw;
        }

        if (root is null)
        {
            _logger?.Error(
                $"mount failed: no drive letter assigned within {_readinessTimeout.TotalSeconds:0} " +
                $"seconds for {isoPath}; dismounting.");
            await DismountBestEffortAsync(isoPath);
            throw new InvalidOperationException(
                $"The ISO was mounted but no drive letter was assigned within " +
                $"{_readinessTimeout.TotalSeconds:0} seconds. The ISO has been dismounted.");
        }

        _logger?.Info($"drive letter resolved: {root}");
        return root;
    }

    public async Task DismountAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        // Best-effort: a dismount on an already-unmounted image is a safe no-op,
        // and failures here are logged, never propagated (the caller relies on
        // this to guarantee cleanup in a finally block).
        try
        {
            var result = await _runner.RunAsync(BuildDismountScript(isoPath), cancellationToken);
            if (result.ExitCode != 0)
            {
                _logger?.Warning(
                    $"ISO dismount returned exit {result.ExitCode}: {NormalizePowerShellError(result.StandardError)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"ISO dismount failed (manual cleanup may be required): {NormalizePowerShellError(ex.Message)}");
        }
    }

    private async Task DismountBestEffortAsync(string isoPath)
    {
        _logger?.Info($"cleanup/dismount after failed mount: {isoPath}");
        try
        {
            var result = await _runner.RunAsync(BuildDismountScript(isoPath), CancellationToken.None);
            if (result.ExitCode != 0)
            {
                _logger?.Warning(
                    $"best-effort ISO dismount returned exit {result.ExitCode}: " +
                    $"{NormalizePowerShellError(result.StandardError)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"best-effort ISO dismount failed: {NormalizePowerShellError(ex.Message)}");
        }
    }

    /// <summary>Attach the ISO and confirm the operation reached the OS.</summary>
    public static string BuildMountScript(string isoPath)
    {
        var safe = EscapeSingleQuoted(isoPath);
        return "$ErrorActionPreference = 'Stop'; " +
               "$img = Mount-DiskImage -ImagePath '" + safe + "' -PassThru -ErrorAction Stop; " +
               "$img.ImagePath";
    }

    /// <summary>
    /// Resolve the drive-letter root for an already-mounted ISO, or return an
    /// empty string when the letter is not yet assigned. Kept separate from the
    /// attach so the caller can poll it (the letter is not guaranteed to exist
    /// the instant <c>Mount-DiskImage</c> returns).
    /// </summary>
    public static string BuildResolveScript(string isoPath)
    {
        var safe = EscapeSingleQuoted(isoPath);
        return "$ErrorActionPreference = 'Stop'; " +
               "$dev = (Get-DiskImage -ImagePath '" + safe + "' -ErrorAction Stop).DevicePath; " +
               "$vol = Get-Volume -DevicePath $dev -ErrorAction SilentlyContinue; " +
               "if ($vol -and -not [string]::IsNullOrEmpty($vol.DriveLetter)) { $vol.DriveLetter + ':\\' } else { '' }";
    }

    public static string BuildDismountScript(string isoPath)
    {
        var safe = EscapeSingleQuoted(isoPath);
        // -ErrorAction SilentlyContinue makes a dismount on an image that is not
        // currently mounted a safe no-op instead of a thrown error, so best-effort
        // cleanup (e.g. after a cancelled mount) never surfaces a spurious failure.
        return "Dismount-DiskImage -ImagePath '" + safe + "' -ErrorAction SilentlyContinue | Out-Null; 'ok'";
    }

    /// <summary>
    /// Escapes a value for inclusion in a single-quoted PowerShell string. Every
    /// single quote is doubled; the surrounding quotes in the calling script
    /// then treat the whole value as a literal, so a hostile path cannot break
    /// out and inject commands. EncodedCommand base64-wraps the entire script,
    /// so no shell quoting is involved either.
    /// </summary>
    private static string EscapeSingleQuoted(string value)
        => (value ?? string.Empty).Replace("'", "''");

    private static string BuildFailureMessage(string op, int exitCode, PowerShellResult result)
    {
        var err = NormalizePowerShellError(result.StandardError);
        var detail = !string.IsNullOrWhiteSpace(err)
            ? err
            : !string.IsNullOrWhiteSpace(NormalizePowerShellError(result.StandardOutput))
                ? NormalizePowerShellError(result.StandardOutput)
                : "No diagnostic output was captured.";
        return $"ISO {op} failed (exit {exitCode}). {detail}";
    }

    /// <summary>
    /// Strips PowerShell CLIXML / progress noise (e.g. <c>#&lt; CLIXML</c> and
    /// "Preparing modules for first use...") from diagnostic text while
    /// preserving the meaningful error message and the raw output as a fallback.
    /// </summary>
    public static string NormalizePowerShellError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        // Pull the human-readable strings out of a CLIXML envelope if present.
        if (raw.Contains("#< CLIXML", StringComparison.Ordinal))
        {
            foreach (Match m in ClixmlStringRegex.Matches(raw))
            {
                var text = StripAnsi(m.Groups[1].Value).Trim();
                if (text.Length != 0)
                {
                    sb.AppendLine(text);
                }
            }
        }

        // Keep any plain (non-CLIXML) lines, dropping known noise.
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("#< CLIXML", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("<?xml", StringComparison.Ordinal) ||
                trimmed.StartsWith("<Objs", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.Contains("Preparing modules for first use", StringComparison.Ordinal))
            {
                continue;
            }

            if (ProgressLineRegex.IsMatch(trimmed))
            {
                continue;
            }

            sb.AppendLine(trimmed);
        }

        var normalized = sb.ToString().Trim();
        // Never return completely empty: fall back to the raw text so diagnostics
        // are preserved even if our stripping logic missed the format.
        return normalized.Length == 0 ? raw.Trim() : normalized;
    }

    private static string StripAnsi(string value)
        => AnsiRegex.Replace(value, string.Empty);

    private static readonly Regex ClixmlStringRegex =
        new("<S>(.*?)</S>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProgressLineRegex =
        new(@"^\s*\d+%\s*\[", RegexOptions.Compiled);

    private static readonly Regex AnsiRegex =
        new("\x1b\\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>
    /// Abstraction over running a PowerShell script and capturing its result.
    /// The default implementation shells out to <c>powershell.exe</c>; tests
    /// supply a fake so no real ISO, elevation, or PowerShell is required.
    /// </summary>
    public interface IPowerShellRunner
    {
        Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken);
    }

    /// <summary>Outcome of a single PowerShell script execution.</summary>
    public sealed class PowerShellResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
    }

    /// <summary>
    /// Real runner that executes the script via <c>powershell.exe -EncodedCommand</c>.
    /// The script is base64-encoded (UTF-16LE) so no shell quoting/escaping is
    /// needed and hostile paths cannot inject commands.
    /// </summary>
    private sealed class RealPowerShellRunner : IPowerShellRunner
    {
        public async Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken)
        {
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
                await process.WaitForExitAsync(cancellationToken);

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();

                return new PowerShellResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = stdout,
                    StandardError = stderr
                };
            }
        }
    }
}
