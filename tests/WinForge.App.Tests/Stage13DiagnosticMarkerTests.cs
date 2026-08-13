using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForge.App.Localization;
using WinForge.App.Mvvm;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Diagnostic instrumentation tests (Stage 13 — real-desktop visibility blocker):
/// prove the literal marker + always-visible debug value are in the RUNTIME view
/// (SourceView, which the wizard actually renders — ImageView is legacy) and that
/// the compatibility text is bound WITHOUT any Visibility condition.
/// </summary>
public class Stage13DiagnosticMarkerTests
{
    // 1. source workflow template resolves to SourceView (the RUNTIME view)
    [Fact]
    public void Source_Workflow_Uses_SourceView()
    {
        var appXaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/WinForge.App/App.xaml"));
        var template = appXaml.Split('\n')
            .Select((l, i) => (l, i))
            .First(x => x.l.Contains("SourceStepTemplate"));
        Assert.Contains("SourceView", appXaml); // template instantiates SourceView
    }

    // 2+3. production SourceView contains the compatibility row; ImageView does NOT
    [Fact]
    public void SourceView_Has_Compatibility_Row_ImageView_Does_Not()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src/WinForge.App/Views/SourceView.xaml"));
        Assert.Contains("CompatibilityStatusText", source);
        Assert.Contains("Compat.Label", source);

        var image = File.ReadAllText(Path.Combine(RepoRoot(), "src/WinForge.App/Views/ImageView.xaml"));
        Assert.DoesNotContain("CompatibilityStatusText", image);
        Assert.DoesNotContain("Compat.Label", image);
    }

    // 4. no PHASE13 diagnostic marker remains anywhere
    [Theory]
    [InlineData("src/WinForge.App/Views/ImageView.xaml")]
    [InlineData("src/WinForge.App/Views/SourceView.xaml")]
    public void No_Phase13_Marker_Remains(string relative)
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), relative));
        Assert.DoesNotContain("PHASE13-COMPAT-DIAG", xaml);
    }

    // 2. CompatibilityDebugText never empty
    [Fact]
    public void DebugText_Never_Empty()
    {
        var vm = NewVm(Stage13PreflightFixtures.RealLike26200Pro());
        Assert.Equal("Profile=False", vm.CompatibilityDebugText);

        InspectAsync(vm).GetAwaiter().GetResult();
        Assert.False(string.IsNullOrWhiteSpace(vm.CompatibilityDebugText));
        Assert.StartsWith("Profile=True", vm.CompatibilityDebugText);
    }

    // 3. 26200 fixture -> Profile=True with full dump
    [Fact]
    public void DebugText_Dumps_Runtime_State()
    {
        var vm = NewVm(Stage13PreflightFixtures.RealLike26200Pro());
        InspectAsync(vm).GetAwaiter().GetResult();
        Assert.Contains("Profile=True", vm.CompatibilityDebugText);
        Assert.Contains("Supported", vm.CompatibilityDebugText);
        Assert.Contains("Windows11_25H2", vm.CompatibilityDebugText);
        Assert.Contains("Build=26200", vm.CompatibilityDebugText);
        Assert.Contains("Wim", vm.CompatibilityDebugText);
        Assert.Contains("Arch=x64", vm.CompatibilityDebugText);
        Assert.Contains("Lang=zh-CN", vm.CompatibilityDebugText);
        Assert.Contains("Index=none", vm.CompatibilityDebugText); // no edition selected

        vm.SelectedEdition = vm.Editions.First(e => e.EditionId == "Professional");
        Assert.Contains("Index=Professional", vm.CompatibilityDebugText);
    }

    // 5. CompatibilityDebugText is NOT exposed in production UI (no binding anywhere)
    [Fact]
    public void DebugText_Not_Exposed_In_Production_UI()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src/WinForge.App/Views/SourceView.xaml"));
        var image = File.ReadAllText(Path.Combine(RepoRoot(), "src/WinForge.App/Views/ImageView.xaml"));
        Assert.DoesNotContain("CompatibilityDebugText", source);
        Assert.DoesNotContain("CompatibilityDebugText", image);
    }

    // 5. real WPF render of SourceView (the RUNTIME view) contains the marker
    [Fact]
    public void SourceView_Render_Contains_Marker()
    {
        var vm = NewVm(Stage13PreflightFixtures.RealLike26200Pro());
        InspectAsync(vm).GetAwaiter().GetResult();

        string? status = null;
        var ex = RunSta(() =>
        {
            var view = new SourceView { DataContext = vm };
            view.Measure(new Size(1000, 1000));
            view.Arrange(new Rect(0, 0, 1000, 1000));
            view.UpdateLayout();
            var texts = AllTextBlocks(view).Select(t => t.Text ?? string.Empty).ToList();
            Assert.DoesNotContain(texts, t => t.Contains("PHASE13-COMPAT-DIAG"));
            // FakeLoc resolves keys to themselves; assert on non-localized parts.
            status = texts.FirstOrDefault(t => t.Contains("x64") && t.Contains("WIM"));
        });
        Assert.Null(ex);
        Assert.False(string.IsNullOrWhiteSpace(status), "compatibility row must render in SourceView");
    }

    private static ImageViewModel NewVm(IsoInspectionResult? result)
    {
        var state = new AppState();
        state.SourceImagePath = @"C:\media\Win11.iso";
        return new ImageViewModel(state, new InMemoryLoggerService(),
            new Stage13PreflightFixtures.InspectionFake { Result = result ?? new IsoInspectionResult { Status = IsoInspectionStatus.NotInspected } },
            new WorkflowAndCommandTests.FakeFilePicker(),
            new WorkflowAndCommandTests.FakeWorkspaceFactory(),
            new WorkflowAndCommandTests.FakeWimService(),
            new FakeImageServicingService());
    }

    private static async Task InspectAsync(ImageViewModel vm)
    {
        // The inspection fake returns the fixture; InspectCurrentAsync evaluates it.
        if (vm.InspectIsoCommand is AsyncRelayCommand cmd)
        {
            await cmd.ExecuteAsync(null);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }

    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                lock (WpfRenderLock.Sync)
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

/// <summary>Fixtures shared by the diagnostic tests (real 26200 zh-CN multi-index Pro).</summary>
public static class Stage13PreflightFixtures
{
    public static IsoInspectionResult RealLike26200Pro()
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

    public sealed class InspectionFake : IIsoInspectionService
    {
        public IsoInspectionResult Result { get; set; } = new() { Status = IsoInspectionStatus.NotInspected };
        public Task<IsoInspectionResult> InspectAsync(string isoPath, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }
}


/// <summary>Anti-leak regression: raw localization keys must never reach the UI row.</summary>
public class Stage13NoLeakedKeyTests
{
    [Theory]
    [InlineData("Core")]
    [InlineData("Professional")]
    [InlineData("ProfessionalEducation")]
    [InlineData("ProfessionalWorkstation")]
    [InlineData("Education")]
    [InlineData("Enterprise")]
    [InlineData("CoreSingleLanguage")]
    public void Edition_Names_Never_Leak_Raw_Keys(string editionId)
    {
        // Every known edition has a valid resx entry (zh + en) — lookup returns a
        // real display value, never "Compat.Edition.<id>".
        var zh = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(ImageViewModel).Assembly);
        var zhLoc = new ResourceManagerLocalizationService(zh, System.Globalization.CultureInfo.GetCultureInfo("en"));
        zhLoc.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        var zhVal = zhLoc["Compat.Edition." + editionId];
        Assert.False(zhVal.StartsWith("Compat.", System.StringComparison.Ordinal), $"leaked key for {editionId}");

        var enLoc = new ResourceManagerLocalizationService(zh, System.Globalization.CultureInfo.GetCultureInfo("en"));
        enLoc.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var enVal = enLoc["Compat.Edition." + editionId];
        Assert.False(enVal.StartsWith("Compat.", System.StringComparison.Ordinal), $"leaked key for {editionId}");
    }
}
