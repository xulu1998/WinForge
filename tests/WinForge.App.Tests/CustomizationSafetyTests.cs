using System;
using System.IO;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Safety guarantees of <see cref="OfflineRegistryService"/>: the hive name must
/// be WinForge-owned (never a host hive such as HKLM\SOFTWARE) and the hive file
/// must exist. These checks run before any Win32 call, so the tests are
/// cross-platform and prove a host hive can never be targeted.
/// </summary>
public class OfflineRegistrySafetyTests
{
    private readonly OfflineRegistryService _service = new(new InMemoryLoggerService());

    [Fact]
    public void LoadHive_RejectsHostHiveName()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.LoadHive(@"C:\Windows\System32\config\SOFTWARE", "SOFTWARE"));
    }

    [Fact]
    public void LoadHive_RejectsNonWinfOfgeName_WithSeparator()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.LoadHive(@"C:\x", "WinForge_SOFTWARE\\evil"));
    }

    [Fact]
    public void LoadHive_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.LoadHive(@"C:\x", ""));
    }

    [Fact]
    public void LoadHive_RejectsMissingFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wf_nope_" + Guid.NewGuid().ToString("N") + ".hiv");
        Assert.Throws<FileNotFoundException>(() =>
            _service.LoadHive(tmp, "WinForge_SOFTWARE"));
    }

    [Fact]
    public void UnloadHive_NullHandle_IsNoOp()
    {
        // Must not throw.
        _service.UnloadHive(null!);
    }

    [Fact]
    public void OfflineHivePaths_MapsKnownBases_WithinMount()
    {
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount" };
        var path = OfflineHivePaths.GetHiveFilePath(ws, "SOFTWARE");
        Assert.Equal(@"C:\wf\mount\Windows\System32\config\SOFTWARE", path);
        Assert.Equal("WinForge_SOFTWARE", OfflineHivePaths.GetWinForgeHiveName("SOFTWARE"));
    }

    [Fact]
    public void OfflineHivePaths_UnknownBase_ReturnsNull()
    {
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount" };
        Assert.Null(OfflineHivePaths.GetHiveFilePath(ws, "SAM"));
    }
}

/// <summary>
/// <see cref="MountIdentityValidator"/> must confine every target to the mounted
/// WinForge workspace and refuse host paths / original ISO roots.
/// </summary>
public class MountIdentityValidatorTests
{
    private static ImageServicingWorkspace Mounted() => new()
    {
        WorkingDirectory = @"C:\wf\ws1",
        MountDirectory = @"C:\wf\ws1\mount",
        WorkingImagePath = @"C:\wf\ws1\image\install.wim",
        State = ServicingWorkspaceState.Mounted
    };

    [Fact]
    public void IsWithinMount_True_ForPathUnderMount()
    {
        var v = new MountIdentityValidator();
        Assert.True(v.IsWithinMount(@"C:\wf\ws1\mount\Windows\System32\config\SOFTWARE", Mounted()));
    }

    [Fact]
    public void IsWithinMount_False_ForHostPath()
    {
        var v = new MountIdentityValidator();
        Assert.False(v.IsWithinMount(@"C:\Windows\System32\config\SOFTWARE", Mounted()));
    }

    [Fact]
    public void IsWithinMount_False_ForOriginalIsoRoot()
    {
        var v = new MountIdentityValidator();
        Assert.False(v.IsWithinMount(@"D:\sources\install.wim", Mounted()));
    }

    [Fact]
    public void MatchesSession_True_ForOwnedWorkspace()
    {
        var v = new MountIdentityValidator();
        Assert.True(v.MatchesSession(Mounted()));
    }

    [Fact]
    public void MatchesSession_False_WhenMountOutsideWorkspace()
    {
        var v = new MountIdentityValidator();
        var ws = Mounted();
        ws.MountDirectory = @"C:\elsewhere\mount";
        Assert.False(v.MatchesSession(ws));
    }

    [Fact]
    public void MatchesSession_False_WhenFieldsMissing()
    {
        var v = new MountIdentityValidator();
        Assert.False(v.MatchesSession(new ImageServicingWorkspace()));
    }
}

/// <summary>
/// The trusted definition provider must only emit well-formed, offline-applicable,
/// known-target definitions (no signed-in user, no cloud/account policy).
/// </summary>
public class CustomizationDefinitionProviderTests
{
    [Fact]
    public void PrivacySettings_AreWellFormed()
    {
        var provider = new CustomizationDefinitionProvider();
        var privacy = provider.GetPrivacySettings();
        Assert.NotEmpty(privacy);
        foreach (var s in privacy)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.SettingId));
            Assert.Equal(CustomizationCategory.Privacy, s.Category);
            Assert.True(OfflineHivePaths.IsKnownBase(s.Hive));
            Assert.False(string.IsNullOrWhiteSpace(s.KeyPath));
            Assert.False(string.IsNullOrWhiteSpace(s.ValueName));
        }
    }

    [Fact]
    public void RecommendedServices_HaveRecommendedStartType()
    {
        var provider = new CustomizationDefinitionProvider();
        var services = provider.GetRecommendedServiceChanges();
        Assert.NotEmpty(services);
        foreach (var svc in services)
        {
            Assert.False(string.IsNullOrWhiteSpace(svc.ServiceName));
            Assert.True(svc.RecommendedStartType.HasValue);
        }
    }

    [Fact]
    public void SystemSettings_AreWellFormed()
    {
        var provider = new CustomizationDefinitionProvider();
        var system = provider.GetSystemSettings();
        Assert.NotEmpty(system);
        Assert.All(system, s => Assert.Equal(CustomizationCategory.System, s.Category));
    }
}
