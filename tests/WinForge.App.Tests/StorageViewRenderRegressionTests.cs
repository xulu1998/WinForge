using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinForge.App.Converters;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REAL-DESKTOP BLOCKER regression (Phase 12 Stage 12.2): the Storage page used
/// <c>&lt;Run&gt;</c> inlines (including <c>Run.Text</c> MultiBindings) to compose
/// the usage line. On the real desktop WPF threw while setting
/// 'System.Windows.Documents.Run.Text' when the previously-collapsed TextBlock's
/// inlines were materialized during layout, producing repeated global error
/// dialogs. Compile-time checks passed because the XAML is legal; the old
/// render smoke test only measured a view whose scanned-state StackPanel was
/// still Collapsed, so WPF skipped the Inlines entirely.
///
/// This harness constructs the REAL StorageViewModel, activates the exact state
/// the user reaches (Scan → visible layout path), forces a full layout pass with
/// the REAL resx localization service (zh-CN + en-US), and asserts no exception
/// is thrown and the display properties are populated. It fails if a <Run> ever
/// comes back into StorageView.xaml.
/// </summary>
[Collection("WpfSta")]
public class StorageViewRenderRegressionTests
{
    private static Dispatcher? _sharedDispatcher;
    private static readonly object _sharedGate = new();

    private static void RunWpf(Action body)
    {
        lock (_sharedGate)
        {
            if (_sharedDispatcher is null)
            {
                var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    InstallAppResources();
                    _sharedDispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                ready.Wait();
            }

            Exception? captured = null;
            var done = new ManualResetEventSlim();
            _sharedDispatcher!.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { body(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            }));
            if (!done.Wait(TimeSpan.FromSeconds(120)))
            {
                throw new TimeoutException("WPF test body did not complete within 120s (possible deadlock).");
            }

            if (captured is not null) throw captured;
        }
    }

    private static void InstallAppResources()
    {
        if (Application.Current is null) _ = new Application();
        var res = Application.Current!.Resources;
        if (!res.Contains("locKey")) res.Add("locKey", new LocKeyMultiConverter());
        if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new BooleanToVisibilityConverter());
        if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
        if (!res.Contains("NullEmptyToVis")) res.Add("NullEmptyToVis", new StringNullOrEmptyToVisibilityConverter());
        if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
    }

    private static WinForge.App.Localization.ResourceManagerLocalizationService RealLoc(string cultureName)
    {
        // Strings.resources is embedded in the WinForge.App assembly (not the test
        // assembly) — a wrong assembly here throws MissingManifestResourceException
        // on every key lookup.
        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(StorageViewModel).Assembly);
        var loc = new WinForge.App.Localization.ResourceManagerLocalizationService(
            rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(cultureName));
        return loc;
    }

    private static (StorageViewModel Storage, WorkspaceLifecycleManager Lifecycle) BuildStorage(
        WinForge.App.Localization.ResourceManagerLocalizationService loc)
    {
        var root = Path.Combine(Path.GetTempPath(), "wf12_render_" + Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePathProvider(root);
        var runner = new FakeProcessRunner
        {
            Responder = _ => new ProcessResult { ExitCode = 0, StandardOutput = "No mounted images found." },
        };
        var lifecycle = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), new InMemoryLoggerService());
        var settings = new WorkspaceRootSettingsService(
            Path.Combine(Path.GetTempPath(), "wf12_roots_" + Guid.NewGuid().ToString("N") + ".json"));
        var storage = new StorageViewModel(lifecycle, loc, settings);
        return (storage, lifecycle);
    }

    private static void ForceLayout(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }

    // ---- Blocker regression: the scanned state (visible usage line) renders ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void StorageView_AfterScan_Renders_Without_Exception(string cultureName)
    {
        RunWpf(() =>
        {
            var loc = RealLoc(cultureName);
            var (storage, _) = BuildStorage(loc);
            storage.ScanAsync().GetAwaiter().GetResult();

            Assert.True(storage.HasScanned);
            Assert.False(string.IsNullOrWhiteSpace(storage.TotalBytesText));
            Assert.False(string.IsNullOrWhiteSpace(storage.UsageSummaryText));
            Assert.False(string.IsNullOrWhiteSpace(storage.CurrentRootText));
            Assert.False(string.IsNullOrWhiteSpace(storage.RootFreeSpaceText));

            var view = new StorageView { DataContext = storage };
            ForceLayout(view); // throws if the Run.Text-style Inline defect regresses

            var bound = FindByBinding(view, "UsageSummaryText") as TextBlock;
            Assert.NotNull(bound);
            Assert.Equal(storage.UsageSummaryText, bound!.Text);
        });
    }

    // ---- default root / free space / low-space state ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void StorageView_Root_And_FreeSpace_Are_Populated(string cultureName)
    {
        RunWpf(() =>
        {
            var loc = RealLoc(cultureName);
            var (storage, _) = BuildStorage(loc);

            Assert.False(string.IsNullOrWhiteSpace(storage.CurrentRootText));
            Assert.False(string.IsNullOrWhiteSpace(storage.RootFreeSpaceText));
            // low-space flag is a bool; it must not throw during rendering
            _ = storage.RootLowSpaceWarning;
            _ = storage.RootLowSpaceWarningText;

            var view = new StorageView { DataContext = storage };
            ForceLayout(view);
        });
    }

    // ---- validation error state renders ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void StorageView_ValidationError_Renders(string cultureName)
    {
        RunWpf(() =>
        {
            var loc = RealLoc(cultureName);
            var (storage, _) = BuildStorage(loc);

            // A drive root is always rejected by the validator.
            var driveRoot = Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";
            var rejected = storage.TrySetRoot(driveRoot);
            Assert.False(rejected);
            Assert.False(string.IsNullOrWhiteSpace(storage.RootErrorText));

            var view = new StorageView { DataContext = storage };
            ForceLayout(view);
        });
    }

    // ---- cleanup result state renders ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void StorageView_CleanResult_Renders(string cultureName)
    {
        RunWpf(() =>
        {
            var loc = RealLoc(cultureName);
            var (storage, _) = BuildStorage(loc);

            storage.ScanAsync().GetAwaiter().GetResult();
            storage.CleanAsync().GetAwaiter().GetResult();

            // CleanAsync always sets the result line (even "0 B" reclaimed).
            Assert.False(string.IsNullOrWhiteSpace(storage.CleanResultText));
            var view = new StorageView { DataContext = storage };
            ForceLayout(view);
        });
    }

    // ---- the defect itself must never come back ----

    [Fact]
    public void StorageView_Xaml_Contains_No_Run_Inlines()
    {
        // Locate the repo root by walking up from the test output directory
        // (works with both the default bin/ layout and a custom -p:OutDir).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var xaml = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "WinForge.App", "Views", "StorageView.xaml"));
        Assert.DoesNotContain("<Run", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Run.Text", xaml, StringComparison.Ordinal);
    }

    private static FrameworkElement? FindByBinding(DependencyObject root, string path)
    {
        if (root is TextBlock tb && tb.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == path)
        {
            return tb;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindByBinding(VisualTreeHelper.GetChild(root, i), path);
            if (found is not null) return found;
        }

        return null;
    }
}
