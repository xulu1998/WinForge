using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.WimEngine;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// ViewModel behaviour for ISO selection and inspection. Uses fakes for the
/// file picker and inspection service so no UI dialog or real ISO is required.
/// </summary>
public class IsoViewModelTests
{
    private static (ImageViewModel vm, AppState state) Build(IIsoInspectionService inspection, IFilePicker picker)
    {
        var state = new AppState();
        var vm = new ImageViewModel(
            state,
            new InMemoryLoggerService(),
            inspection,
            picker,
            new ImageWorkspaceFactory(),
            new WimService(),
            new FakeImageServicingService());
        return (vm, state);
    }

    [Fact]
    public async Task SelectIso_Updates_Path_And_Result()
    {
        var path = @"C:\images\windows.iso";
        var picker = new FakeFilePicker { NextPath = path };
        var inspection = new FakeInspection { Next = Candidate(path) };
        var (vm, state) = Build(inspection, picker);

        await vm.SelectIsoAsync();

        Assert.Equal(path, state.SourceImagePath);
        Assert.Equal(path, vm.FileDisplay);
        Assert.Equal("Windows ISO Candidate", vm.DetectedTypeDisplay);
        Assert.False(vm.IsInspecting);
        Assert.True(vm.HasResult);
    }

    [Fact]
    public async Task Successful_Inspection_Updates_Ui_State()
    {
        var path = @"C:\images\windows.iso";
        var picker = new FakeFilePicker { NextPath = null };
        var inspection = new FakeInspection { Next = Candidate(path) };
        var (vm, state) = Build(inspection, picker);
        state.SourceImagePath = path;

        await vm.InspectCurrentAsync();

        Assert.Equal("Windows ISO Candidate", vm.DetectedTypeDisplay);
        Assert.False(vm.IsInspecting);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Failed_Inspection_Produces_Error_State()
    {
        var path = @"C:\images\windows.iso";
        var picker = new FakeFilePicker { NextPath = null };
        var inspection = new FakeInspection { Next = IsoInspectionResult.Failed(path, "Mount failed.") };
        var (vm, state) = Build(inspection, picker);
        state.SourceImagePath = path;

        await vm.InspectCurrentAsync();

        Assert.True(vm.HasError);
        Assert.Equal("Unable to inspect ISO", vm.DetectedTypeDisplay);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
        Assert.False(vm.IsInspecting);
    }

    [Fact]
    public async Task Busy_State_Enters_And_Exits()
    {
        var path = @"C:\images\windows.iso";
        var picker = new FakeFilePicker { NextPath = null };
        var controllable = new ControllableInspection();
        var (vm, state) = Build(controllable, picker);
        state.SourceImagePath = path;

        var task = vm.InspectCurrentAsync();
        Assert.True(vm.IsInspecting); // entered

        controllable.Complete(Candidate(path));
        await task;

        Assert.False(vm.IsInspecting); // exited
        Assert.Equal("Windows ISO Candidate", vm.DetectedTypeDisplay);
    }

    [Fact]
    public async Task Cancelled_Picker_Does_Not_Create_Failure()
    {
        var picker = new FakeFilePicker { NextPath = null };
        var inspection = new FakeInspection { Next = Candidate(@"C:\images\windows.iso") };
        var (vm, state) = Build(inspection, picker);

        await vm.SelectIsoAsync();

        Assert.Equal("No ISO selected", vm.FileDisplay);
        Assert.False(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.False(vm.IsInspecting);
        Assert.False(inspection.Called);
    }

    [Fact]
    public async Task Repeated_Inspect_Command_Cannot_Race()
    {
        var path = @"C:\images\windows.iso";
        var picker = new FakeFilePicker { NextPath = null };
        var controllable = new ControllableInspection();
        var (vm, state) = Build(controllable, picker);
        state.SourceImagePath = path;

        // Fire the command (async void). While it is in flight the guard must
        // report the command as not executable.
        vm.InspectIsoCommand.Execute(null);
        Assert.False(vm.InspectIsoCommand.CanExecute(null));

        controllable.Complete(Candidate(path));
        await Task.Delay(100);

        Assert.False(vm.IsInspecting);
        Assert.True(vm.InspectIsoCommand.CanExecute(null));
    }

    private static IsoInspectionResult Candidate(string path) => new()
    {
        IsoPath = path,
        FileName = Path.GetFileName(path),
        FileSizeBytes = 5_700_000_000L,
        Exists = true,
        ExtensionValid = true,
        IsReadable = true,
        HasSourcesDirectory = true,
        HasBootDirectory = true,
        HasInstallWim = true,
        InstallImageType = InstallImageType.Wim,
        DetectedType = IsoDetectedType.WindowsIsoCandidate,
        Status = IsoInspectionStatus.Completed
    };

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextPath { get; set; }
        public string? PickIsoFile() => NextPath;
        public string? PickFolder() => null;
    }

    private sealed class FakeInspection : IIsoInspectionService
    {
        public IsoInspectionResult? Next { get; set; }
        public bool Called { get; private set; }
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(Next ?? IsoInspectionResult.NotInspected(isoPath));
        }
    }

    private sealed class ControllableInspection : IIsoInspectionService
    {
        private readonly TaskCompletionSource<IsoInspectionResult> _tcs = new();
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
            => _tcs.Task;
        public void Complete(IsoInspectionResult result) => _tcs.TrySetResult(result);
    }
}
