using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Mvvm;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the real-desktop defect: Customize selections exist (the tab
/// shows "已选：N") but Review/Next stay disabled. Root cause was that selections
/// mutate <see cref="IAppState.CurrentCustomizationPlan"/> IN PLACE while
/// <see cref="CustomizationPlan"/> was not observable and <see cref="AppState"/>
/// only notified on a reference change — so <see cref="WorkflowViewModel"/>
/// never recomputed gating. The fix makes the plan observable and forwards nested
/// changes (mirroring the validated ImageServicingWorkspace pattern).
///
/// <para>These tests drive ONE persistent AppState + WorkflowViewModel + the real
/// customization VMs and prove the live chain: tab selection -> PlanSync.Toggle ->
/// CustomizationPlan (INPC) -> AppState nested forward -> WorkflowViewModel
/// .RecomputeStates -> step states + NextCommand.CanExecuteChanged.</para>
/// </summary>
public class CustomizeSelectionToReviewRegressionTests
{
    private sealed class Harness
    {
        public AppState State { get; }
        public WorkflowViewModel Wf { get; }
        public ComponentsViewModel Components { get; }
        public PrivacyViewModel Privacy { get; }
        public SystemViewModel System { get; }

        public Harness()
        {
            State = new AppState();
            var logger = new InMemoryLoggerService();
            var discovery = new FakeCustomizationDiscoveryService
            {
                Inventory = new DiscoveryInventory
                {
                    Discovered = true,
                    AppxPackages = new[]
                    {
                        new DiscoveredAppxPackage { PackageName = "AppA", DisplayName = "App A", Risk = RiskClass.Removable },
                        new DiscoveredAppxPackage { PackageName = "AppB", DisplayName = "App B", Risk = RiskClass.Removable },
                    },
                    WindowsPackages = new[]
                    {
                        new DiscoveredWindowsPackage { PackageIdentity = "P1", DisplayName = "Pkg1", Classification = PackageClassification.Feature, Risk = RiskClass.Removable },
                    },
                    Services = new[]
                    {
                        new DiscoveredOfflineService { ServiceName = "DiagTrack", CurrentStartValue = 2, ServiceKind = ServiceClass.RecommendedConfigurable, Risk = RiskClass.Removable },
                    },
                }
            };
            var defs = new FakeCustomizationDefinitionProvider();
            defs.Privacy.Add(new DiscoveredRegistrySetting
            {
                SettingId = "p1", Category = CustomizationCategory.Privacy, Title = "P1",
                Hive = "SOFTWARE", KeyPath = "K", ValueName = "V", ValueKind = OfflineRegistryValueKind.DWord,
                RecommendedData = "0", Risk = RiskClass.Safe
            });
            defs.System.Add(new DiscoveredRegistrySetting
            {
                SettingId = "s1", Category = CustomizationCategory.System, Title = "S1",
                Hive = "SOFTWARE", KeyPath = "KS", ValueName = "VS", ValueKind = OfflineRegistryValueKind.DWord,
                RecommendedData = "1", Risk = RiskClass.Safe
            });
            defs.Services.Add(new DiscoveredOfflineService
            {
                ServiceName = "DiagTrack", DisplayName = "Telemetry", CurrentStartValue = 2,
                RecommendedStartType = ServiceStartType.Disabled, ServiceKind = ServiceClass.RecommendedConfigurable, Risk = RiskClass.Removable
            });

            var image = new ImageViewModel(
                State, logger,
                new WorkflowAndCommandTests.FakeInspection(),
                new WorkflowAndCommandTests.FakeFilePicker(),
                new WorkflowAndCommandTests.FakeWorkspaceFactory(),
                new WorkflowAndCommandTests.FakeWimService(),
                new FakeImageServicingService());
            Components = new ComponentsViewModel(State, logger, discovery, defs);
            Privacy = new PrivacyViewModel(State, logger, defs);
            System = new SystemViewModel(State, logger, defs);
            var comingSoon = new ComingSoonViewModel();
            var customize = new CustomizeStepViewModel(Components, Privacy, System, comingSoon);
            var plan = new PlanReviewViewModel(State, logger, new FakeCustomizationExecutionService());
            var build = new BuildStepViewModel(
                State, new FakeBuildService(), new FakeFileSystem(), new WorkflowAndCommandTests.FakeFilePicker(),
                new FakeAdkToolLocator(), logger, new FakeLocalizationService());
            Wf = new WorkflowViewModel(State, image, customize, plan, build);
        }

        /// <summary>Mount a working image and navigate into the Customize step.</summary>
        public void SetupMountedAndEnterCustomize()
        {
            State.CurrentImageWorkspace = new ImageWorkspace();
            State.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                WorkingDirectory = @"C:\wf\ws",
                MountDirectory = @"C:\wf\ws\mount",
                WorkingImagePath = @"C:\wf\ws\image\install.wim",
                State = ServicingWorkspaceState.Mounted
            };
            Wf.GoToStep(WorkflowStep.Customize);
        }

        public async Task DiscoverComponentsAsync() => await Components.DiscoverAsync();
    }

    // ---- Primary scenario: one selection enables Review/Next, gating identical
    // across cultures (the predicate never reads localization). ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task Selection_EnablesReviewAndNext_Live_AndCultureInvariant(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        await h.DiscoverComponentsAsync();

        // 0 selections -> Review unavailable, Next disabled.
        Assert.Equal(WorkflowStep.Customize, h.Wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.NotAvailable, h.Wf.Steps[3].State); // Review
        Assert.False(h.Wf.NextCommand.CanExecute(null));

        var nextChanged = 0;
        h.Wf.NextCommand.CanExecuteChanged += (_, _) => nextChanged++;

        // Select ONE Appx operation.
        h.Components.AppxPackages[0].IsSelected = true;

        // 1 valid selection -> Review available, Next enabled, CanExecuteChanged fired.
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[3].State);
        Assert.True(h.Wf.NextCommand.CanExecute(null));
        Assert.True(nextChanged > 0);
        Assert.Single(h.State.CurrentCustomizationPlan!.SelectedOperations);

        // Execute Next -> Review becomes Current.
        h.Wf.GoNext();
        Assert.Equal(WorkflowStep.Review, h.Wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Current, h.Wf.Steps[3].State);
    }

    // ---- Per-tab selection gates (no specific tab/type required). ----

    [Fact]
    public void PrivacyOnlySelection_EnablesReviewAndNext()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        Assert.False(h.Wf.NextCommand.CanExecute(null));

        h.Privacy.Settings[0].IsSelected = true;

        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[3].State);
        Assert.True(h.Wf.NextCommand.CanExecute(null));
        Assert.Single(h.State.CurrentCustomizationPlan!.SelectedOperations);
    }

    [Fact]
    public void SystemServiceOnlySelection_EnablesReviewAndNext()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        Assert.False(h.Wf.NextCommand.CanExecute(null));

        h.System.RecommendedServices[0].IsSelected = true;

        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[3].State);
        Assert.True(h.Wf.NextCommand.CanExecute(null));
        Assert.Single(h.State.CurrentCustomizationPlan!.SelectedOperations);
    }

    // ---- Deselect back to 0 disables Review/Next again, and CanExecuteChanged
    // actually fires on the way down too. ----

    [Fact]
    public async Task DeselectLastItem_DisablesReviewAndNext_Again()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        await h.DiscoverComponentsAsync();

        h.Components.AppxPackages[0].IsSelected = true;
        Assert.True(h.Wf.NextCommand.CanExecute(null));

        var nextChanged = 0;
        h.Wf.NextCommand.CanExecuteChanged += (_, _) => nextChanged++;

        h.Components.AppxPackages[0].IsSelected = false;

        Assert.False(h.Wf.NextCommand.CanExecute(null));
        Assert.Equal(WorkflowStepState.NotAvailable, h.Wf.Steps[3].State);
        Assert.True(nextChanged > 0);
        Assert.Empty(h.State.CurrentCustomizationPlan!.Operations);
    }

    // ---- Multiple selections keep Review/Next enabled. ----

    [Fact]
    public async Task MultipleSelections_KeepReviewAndNextEnabled()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        await h.DiscoverComponentsAsync();

        h.Components.AppxPackages[0].IsSelected = true;
        h.Components.AppxPackages[1].IsSelected = true;
        h.Privacy.Settings[0].IsSelected = true;

        Assert.True(h.Wf.NextCommand.CanExecute(null));
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[3].State);
        Assert.Equal(3, h.State.CurrentCustomizationPlan!.SelectedOperations.Count);
    }

    // ---- The crux: an IN-PLACE plan toggle (reference unchanged) must still
    // propagate. This is exactly the failure mode from the real desktop. ----

    [Fact]
    public async Task InPlacePlanToggle_AfterDeselect_StillPropagates()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        await h.DiscoverComponentsAsync();

        h.Components.AppxPackages[0].IsSelected = true;  // creates plan (reference change)
        h.Components.AppxPackages[0].IsSelected = false; // removes op; plan stays Draft, ref unchanged
        Assert.False(h.Wf.NextCommand.CanExecute(null));

        var nextChanged = 0;
        h.Wf.NextCommand.CanExecuteChanged += (_, _) => nextChanged++;

        h.Components.AppxPackages[0].IsSelected = true;  // in-place toggle, reference unchanged

        Assert.True(h.Wf.NextCommand.CanExecute(null));
        Assert.True(nextChanged > 0); // nested notification propagated WITHOUT a reference change
    }

    // ---- No reentrancy loop: many toggles must converge, not stack-overflow. ----

    [Fact]
    public async Task RapidToggles_ConvergeWithoutLoop()
    {
        var h = new Harness();
        h.SetupMountedAndEnterCustomize();
        await h.DiscoverComponentsAsync();

        for (var i = 0; i < 5; i++)
        {
            h.Components.AppxPackages[0].IsSelected = true;
            h.Components.AppxPackages[0].IsSelected = false;
        }

        Assert.False(h.Wf.NextCommand.CanExecute(null));
        Assert.Equal(WorkflowStepState.NotAvailable, h.Wf.Steps[3].State);

        h.Components.AppxPackages[0].IsSelected = true;
        Assert.True(h.Wf.NextCommand.CanExecute(null));
    }
}
