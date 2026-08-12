using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 11.1 infrastructure: the four read-only DISM parsers and the
/// <see cref="WindowsComponentIntelligenceService"/> orchestrator. Uses the shared
/// <see cref="FakeProcessRunner"/> / <see cref="FakeMountIdentityValidator"/> from this
/// test assembly (no real DISM, no real mount).
/// </summary>
public class ComponentIntelligenceInfraTests
{
    private const string AppXSample = @"Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

DisplayName : Microsoft.BingWeather
Version : 4.53.53006.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe
Regions : all
";

    private const string CapSample = @"Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Capability Identity : App.Support.QuickStarts~31bf3856ad364e35~amd64~~10.0.26100.1
State : Installed
";

    private const string FeatureSample = @"Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Feature Name : Microsoft-Windows-DataCenterNanoServer
State : Disabled
";

    private const string PkgSample = @"Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Package Identity : Microsoft-Windows-Client-Package~31bf3856ad364e35~amd64~~10.0.26100.1
State : Installed
Release Type : Feature Pack
";

    // ---------- Parsers ----------

    [Fact]
    public void AppxParser_ParsesIdentity_DisplayName_State_AndDerivedFamily()
    {
        var items = AppxInventoryParser.Parse(AppXSample);

        Assert.Single(items);
        Assert.Equal("Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe", items[0].RawIdentity);
        Assert.Equal("Microsoft.BingWeather", items[0].DisplayName);
        Assert.Equal("4.53.53006.0", items[0].Version);
        Assert.Equal("Provisioned", items[0].State);
        Assert.Equal("Microsoft.BingWeather_8wekyb3d8bbwe", items[0].PackageFamilyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no packages here")]
    public void AppxParser_Empty_ReturnsEmpty(string input)
        => Assert.Empty(AppxInventoryParser.Parse(input));

    [Fact]
    public void AppxParser_IsRecognizedOutput_Banner()
        => Assert.True(AppxInventoryParser.IsRecognizedOutput("Deployment Image Servicing and Management tool\nPackageName : X"));

    [Fact]
    public void CapabilityParser_ParsesIdentityAndState()
    {
        var items = CapabilityInventoryParser.Parse(CapSample);

        Assert.Single(items);
        Assert.Equal("App.Support.QuickStarts~31bf3856ad364e35~amd64~~10.0.26100.1", items[0].RawIdentity);
        Assert.Equal(CapabilityState.Installed, items[0].CapState);
    }

    [Fact]
    public void OptionalFeatureParser_ParsesIdentityAndState()
    {
        var items = OptionalFeatureInventoryParser.Parse(FeatureSample);

        Assert.Single(items);
        Assert.Equal("Microsoft-Windows-DataCenterNanoServer", items[0].RawIdentity);
        Assert.Equal(FeatureState.Disabled, items[0].FeatureStateValue);
    }

    [Fact]
    public void CbsPackageParser_ParsesIdentity_State_AndReleaseType()
    {
        var items = CbsPackageInventoryParser.Parse(PkgSample);

        Assert.Single(items);
        Assert.Equal("Microsoft-Windows-Client-Package~31bf3856ad364e35~amd64~~10.0.26100.1", items[0].RawIdentity);
        Assert.Equal(CbsPackageState.Installed, items[0].PkgState);
        Assert.Equal("Feature Pack", items[0].ReleaseType);
    }

    [Fact]
    public void CbsPackageParser_ServicingStackIdentity_Parsed()
    {
        var outp = "Package Identity : Microsoft-Windows-ServicingStack-Package~31bf3856ad364e35~amd64~~10.0.26100.1\nState : Installed\n";
        var items = CbsPackageInventoryParser.Parse(outp);

        Assert.Single(items);
        Assert.Contains("ServicingStack", items[0].RawIdentity);
    }

    // ---------- Orchestrator ----------

    private static WindowsComponentIntelligenceService ServiceWith(FakeProcessRunner runner)
        => new WindowsComponentIntelligenceService(runner, new InMemoryLoggerService(), new FakeMountIdentityValidator { SessionMatches = true });

    private static ProcessResult Banner(string body) => new ProcessResult { ExitCode = 0, StandardOutput = body };

    [Fact]
    public async Task Discover_NoMount_ReturnsNotDiscovered()
    {
        var runner = new FakeProcessRunner();
        var service = ServiceWith(runner);
        var ws = new ImageServicingWorkspace { MountDirectory = null!, State = ServicingWorkspaceState.Mounted };

        var result = await service.DiscoverAsync(ws, CancellationToken.None);

        Assert.False(result.Discovered);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Discover_WithMount_RunsFourDismPasses_AndSixNotSupported()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("Get-ProvisionedAppxPackages")) return Banner(AppXSample);
                if (req.Arguments.Contains("Get-Capabilities")) return Banner(CapSample);
                if (req.Arguments.Contains("Get-Features")) return Banner(FeatureSample);
                if (req.Arguments.Contains("Get-Packages")) return Banner(PkgSample);
                return Banner(string.Empty);
            }
        };
        var service = ServiceWith(runner);
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount", State = ServicingWorkspaceState.Mounted };

        var result = await service.DiscoverAsync(ws, CancellationToken.None);

        Assert.True(result.Discovered);
        Assert.Equal(10, result.Categories.Count); // 4 implemented + 6 designed-not-implemented

        var notSupported = result.Categories.Where(c => c.Status == InventoryStatus.NotSupported).ToList();
        Assert.Equal(6, notSupported.Count);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.Service);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.ScheduledTask);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.Driver);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.Language);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.WinRecovery);
        Assert.Contains(notSupported, c => c.Category == ComponentCategory.SystemApp);

        var appx = result.Categories.First(c => c.Category == ComponentCategory.AppX);
        Assert.Equal(InventoryStatus.Success, appx.Status);
        Assert.True(appx.Items.Count > 0);
    }

    [Fact]
    public async Task Discover_PerCategoryFailure_DoesNotAbortOtherCategories()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("Get-ProvisionedAppxPackages"))
                {
                    throw new System.InvalidOperationException("dism exploded");
                }

                if (req.Arguments.Contains("Get-Capabilities")) return Banner(CapSample);
                if (req.Arguments.Contains("Get-Features")) return Banner(FeatureSample);
                if (req.Arguments.Contains("Get-Packages")) return Banner(PkgSample);
                return Banner(string.Empty);
            }
        };
        var service = ServiceWith(runner);
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount", State = ServicingWorkspaceState.Mounted };

        var result = await service.DiscoverAsync(ws, CancellationToken.None);

        Assert.True(result.Discovered);
        Assert.False(result.Cancelled);
        Assert.Equal(InventoryStatus.Failed, result.Categories.First(c => c.Category == ComponentCategory.AppX).Status);
        Assert.Equal(InventoryStatus.Success, result.Categories.First(c => c.Category == ComponentCategory.Capability).Status);
    }

    [Fact]
    public async Task Discover_Cancellation_ReturnsCancelled()
    {
        var runner = new FakeProcessRunner { Default = Banner(AppXSample) };
        var service = ServiceWith(runner);
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount", State = ServicingWorkspaceState.Mounted };

        var result = await service.DiscoverAsync(ws, new CancellationToken(true));

        Assert.True(result.Cancelled);
        Assert.True(result.Discovered);
    }

    [Fact]
    public void CuratedCatalog_TeamsDependsOnOneDrive_AsRelatedTo()
    {
        // Stage 11.1 audit (ADR-046): the curated catalog must NOT claim Teams
        // *Requires* OneDrive. The edge is RelatedTo — a soft association, not a hard
        // runtime dependency. This locks the generator/catalog against regressions.
        var catalog = new CuratedComponentCatalog().GetDefinitions();
        var teams = Assert.Single(catalog.Where(d => d.Id == "Teams"));
        var dep = Assert.Single(teams.Dependencies);

        Assert.Equal("OneDrive", dep.ToId);
        Assert.Equal(DependencyRelation.RelatedTo, dep.Relation);
    }
}
