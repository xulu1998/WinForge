using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Execution;

/// <summary>
/// Windows implementation of <see cref="IProcessRunner"/> backed by
/// <see cref="System.Diagnostics.Process"/>. It always runs without a window,
/// captures stdout/stderr, and supports cooperative cancellation by killing the
/// child process when the token fires. The <see cref="IProcessRunner"/> abstraction
/// (and these DTOs) live in Core, so Core never references <c>Process</c> directly.
/// </summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            UseShellExecute = request.UseShellExecute,
            RedirectStandardOutput = request.RedirectStandardOutput,
            RedirectStandardError = request.RedirectStandardError,
            CreateNoWindow = request.CreateNoWindow
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {request.FileName}.");
        }

        // Best-effort cancellation: terminate the child if the token fires so the
        // caller is not blocked on a hung external tool. The operation itself
        // (e.g. ISO cleanup) is never dependent on this process completing.
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

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr
            };
        }
    }
}
