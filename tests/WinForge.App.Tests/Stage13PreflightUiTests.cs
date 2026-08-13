using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.App.Views;
using WinForge.Core.Compatibility;
using WinForge.Core.Models;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 13 real-desktop blocker: the compatibility preflight
/// UI did not appear after image detection. Root cause: the profile setter's
/// SetField only notified "CompatibilityProfile", but the View binds to DERIVED
/// properties (CompatibilityStatusText / DetailsText / HasCompatibility*), which
/// were never notified — the section stayed empty. Selected-edition changes also
/// never refreshed edition-specific facts. Fixed by notifying the derived
/// properties from the profile setter + SelectedEdition setter, and by making the
/// section visible via HasCompatibilityProfile with always-visible details.
/// </summary>
public class Stage13PreflightUiTests
{
    // Real 25H2 zh-CN Consumer multi-index fixture (matches the real incident data).
    private static IsoInspectionResult RealLike26200Pro()
    {
        var editions = new List<WindowsEditionInfo>
        {
            New(1, "Windows 11 Home", "Core"),
            New(2, "Windows 11 Home Single Language", "CoreSingleLanguage"),
            New(3, "Windows 11 Education", "Education"),
            New(4, "Windows 11 Pro", "Professional"),
            New(5, "Windows 11 Pro Education", "ProfessionalEducation"),
            New(6, "Windows 11 Pro for Workstations", "ProfessionalWorkstation"),
        };
        return new IsoInspectionResult
        {
            IsoPath = @"C:\media\Win11_25H2_Chinese_Simplified_x64_v2.iso",
            Status = IsoInspectionStatus.Completed,
            DetectedType = IsoDetectedType.WindowsIsoCandidate,
            HasBootDirectory = true,
            HasSourcesDirectory = true,
            HasBootWim = true,
            HasInstallWim = true,
            InstallImageType = InstallImageType.Wim,
            SelectedIndex = 4,
            ImageMetadata = new WindowsImageMetadataResult
            {
                Status = WindowsImageMetadataStatus.Completed,
                Version = "10.0.26200.1000",
                Build = "26200",
                Architecture = "x64",
                Languages = new List<string> { "zh-CN" },
                Editions = editions,
            },
        };
    }

    private static WindowsEditionInfo New(int index, string name, string editionId) => new()
    {
        Index = index,
        Name = name,
        EditionId = editionId,
        Architecture = "x64",
        Version = "10.0.26200.1000",
        Build = "26200",
        InstallationType = "Client",
        DefaultLanguage = "zh-CN",
        DisplayVersion = "25H2",
        Languages = new List<string> { "zh-CN" },
    };

    private sealed class FakeInspection : IIsoInspectionService
    {
        public IsoInspectionResult Result { get; set; } = new() { Status = IsoInspectionStatus.NotInspected };
        public Task<IsoInspectionResult> InspectAsync(string isoPath, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private static ImageViewModel MakeVm(IsoInspectionResult? result)
    {
        var state = new AppState();
        state.SourceImagePath = @"C://media//Win11.iso";
        var vm = new ImageViewModel(state, new InMemoryLoggerService(),
            new FakeInspection { Result = result ?? new IsoInspectionResult { Status = IsoInspectionStatus.NotInspected } },
            new WorkflowAndCommandTests.FakeFilePicker(),
            new WorkflowAndCommandTests.FakeWorkspaceFactory(),
            new WorkflowAndCommandTests.FakeWimService(),
            new FakeImageServicingService());
        return vm;
    }

    private static async Task InspectAsync(ImageViewModel vm)
    {
        if (vm.InspectIsoCommand is Mvvm.AsyncRelayCommand cmd)
        {
            await cmd.ExecuteAsync(null);
        }
    }

    // 1. real-like 26200 zh-CN Pro fixture produces a compatibility profile
    [Fact]
    public void RealLike26200_Produces_Supported_Profile()
    {
        var p = new CompatibilityRuleEngine().Evaluate(RealLike26200Pro());
        Assert.NotNull(p);
        Assert.Equal(WindowsRelease.Windows11_25H2, p.Release);
        Assert.Equal(26200, p.Build);
        Assert.Equal(ImageFormatKind.Wim, p.ImageFormat);
        Assert.Equal(MediaClassification.MicrosoftOfficialLike, p.MediaType);
        Assert.Equal(CompatibilityStatus.Supported, p.Status);
    }

    // 2. ImageViewModel receives it after detection
    [Fact]
    public async Task ViewModel_Receives_Profile_After_Detection()
    {
        var vm = MakeVm(RealLike26200Pro());
        await InspectAsync(vm);
        Assert.NotNull(vm.CompatibilityProfile);
        Assert.True(vm.HasCompatibilityProfile);
    }

    // 3. compatibility section Visible after detection (real WPF render)
    [Fact]
    public void Section_Visible_After_Detection_In_Rendered_View()
    {
        var vm = MakeVm(RealLike26200Pro());
        InspectAsync(vm).GetAwaiter().GetResult();

        string? status = null;
        string? details = null;
        var ex = RunSta(() =>
        {
            var view = new ImageView { DataContext = vm };
            view.Measure(new Size(900, 900));
            view.Arrange(new Rect(0, 0, 900, 900));
            view.UpdateLayout();

            var texts = AllTextBlocks(view).Select(t => t.Text).ToList();
            status = texts.FirstOrDefault(t => t != null && t.Contains("Windows 11 25H2", StringComparison.Ordinal));
            details = texts.FirstOrDefault(t => t != null && t.Contains("WIM", StringComparison.Ordinal));
        });
        Assert.Null(ex);
        Assert.False(string.IsNullOrWhiteSpace(status), "compatibility status text must be visible");
        Assert.False(string.IsNullOrWhiteSpace(details), "compatibility row details must be visible");
    }

    // 4-10. compact row: release/arch/wim/status render; edition appears on selection
    [Fact]
    public void Compact_Row_Shows_Media_Level_Without_Edition()
    {
        var vm = MakeVm(RealLike26200Pro());
        InspectAsync(vm).GetAwaiter().GetResult();

        // Media-level row BEFORE any edition selection.
        Assert.Contains("Windows 11 25H2", vm.CompatibilityStatusText);
        Assert.Contains("x64", vm.CompatibilityStatusText);
        Assert.Contains("WIM", vm.CompatibilityStatusText);
        Assert.Contains("✓ 支持", vm.CompatibilityStatusText);
        Assert.DoesNotContain("Pro", vm.CompatibilityStatusText); // no edition selected yet
    }

    // 11. switching edition refreshes compatibility (no re-detect)
    [Fact]
    public void Switching_Edition_Refreshes_Compatibility()
    {
        var vm = MakeVm(RealLike26200Pro());
        InspectAsync(vm).GetAwaiter().GetResult();

        // Select Home first — the row gains the edition identity (fallback = EditionId).
        vm.SelectedEdition = vm.Editions.First(e => e.EditionId == "Core");
        Assert.Contains("Core", vm.CompatibilityStatusText); // fallback edition name

        // Switch to Pro (index 4) — no second Detect.
        vm.SelectedEdition = vm.Editions.First(e => e.EditionId == "Professional");
        Assert.Contains("Pro", vm.CompatibilityStatusText);
        Assert.Contains("Windows 11 25H2", vm.CompatibilityStatusText);
    }

    // 12. warning state renders
    [Fact]
    public void Warning_State_Renders()
    {
        var vm = MakeVm(Stage13CompatibilityFixtures.EsdMedia());
        InspectAsync(vm).GetAwaiter().GetResult();
        Assert.True(vm.HasCompatibilityWarnings);
        Assert.False(string.IsNullOrWhiteSpace(vm.CompatibilityWarningsText));
        Assert.Contains("ESD", vm.CompatibilityStatusText);
        Assert.Contains("仅检查支持", vm.CompatibilityStatusText);
    }

    // 13. blocking state renders
    [Fact]
    public void Blocking_State_Renders()
    {
        var vm = MakeVm(Stage13CompatibilityFixtures.Arm64Media());
        InspectAsync(vm).GetAwaiter().GetResult();
        Assert.True(vm.HasCompatibilityBlockers);
        Assert.False(string.IsNullOrWhiteSpace(vm.CompatibilityBlockersText));
        Assert.Contains("arm64", vm.CompatibilityStatusText);
        Assert.Contains("✕ 当前不支持", vm.CompatibilityStatusText);
    }

    // 14. null/not-yet-detected state hides section
    [Fact]
    public void NotDetected_Hides_Section()
    {
        var vm = MakeVm(null);
        Assert.False(vm.HasCompatibilityProfile);
        Assert.Equal(string.Empty, vm.CompatibilityStatusText);

        var ex = RunSta(() =>
        {
            var view = new ImageView { DataContext = vm };
            view.Measure(new Size(900, 900));
            view.Arrange(new Rect(0, 0, 900, 900));
            view.UpdateLayout();
        });
        Assert.Null(ex); // renders without binding errors when hidden
    }

    // 15. zh-CN / en-US
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Localized_Status_Renders(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(ImageViewModel).Assembly);
        var loc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));

        var state = new AppState();
        state.SourceImagePath = @"C://media//Win11.iso";
        var vm = new ImageViewModel(state, new InMemoryLoggerService(),
            new FakeInspection { Result = RealLike26200Pro() },
            new WorkflowAndCommandTests.FakeFilePicker(),
            new WorkflowAndCommandTests.FakeWorkspaceFactory(),
            new WorkflowAndCommandTests.FakeWimService(),
            new FakeImageServicingService(), loc);
        InspectAsync(vm).GetAwaiter().GetResult(); // detection first (clears edition)
        vm.SelectedEdition = vm.Editions.First(e => e.EditionId == "Professional");
        Assert.Contains("Windows 11 25H2", vm.CompatibilityStatusText);
        Assert.Contains(culture == "zh-CN" ? "专业版" : "Pro", vm.CompatibilityStatusText);
        Assert.Contains(culture == "zh-CN" ? "支持" : "Supported", vm.CompatibilityStatusText);
    }

    // 16. real WPF render/binding audit is covered by the binding-audit suite
    //     (ImageView.CompatibilityPreflight case in CustomizeBindingRegressionTests)

    private static readonly object ResourceLock = new();

    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                lock (ResourceLock)
                {
                    var app = Application.Current ?? new Application();
                    var res = app.Resources;
                    if (!res.Contains("locKey")) res.Add("locKey", new WinForge.App.Converters.LocKeyMultiConverter());
                    if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new System.Windows.Controls.BooleanToVisibilityConverter());
                    if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new Converters.BooleanToVisibilityInverseConverter());
                    if (!res.Contains("BoolToBold")) res.Add("BoolToBold", new Converters.BooleanToFontWeightConverter());
                    if (!res.Contains("NullToVis")) res.Add("NullToVis", new Converters.NullToVisibilityConverter());
                    if (!res.Contains("NullEmptyToVis")) res.Add("NullEmptyToVis", new Converters.StringNullOrEmptyToVisibilityConverter());
                    if (!res.Contains("StatusTile")) res.Add("StatusTile", new Style(typeof(Border)));
                    if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
                    if (!res.Contains("FieldLabel")) res.Add("FieldLabel", new Style(typeof(TextBlock)));
                    res["Loc"] = new FakeLoc();
                }

                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    private static IEnumerable<TextBlock> AllTextBlocks(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb;
            }

            foreach (var nested in AllTextBlocks(child))
            {
                yield return nested;
            }
        }
    }
}

/// <summary>Additional fixtures used by the preflight UI tests.</summary>
public static class Stage13CompatibilityFixtures
{
    public static WinForge.Core.Models.IsoInspectionResult EsdMedia()
    {
        var r = Stage13FixturesBase.Completed(imageType: InstallImageType.Esd);
        return r;
    }

    public static WinForge.Core.Models.IsoInspectionResult Arm64Media()
    {
        var r = Stage13FixturesBase.Completed(arch: "arm64");
        return r;
    }
}

internal static class Stage13FixturesBase
{
    public static WinForge.Core.Models.IsoInspectionResult Completed(
        string editionId = "Professional",
        string? arch = "x64",
        string? lang = "zh-CN",
        InstallImageType imageType = InstallImageType.Wim)
    {
        var editions = new List<WindowsEditionInfo>
        {
            new()
            {
                Index = 1,
                Name = "Windows 11 Professional",
                EditionId = editionId,
                Architecture = arch,
                Version = "10.0.26200.1000",
                Build = "26200",
                InstallationType = "Client",
                DefaultLanguage = lang,
                DisplayVersion = "25H2",
                Languages = new List<string> { lang! },
            },
        };
        return new WinForge.Core.Models.IsoInspectionResult
        {
            IsoPath = @"C:\media\Win11.iso",
            Status = IsoInspectionStatus.Completed,
            DetectedType = IsoDetectedType.WindowsIsoCandidate,
            HasBootDirectory = true,
            HasSourcesDirectory = true,
            HasBootWim = true,
            HasInstallWim = imageType == InstallImageType.Wim,
            HasInstallEsd = imageType == InstallImageType.Esd,
            InstallImageType = imageType,
            SelectedIndex = 1,
            ImageMetadata = new WindowsImageMetadataResult
            {
                Status = WindowsImageMetadataStatus.Completed,
                Version = "10.0.26200.1000",
                Build = "26200",
                Architecture = arch,
                Languages = new List<string> { lang! },
                Editions = editions,
            },
        };
    }
}


/// <summary>Stub localization service for STA render tests (keys fall back to themselves).</summary>
internal sealed class FakeLoc : ILocalizationService
{
    public System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.GetCultureInfo("en");
    public event System.EventHandler? CultureChanged { add { } remove { } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string this[string key] => key;
    public bool Contains(string key) => true;
    public void SetCulture(System.Globalization.CultureInfo culture) { }
}
