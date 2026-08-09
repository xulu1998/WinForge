using System.Collections;
using System.Globalization;
using System.Resources;
using WinForge.App.FriendlyMetadata;
using WinForge.App.Localization;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// SERVICE / APP friendly-metadata mapping, the <see cref="ServiceConfigPolicy"/>
/// allowlist, and REGRESSION guards that lock in the wizard/gating and
/// boot-time behavior delivered by the UX workflow refactor. All tests are
/// CI-safe: they build view models from fakes and never touch an ISO, mount,
/// or the UI thread.
/// </summary>
public class FriendlyMetadataAndRegressionTests
{
    private static ResourceManagerLocalizationService EnglishService()
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(HomeViewModel).Assembly);
        return new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
    }

    // ---- SERVICE: friendly metadata (well-known allowlisted services) ----

    [Fact]
    public void Svc_DiagTrack_Maps_To_Friendly_Name_And_Description()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal("Connected User Experiences and Telemetry", provider.GetServiceFriendlyName("DiagTrack"));
        Assert.Equal("Collects and uploads diagnostics and usage data.", provider.GetServiceDescription("DiagTrack"));
    }

    [Fact]
    public void Svc_WerSvc_And_PcaSvc_Map()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal("Windows Error Reporting Service", provider.GetServiceFriendlyName("WerSvc"));
        Assert.Equal("Program Compatibility Assistant Service", provider.GetServiceFriendlyName("PcaSvc"));
    }

    [Fact]
    public void Svc_Case_Insensitive_And_Version_Suffixed_Still_Match()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        // Lowercase and edition/version-suffixed identities must still resolve.
        Assert.Equal("Connected User Experiences and Telemetry", provider.GetServiceFriendlyName("diagtrack"));
        Assert.Equal("Connected User Experiences and Telemetry", provider.GetServiceFriendlyName("DiagTrack.12345"));
    }

    [Fact]
    public void Svc_Unknown_Returns_Raw_Name_Not_Fabricated()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal("Spooler", provider.GetServiceFriendlyName("Spooler"));
        Assert.Equal(string.Empty, provider.GetServiceDescription("Spooler"));
    }

    [Fact]
    public void Svc_Null_Or_Empty_Returns_Empty()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal(string.Empty, provider.GetServiceFriendlyName(null!));
        Assert.Equal(string.Empty, provider.GetServiceFriendlyName(string.Empty));
        Assert.Equal(string.Empty, provider.GetServiceDescription(null!));
    }

    [Fact]
    public void Svc_FriendlyName_Localizes_To_ZhCn_When_Culture_Switched()
    {
        var svc = EnglishService();
        var provider = new FriendlyMetadataProvider(svc);
        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("诊断与使用情况遥测", provider.GetServiceFriendlyName("DiagTrack"));
    }

    // ---- SERVICE: the configuration allowlist (ADR-030) ----

    [Fact]
    public void ServiceConfigPolicy_Allows_Trusted_Markers()
    {
        Assert.True(ServiceConfigPolicy.IsConfigurable("DiagTrack"));
        Assert.True(ServiceConfigPolicy.IsConfigurable("WerSvc"));
        Assert.True(ServiceConfigPolicy.IsConfigurable("PcaSvc"));
    }

    [Fact]
    public void ServiceConfigPolicy_Rejects_Unknown_And_Null()
    {
        Assert.False(ServiceConfigPolicy.IsConfigurable("Spooler"));
        Assert.False(ServiceConfigPolicy.IsConfigurable("Dnscache"));
        Assert.False(ServiceConfigPolicy.IsConfigurable(null));
        Assert.False(ServiceConfigPolicy.IsConfigurable(string.Empty));
    }

    [Fact]
    public void ServiceConfigPolicy_Case_Insensitive_Substring()
    {
        Assert.True(ServiceConfigPolicy.IsConfigurable("microsoft.diagtrack"));
        Assert.True(ServiceConfigPolicy.IsConfigurable("MyWerSvcService"));
    }

    // ---- APP: friendly metadata (provisioned Appx packages) ----

    [Fact]
    public void App_BingWeather_Maps()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal("Weather", provider.GetAppFriendlyName("Microsoft.BingWeather"));
        Assert.Equal("The preinstalled Bing Weather app.", provider.GetAppDescription("Microsoft.BingWeather"));
    }

    [Fact]
    public void App_Suffixed_Package_Identity_Maps()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        // Publisher/architecture-suffixed identities still resolve to the friendly name.
        Assert.Equal("Weather", provider.GetAppFriendlyName("Microsoft.BingWeather_8wekyb3d8bbwe"));
        Assert.Equal("Get Help", provider.GetAppFriendlyName("Microsoft.GetHelp_8wekyb3d8bbwe"));
    }

    [Fact]
    public void App_Unknown_Returns_Raw()
    {
        var provider = new FriendlyMetadataProvider(EnglishService());
        Assert.Equal("Microsoft.Office.Desktop", provider.GetAppFriendlyName("Microsoft.Office.Desktop"));
        Assert.Equal(string.Empty, provider.GetAppDescription("Microsoft.Office.Desktop"));
    }

    // ---- REGRESSION: ImageViewModel boot / null-localization safety ----

    [Fact]
    public void Regression_ImageViewModel_Builds_And_Falls_Back_Without_Localization()
    {
        // No ILocalizationService (null) must not throw; user-facing strings fall back.
        var image = new ImageViewModel(
            new AppState(), new InMemoryLoggerService(),
            new WorkflowAndCommandTests.FakeInspection(),
            new WorkflowAndCommandTests.FakeFilePicker(),
            new WorkflowAndCommandTests.FakeWorkspaceFactory(),
            new WorkflowAndCommandTests.ReadyWimService(),
            new FakeImageServicingService());

        Assert.Equal("No ISO selected", image.FileDisplay);
        Assert.Equal("No ISO selected", image.DetectedTypeDisplay);
        Assert.Equal("Select an edition", image.WorkspaceStatusDisplay);
    }

    // ---- REGRESSION: wizard gating invariants ----

    [Fact]
    public void Regression_Workflow_Recompute_Never_Auto_Advances_Current_Step()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoNext(); // advance to Prepare
        Assert.Equal(WorkflowStep.Prepare, wf.CurrentStep!.Step);

        // Mounting later must not yank the user forward; the active step stays put,
        // but the now-satisfied Customize step becomes Available.
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        Assert.Equal(WorkflowStep.Prepare, wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Available, wf.Steps[2].State);
    }

    [Fact]
    public void Regression_Workflow_Apply_Step_Hidden_Until_Plan_Validated()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };

        // A selected (Draft) plan unlocks Review but NOT Apply.
        state.CurrentCustomizationPlan = WorkflowAndCommandTests.SelectedPlan();
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[4].State);

        // Only a validated plan unlocks Apply.
        var plan = WorkflowAndCommandTests.SelectedPlan();
        plan.Validate();
        state.CurrentCustomizationPlan = plan;
        Assert.Equal(WorkflowStepState.Available, wf.Steps[4].State);
    }

    [Fact]
    public void Regression_SourceChange_Clears_Plan_And_Discovery_When_Not_Executing()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentCustomizationPlan = WorkflowAndCommandTests.SelectedPlan();
        state.DiscoveredInventory = new DiscoveryInventory { Discovered = true };

        state.SourceImagePath = @"C:\changed.iso";

        Assert.Null(state.CurrentCustomizationPlan);
        Assert.Null(state.DiscoveredInventory);
    }

    [Fact]
    public void Regression_SourceChange_Keeps_Plan_And_Discovery_During_Executing()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentCustomizationPlan = WorkflowAndCommandTests.SelectedPlan();
        state.DiscoveredInventory = new DiscoveryInventory { Discovered = true };
        state.CustomizationExecutionState = CustomizationExecutionState.Executing;

        state.SourceImagePath = @"C:\changed.iso";

        Assert.NotNull(state.CurrentCustomizationPlan);
        Assert.NotNull(state.DiscoveredInventory);
    }
}
