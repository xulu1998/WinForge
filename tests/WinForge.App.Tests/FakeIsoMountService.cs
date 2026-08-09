using System;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// Fake <see cref="IIsoMountService"/> for servicing tests. Returns a configured
/// transient mount root so the servicing service can build a source image path
/// without a real ISO, and records dismount calls.
/// </summary>
internal sealed class FakeIsoMountService : IIsoMountService
{
    public string MountRoot { get; set; } = @"E:\";

    public bool DismountCalled { get; private set; }

    public string? LastDismounted { get; private set; }

    public Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default)
        => Task.FromResult(MountRoot);

    public Task DismountAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        DismountCalled = true;
        LastDismounted = isoPath;
        return Task.CompletedTask;
    }
}
