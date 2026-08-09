using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// Reusable fake <see cref="IProcessRunner"/> for infrastructure service tests.
/// Records every invocation so assertions can inspect command form, and returns
/// staged <see cref="ProcessResult"/> output via an optional responder.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public Exception? Throw { get; set; }

    public Func<ProcessRequest, ProcessResult>? Responder { get; set; }

    public ProcessResult Default { get; set; } = new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };

    private readonly List<ProcessRequest> _requests = new();
    public IReadOnlyList<ProcessRequest> Requests => _requests;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        if (Throw is not null)
        {
            return Task.FromException<ProcessResult>(Throw);
        }

        if (Responder is not null)
        {
            return Task.FromResult(Responder(request));
        }

        return Task.FromResult(Default);
    }
}
