using System.Threading;
using System.Threading.Tasks;

namespace WinForge.Core.Services;

/// <summary>
/// A platform-agnostic description of an external process to run. Core defines
/// this so Infrastructure can execute Windows tools (e.g. DISM) behind an
/// abstraction that is fully testable with a fake — Core itself never references
/// <see cref="System.Diagnostics.Process"/>.
/// </summary>
public sealed class ProcessRequest
{
    /// <summary>Executable file name, e.g. <c>dism.exe</c>.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Command-line arguments.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Use shell execution. Almost always false for tool invocation.</summary>
    public bool UseShellExecute { get; init; }

    /// <summary>Capture standard output.</summary>
    public bool RedirectStandardOutput { get; init; } = true;

    /// <summary>Capture standard error.</summary>
    public bool RedirectStandardError { get; init; } = true;

    /// <summary>Run without a visible window.</summary>
    public bool CreateNoWindow { get; init; } = true;
}

/// <summary>
/// The captured result of running a <see cref="ProcessRequest"/>.
/// </summary>
public sealed class ProcessResult
{
    /// <summary>Process exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>Redirected standard output (may be empty).</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Redirected standard error (may be empty).</summary>
    public string StandardError { get; init; } = string.Empty;
}

/// <summary>
/// Runs an external process and returns its captured result. The concrete
/// Windows implementation lives in Infrastructure; tests supply a fake.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
