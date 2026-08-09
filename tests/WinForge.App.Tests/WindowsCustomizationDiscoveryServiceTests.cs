using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// <see cref="WindowsCustomizationDiscoveryService"/> behaviour driven by fakes
/// for DISM, the offline registry, and the definition provider. Covers discovery
/// of Appx packages, Windows packages, offline services, and trusted registry
/// definitions, plus tolerance for an unusable (non-mounted) session.
/// </summary>
public class WindowsCustomizationDiscoveryServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf_disc_" + System.Guid.NewGuid().ToString("N"));
    private FakeProcessRunner _runner = null!;
    private FakeOfflineRegistryService _registry = null!;
    private FakeCustomizationDefinitionProvider _defs = null!;
    private FakeMountIdentityValidator _validator = null!;
    private WindowsCustomizationDiscoveryService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private void Build(bool sessionMatches = true)
    {
        _runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Get-ProvisionedAppxPackages"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = AppxOut };
                }

                if (req.Arguments.Contains("/Get-Packages"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = PkgOut };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
            }
        };

        _registry = new FakeOfflineRegistryService();
        _registry.SubKeys["WinForge_SYSTEM|ControlSet001\\Services"] = new() { "DiagTrack", "Dnscache" };
        _registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
        _registry.Values["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = "2";

        _defs = new FakeCustomizationDefinitionProvider();
        _defs.Privacy.Add(new DiscoveredRegistrySetting
        {
            SettingId = "p1", Category = CustomizationCategory.Privacy, Title = "Privacy 1",
            Hive = "SOFTWARE", KeyPath = "K", ValueName = "V", ValueKind = OfflineRegistryValueKind.DWord,
            RecommendedData = "0", Risk = RiskClass.Safe
        });
        _defs.System.Add(new DiscoveredRegistrySetting
        {
            SettingId = "s1", Category = CustomizationCategory.System, Title = "System 1",
            Hive = "SOFTWARE", KeyPath = "K2", ValueName = "V2", ValueKind = OfflineRegistryValueKind.DWord,
            RecommendedData = "1", Risk = RiskClass.Safe
        });

        _validator = new FakeMountIdentityValidator { SessionMatches = sessionMatches };

        _service = new WindowsCustomizationDiscoveryService(
            _runner, _registry, _defs, new InMemoryLoggerService(), _validator);
    }

    private ImageServicingWorkspace Mounted()
    {
        var mount = Path.Combine(_root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "config"));
        File.WriteAllBytes(Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), new byte[8]);
        return new ImageServicingWorkspace
        {
            WorkingDirectory = _root,
            MountDirectory = mount,
            WorkingImagePath = Path.Combine(_root, "image", "install.wim"),
            State = ServicingWorkspaceState.Mounted
        };
    }

    // REAL DISM output copied from a Windows 11 Pro mounted image
    // (`dism /English /Image:<mount> /Get-ProvisionedAppxPackages`) — single-word
    // `DisplayName` then `PackageName` headers, with Clipchamp from the desktop
    // evidence plus two more real store apps.
    private const string AppxOut = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

DisplayName : Clipchamp.Clipchamp
Version : 4.4.10720.0
Architecture : neutral
ResourceId : ~
PackageName : Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt
Regions : all

DisplayName : Microsoft.BingWeather
Version : 4.53.53006.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe
Regions : all

DisplayName : Microsoft.Windows.Photos
Version : 2024.11020.15005.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.Windows.Photos_2024.11020.15005.0_neutral_~_8wekyb3d8bbwe
Regions : all
";

    private const string PkgOut = @"
Package Identity : Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~en-US~10.0.26100.1
State : Installed
Release Type : Language Pack

Package Identity : Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~10.0.26100.1
State : Installed
Release Type : Feature Pack
";

    [Fact]
    public async Task Discover_ReturnsAppxPackages()
    {
        Build();
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.True(inv.Discovered);
        Assert.Equal(3, inv.AppxPackages.Count);
        // The removal identity is the exact PackageName, not the DisplayName.
        Assert.Contains(inv.AppxPackages, p => p.PackageName == "Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt");
    }

    [Fact]
    public async Task Discover_ReturnsPackages_WithClassification()
    {
        Build();
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(2, inv.WindowsPackages.Count);
        Assert.Contains(inv.WindowsPackages, p => p.Classification == PackageClassification.Language);
        Assert.Contains(inv.WindowsPackages, p => p.Classification == PackageClassification.Feature);
    }

    [Fact]
    public async Task Discover_ReturnsServices_FromHive()
    {
        Build();
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(2, inv.Services.Count);
        Assert.Contains(inv.Services, s => s.ServiceName == "DiagTrack");
    }

    [Fact]
    public async Task Discover_ReturnsTrustedRegistryDefinitions()
    {
        Build();
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(2, inv.RegistrySettings.Count);
    }

    [Fact]
    public async Task Discover_Skips_WhenSessionNotValid()
    {
        Build(sessionMatches: false);
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.False(inv.Discovered);
    }

    [Fact]
    public async Task Discover_SurfacesDismFailure_AsError_NotSilentZero()
    {
        Build();
        // DISM fails (non-zero exit) for every command.
        _runner.Responder = req => new ProcessResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = "Some DISM error" };
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        // The pass still ran against the mounted session, but the failures must
        // be surfaced — NOT collapsed into a misleading "0 discovered".
        Assert.True(inv.Discovered);
        Assert.Equal(DiscoverySourceStatus.Failed, inv.AppxStatus);
        Assert.Equal(DiscoverySourceStatus.Failed, inv.PackageStatus);
        Assert.False(string.IsNullOrEmpty(inv.AppxError));
        Assert.Empty(inv.AppxPackages);
    }

    [Fact]
    public async Task Discover_SurfacesLocalizedOutput_AsError()
    {
        Build();
        _runner.Responder = req =>
        {
            if (req.Arguments.Contains("/Get-ProvisionedAppxPackages"))
            {
                // German-style output: no English banner, no English key.
                return new ProcessResult { ExitCode = 0, StandardOutput = "Paketname : Microsoft.X\nAnzeigename : X\n" };
            }
            if (req.Arguments.Contains("/Get-Packages"))
            {
                return new ProcessResult { ExitCode = 0, StandardOutput = "Paketidentität : Microsoft.Y\n" };
            }
            return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
        };
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        // Exit 0 but unrecognized/localized output is an explicit failure.
        Assert.Equal(DiscoverySourceStatus.Failed, inv.AppxStatus);
        Assert.False(string.IsNullOrEmpty(inv.AppxError));
        Assert.Empty(inv.AppxPackages);
    }

    [Fact]
    public async Task Discover_SurfacesHiveLoadFailure_AsError()
    {
        Build();
        _registry.ThrowOnLoad = true;
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        // A failed SYSTEM hive load must surface as an error, not "0 services".
        Assert.Equal(DiscoverySourceStatus.Failed, inv.ServiceStatus);
        Assert.False(string.IsNullOrEmpty(inv.ServiceError));
        Assert.Empty(inv.Services);
    }

    [Fact]
    public async Task Discover_ResolvesNonDefaultControlSet()
    {
        Build();
        // The active control set is ControlSet002, not the usual 001.
        _registry.Values["WinForge_SYSTEM|Select|Current"] = "2";
        _registry.SubKeys["WinForge_SYSTEM|ControlSet002\\Services"] = new() { "Spooler", "Winmgmt" };
        _registry.Values["WinForge_SYSTEM|ControlSet002\\Services\\Spooler|Start"] = "2";
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(DiscoverySourceStatus.Success, inv.ServiceStatus);
        Assert.Equal(2, inv.Services.Count);
        Assert.Contains(inv.Services, s => s.ServiceName == "Spooler");
    }

    [Fact]
    public async Task Discover_SurfacesEmptyServicesEnumeration_AsError_NotSilentZero()
    {
        Build();
        // The hive loads fine (privileges/paths OK), but the resolved ControlSet's
        // Services key is empty/absent. This must surface as a failure — never a
        // misleading successful "0 services discovered".
        _registry.SubKeys.Remove("WinForge_SYSTEM|ControlSet001\\Services");
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(DiscoverySourceStatus.Failed, inv.ServiceStatus);
        Assert.False(string.IsNullOrEmpty(inv.ServiceError));
        Assert.Empty(inv.Services);
    }

    [Fact]
    public async Task Discover_GenuineZeroAppx_IsSuccess_NotFailed()
    {
        Build();
        // A valid /English DISM run that genuinely lists zero provisioned packages
        // (the banner + "will be listed" but no PackageName blocks). This is a
        // legitimate, recognizable result and must be reported as SUCCESS with 0
        // items — NOT as a failure. It is distinct from:
        //  - command failure (exit != 0)            -> Failed
        //  - unrecognized/localized output           -> Failed
        _runner.Responder = req =>
        {
            if (req.Arguments.Contains("/Get-ProvisionedAppxPackages"))
            {
                return new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

The following provisioned packages will be listed:
"
                };
            }
            if (req.Arguments.Contains("/Get-Packages"))
            {
                return new ProcessResult { ExitCode = 0, StandardOutput = PkgOut };
            }
            return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
        };
        var inv = await _service.DiscoverAsync(Mounted(), CancellationToken.None);
        Assert.Equal(DiscoverySourceStatus.Success, inv.AppxStatus);
        Assert.True(string.IsNullOrEmpty(inv.AppxError));
        Assert.Empty(inv.AppxPackages);
    }
}
