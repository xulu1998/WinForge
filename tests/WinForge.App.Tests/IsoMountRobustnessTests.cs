using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression coverage for the real-desktop defect where ISO mount occasionally
/// failed inside WinForge although the identical manual PowerShell command
/// succeeded. The defect was assuming the drive letter is available the instant
/// <c>Mount-DiskImage</c> returns, with no <c>$ErrorActionPreference='Stop'</c>,
/// no <c>-ErrorAction Stop</c>, and no readiness retry. These tests drive the
/// real <see cref="WindowsIsoMountService"/> orchestration through a fake
/// <see cref="WindowsIsoMountService.IPowerShellRunner"/> so no real ISO,
/// elevation, or PowerShell is required.
/// </summary>
public sealed class IsoMountRobustnessTests
{
    private sealed class FakeRunner : WindowsIsoMountService.IPowerShellRunner
    {
        public List<string> ScriptsRun { get; } = new();
        public int ResolveCalls { get; private set; }

        public Func<string, WindowsIsoMountService.PowerShellResult>? AttachHandler;
        public Func<string, WindowsIsoMountService.PowerShellResult>? ResolveHandler;

        public Task<WindowsIsoMountService.PowerShellResult> RunAsync(string script, CancellationToken ct)
        {
            ScriptsRun.Add(script);

            if (script.Contains("Mount-DiskImage") && script.Contains("-PassThru"))
            {
                return Task.FromResult((AttachHandler ?? DefaultAttach)(script));
            }

            if (script.Contains("Get-DiskImage"))
            {
                ResolveCalls++;
                return Task.FromResult((ResolveHandler ?? DefaultResolve)(script));
            }

            // Dismount.
            return Task.FromResult(new WindowsIsoMountService.PowerShellResult
            {
                ExitCode = 0,
                StandardOutput = "ok"
            });
        }

        public IReadOnlyList<string> DismountScripts =>
            ScriptsRun.Where(s => s.Contains("Dismount-DiskImage")).ToList();

        private static WindowsIsoMountService.PowerShellResult DefaultAttach(string script) =>
            new() { ExitCode = 0, StandardOutput = "C:\\images\\win.iso" };

        private static WindowsIsoMountService.PowerShellResult DefaultResolve(string script) =>
            new() { ExitCode = 0, StandardOutput = "X:\\" };
    }

    private static WindowsIsoMountService CreateService(
        FakeRunner runner,
        TimeSpan? timeout = null,
        TimeSpan? poll = null)
        => new(runner, readinessTimeout: timeout, pollDelay: poll);

    // 1) Volume available immediately: attach succeeds, first resolve returns the letter.
    [Fact]
    public async Task Mount_ImmediateLetter_ReturnsRoot_NoRetry()
    {
        var runner = new FakeRunner();
        var svc = CreateService(runner, timeout: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromMilliseconds(10));

        var root = await svc.MountReadOnlyAsync("C:\\images\\win.iso");

        Assert.Equal("X:\\", root);
        Assert.Equal(1, runner.ResolveCalls); // no extra polling needed
    }

    // 2) Volume becomes available after retry: first N resolves empty, then a letter.
    [Fact]
    public async Task Mount_LetterAfterRetry_ReturnsRoot()
    {
        var counter = 0;
        var runner = new FakeRunner
        {
            ResolveHandler = _ =>
            {
                var c = counter++;
                return new WindowsIsoMountService.PowerShellResult
                {
                    ExitCode = 0,
                    StandardOutput = c >= 2 ? "Y:\\" : string.Empty
                };
            }
        };

        var svc = CreateService(runner, timeout: TimeSpan.FromMilliseconds(500), poll: TimeSpan.FromMilliseconds(10));
        var root = await svc.MountReadOnlyAsync("C:\\images\\win.iso");

        Assert.Equal("Y:\\", root);
        Assert.True(counter >= 3, "expected several poll attempts before the letter appeared");
    }

    // 3) Volume never becomes available: attach succeeds, resolve always empty -> fail + cleanup.
    [Fact]
    public async Task Mount_LetterNeverAppears_Throws_And_Dismounts()
    {
        var runner = new FakeRunner
        {
            ResolveHandler = _ => new WindowsIsoMountService.PowerShellResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty
            }
        };
        var svc = CreateService(runner, timeout: TimeSpan.FromMilliseconds(120), poll: TimeSpan.FromMilliseconds(10));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MountReadOnlyAsync("C:\\images\\win.iso"));

        Assert.Contains("no drive letter", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(runner.DismountScripts); // best-effort cleanup ran
    }

    // 4) Cancellation while waiting: resolve reports no letter, then the token fires.
    [Fact]
    public async Task Mount_CancelledWhileWaiting_Throws_OperationCanceled_And_Dismounts()
    {
        var cts = new CancellationTokenSource();
        var runner = new FakeRunner
        {
            ResolveHandler = _ =>
            {
                cts.Cancel(); // cancel as soon as we start waiting
                return new WindowsIsoMountService.PowerShellResult
                {
                    ExitCode = 0,
                    StandardOutput = string.Empty
                };
            }
        };
        var svc = CreateService(runner, timeout: TimeSpan.FromSeconds(5), poll: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.MountReadOnlyAsync("C:\\images\\win.iso", cts.Token));

        Assert.NotEmpty(runner.DismountScripts); // cleanup ran before propagating cancellation
    }

    // 5) Mount-DiskImage failure: attach returns non-zero exit -> fail fast (no retry, no resolve).
    [Fact]
    public async Task Mount_AttachFails_Throws_NoResolveAttempted()
    {
        var runner = new FakeRunner
        {
            AttachHandler = _ => new WindowsIsoMountService.PowerShellResult
            {
                ExitCode = 1,
                StandardError = "#< CLIXML <Objs><S>Mount-DiskImage : Access is denied.</S></Objs>"
            }
        };
        var svc = CreateService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MountReadOnlyAsync("C:\\images\\win.iso"));

        Assert.Equal(0, runner.ResolveCalls); // never polled for a letter
        Assert.Contains("Access is denied", ex.Message);
        Assert.DoesNotContain("#< CLIXML", ex.Message);
    }

    // 6) Failed readiness (never available) performs cleanup. Verified in (3);
    //    this additionally asserts the dismount runs even on a thrown failure.
    [Fact]
    public async Task Mount_FailedReadiness_CleanupRunsBeforeThrow()
    {
        var runner = new FakeRunner
        {
            ResolveHandler = _ => new WindowsIsoMountService.PowerShellResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty
            }
        };
        var svc = CreateService(runner, timeout: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MountReadOnlyAsync("C:\\images\\win.iso"));

        Assert.Contains(runner.DismountScripts, s => s.Contains("Dismount-DiskImage"));
    }

    // 7) Successful mount with CLIXML stderr still returns the normalized root.
    [Fact]
    public async Task Mount_SuccessReturnsNormalizedRoot_DespiteClixmlStderr()
    {
        var runner = new FakeRunner
        {
            AttachHandler = _ => new WindowsIsoMountService.PowerShellResult
            {
                ExitCode = 0,
                StandardOutput = "C:\\images\\win.iso",
                StandardError = "#< CLIXML <Objs><S>verbose noise</S></Objs>"
            }
        };
        var svc = CreateService(runner, timeout: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromMilliseconds(10));

        var root = await svc.MountReadOnlyAsync("C:\\images\\win.iso");

        Assert.Equal("X:\\", root);
    }

    // 8) CLIXML / progress noise does not become the only visible diagnostic.
    [Fact]
    public void NormalizePowerShellError_StripsClixmlAndProgress_KeepsMeaning()
    {
        var raw = "#< CLIXML <Objs><S>Mount-DiskImage : The operation failed because the media is not ready.</S></Objs>\r\n" +
                  "Preparing modules for first use...\r\n" +
                  "  50%  [=========                ]\r\n" +
                  "Some plain diagnostic line.";

        var normalized = WindowsIsoMountService.NormalizePowerShellError(raw);

        Assert.DoesNotContain("#< CLIXML", normalized);
        Assert.DoesNotContain("Preparing modules for first use", normalized);
        Assert.DoesNotContain("%", normalized);
        Assert.Contains("The operation failed because the media is not ready", normalized);
        Assert.Contains("Some plain diagnostic line", normalized);
    }

    // 8b) If stripping removes everything, the raw text is preserved (never lose diagnostics).
    [Fact]
    public void NormalizePowerShellError_FallsBackToRaw_WhenNothingMeaningful()
    {
        var raw = "#< CLIXML <Objs Version=\"1.1\"></Objs>";
        var normalized = WindowsIsoMountService.NormalizePowerShellError(raw);
        Assert.Equal(raw.Trim(), normalized);
    }

    // 9) ISO path escaping remains safe (no command injection via crafted path).
    [Fact]
    public void BuildMountScript_EscapesPath_PreventingInjection()
    {
        var evil = "'; Start-Process calc.exe; '";
        var script = WindowsIsoMountService.BuildMountScript(evil);

        // Safe form: each quote in the payload is doubled AND the value stays
        // wrapped in the surrounding single-quoted -ImagePath literal, so the
        // effective PowerShell text is `-ImagePath '''; Start-Process calc.exe; '''`
        // which PowerShell parses as ONE literal string (no command breakout).
        Assert.Contains("-ImagePath '''; Start-Process calc.exe; '''", script);
        // The unescaped breakout form (single quotes around the payload) must
        // NOT be present.
        Assert.DoesNotContain("-ImagePath '; Start-Process calc.exe; '", script);
        Assert.Contains("-ErrorAction Stop", script);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
    }

    // 9b) The mount/resolve scripts opt into strict error handling.
    [Fact]
    public void BuildScripts_EnableStrictErrorHandling()
    {
        var mount = WindowsIsoMountService.BuildMountScript("C:\\x.iso");
        var resolve = WindowsIsoMountService.BuildResolveScript("C:\\x.iso");

        Assert.Contains("$ErrorActionPreference = 'Stop'", mount);
        Assert.Contains("Mount-DiskImage", mount);
        Assert.Contains("-ErrorAction Stop", mount);

        Assert.Contains("$ErrorActionPreference = 'Stop'", resolve);
        Assert.Contains("Get-DiskImage", resolve);
        Assert.Contains("Get-Volume", resolve);
    }

    // 10) Distinct phases are logged (requested -> attached -> waiting -> resolved).
    [Fact]
    public async Task Mount_LogsDistinctPhases()
    {
        var logger = new InMemoryLoggerService();
        var runner = new FakeRunner();
        var svc = new WindowsIsoMountService(
            runner, logger, readinessTimeout: TimeSpan.FromMilliseconds(200), pollDelay: TimeSpan.FromMilliseconds(10));

        await svc.MountReadOnlyAsync("C:\\images\\win.iso");

        var messages = logger.Entries.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("ISO mount requested"));
        Assert.Contains(messages, m => m.Contains("disk image attached"));
        Assert.Contains(messages, m => m.Contains("waiting for volume"));
        Assert.Contains(messages, m => m.Contains("drive letter resolved"));
    }
}
