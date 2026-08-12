using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinForge.App.Converters;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression suite for the Stage 11.1 "Component Intelligence page is completely
/// blank after real discovery" defect (PHASE 11 STAGE 11.1 REAL DESKTOP DEFECT).
///
/// <para>Root cause was a missing <c>ComponentIntelligenceView.xaml.cs</c> code-behind:
/// <c>new ComponentIntelligenceView()</c> never called <c>InitializeComponent()</c>, so the
/// BAML was never loaded and <see cref="FrameworkElement.Content"/> stayed <c>null</c> — the
/// page rendered as an empty <see cref="System.Windows.Controls.Border"/>. These tests trace
/// the full production chain and lock it against regression:</para>
///
/// <list type="number">
///   <item><description>Navigation: <see cref="MainViewModel.ShowUtilityCommand"/> with
///     <see cref="PageKey.ComponentIntelligence"/> resolves the right ActiveView.</description></item>
///   <item><description>ActiveView → DataTemplate (App.xaml, DataType=ComponentIntelligenceViewModel)
///     → <see cref="ComponentIntelligenceView"/> whose Content is non-null (no silent blank).</description></item>
///   <item><description>The same VM instance stays active across navigate-away/back (discovery done on one
///     instance shows when navigated back).</description></item>
///   <item><description>The static shell (title, mode selector, Discover button, status/empty-state text)
///     is present and visible even at zero discovery results.</description></item>
///   <item><description>Populated discovery results produce visible, bound list items.</description></item>
///   <item><description>Standard mode shows only curated rows; Advanced mode additionally shows
///     unclassified rows.</description></item>
///   <item><description>View construction cannot silently degrade to a blank page — it must either render
///     a populated tree or throw.</description></item>
///   <item><description>Shell text loads under both zh-CN and en-US.</description></item>
/// </list>
/// </summary>
[Collection("WpfSta")]
public class ComponentIntelligenceNavigationTests
{
    // ---- Minimal fakes (mirror ComponentIntelligenceViewModelTests) ----

    private sealed class StubService : IComponentIntelligenceService
    {
        public ComponentInventory Result { get; set; } = new ComponentInventory();
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private static ComponentInventory MakeDiscoveredInventory() => new ComponentInventory
    {
        Discovered = true,
        Cancelled = false,
        Categories = new List<CategoryDiscoveryResult>
        {
            new CategoryDiscoveryResult
            {
                Category = ComponentCategory.AppX,
                Status = InventoryStatus.Success,
                Items = new List<IRawInventoryItem>
                {
                    new RawAppxPackage
                    {
                        Category = ComponentCategory.AppX,
                        RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                        DisplayName = "Microsoft.BingWeather",
                        State = "Provisioned"
                    },
                    new RawAppxPackage
                    {
                        Category = ComponentCategory.AppX,
                        RawIdentity = "Contoso.Fabrikam_8wekyb3d8bbwe",
                        DisplayName = "Fabrikam",
                        State = "Provisioned"
                    }
                }
            }
        }
    };

    // ---- STA + resource helpers ----

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new Exception("STA run failed — see inner exception for the full WPF chain.", captured);
        }
    }

    /// <summary>
    /// Installs the converters/styles the view's BAML references as application-scope
    /// resources, then installs the localization service as the <c>Loc</c> resource the
    /// XAML binds through. Registered explicitly (guarded by <c>Contains</c>) so the tests
    /// are robust regardless of whether a prior test in the run already created an
    /// <see cref="Application"/> (the WPF Application is process-wide).
    /// </summary>
    private static void EnsureAppResources(ILocalizationService loc)
    {
        if (Application.Current is null)
        {
            new Application();
        }

        var res = Application.Current!.Resources;
        if (!res.Contains("locKey")) res.Add("locKey", new LocKeyMultiConverter());
        if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new BooleanToVisibilityConverter());
        if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
        if (!res.Contains("NullToVis")) res.Add("NullToVis", new NullToVisibilityConverter());
        if (!res.Contains("StatusTile")) res.Add("StatusTile", new Style(typeof(Border)));
        if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
        if (!res.Contains("recColor")) res.Add("recColor", new RecommendationToColorConverter());
        if (!res.Contains("riskColor")) res.Add("riskColor", new RiskToColorConverter());

        res["Loc"] = loc;
    }

    private static ResourceManagerLocalizationService RealLocalizer(CultureInfo culture)
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(ComponentIntelligenceView).Assembly);
        return new ResourceManagerLocalizationService(rm, culture);
    }

    private static ComponentIntelligenceViewModel BuildCuratedVm(ILocalizationService loc)
    {
        var state = new AppState();
        var svc = new StubService();
        var catalog = new CuratedComponentCatalog();
        return new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(), svc, catalog, loc);
    }

    private static ComponentIntelligenceViewModel BuildDiscoveredVm(ILocalizationService loc)
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            }
        };
        var svc = new StubService { Result = MakeDiscoveredInventory() };
        var catalog = new CuratedComponentCatalog();
        return new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(), svc, catalog, loc);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
            {
                yield return t;
            }

            foreach (var d in Descendants<T>(child))
            {
                yield return d;
            }
        }
    }

    // =========================================================================
    // 1. Navigation produces a VISIBLE (non-blank) ActiveView
    // =========================================================================

    [Fact]
    public void Navigation_ComponentIntelligence_ProducesVisibleView()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            using var provider = Bootstrapper.Build();
            var mainVm = provider.GetRequiredService<MainViewModel>();
            mainVm.ShowUtilityCommand.Execute(PageKey.ComponentIntelligence);

            // Navigation resolves the correct view model.
            var active = Assert.IsType<ComponentIntelligenceViewModel>(mainVm.ActiveView);

            // The production shell renders this VM through the App.xaml DataTemplate
            // (DataType=ComponentIntelligenceViewModel -> ComponentIntelligenceView).
            // Constructing that exact view type with the navigated VM as DataContext
            // proves the page renders VISIBLE (non-blank) rather than an empty Border.
            var view = new ComponentIntelligenceView { DataContext = active };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            // ROOT-CAUSE REGRESSION: without the code-behind, Content is null and the
            // page is silently blank. A populated, non-empty visual tree must exist.
            Assert.NotNull(view.Content);
            Assert.True(VisualTreeHelper.GetChildrenCount(view) > 0);
        });
    }

    // =========================================================================
    // 2. The DataContext the shell binds is the intended VM INSTANCE
    // =========================================================================

    [Fact]
    public void Navigation_View_DataContext_IsIntendedViewModelInstance()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            using var provider = Bootstrapper.Build();
            var mainVm = provider.GetRequiredService<MainViewModel>();
            mainVm.ShowUtilityCommand.Execute(PageKey.ComponentIntelligence);

            var active = Assert.IsType<ComponentIntelligenceViewModel>(mainVm.ActiveView);

            // The same view type the DataTemplate produces, bound to the navigated VM.
            var view = new ComponentIntelligenceView { DataContext = mainVm.ActiveView };

            // The navigated instance (not a fresh copy) is what the view binds to.
            Assert.Same(active, view.DataContext);
            Assert.NotNull(view.Content);
        });
    }

    // =========================================================================
    // 3. The SAME VM instance stays active after navigate-away/back
    // =========================================================================

    [Fact]
    public void SameViewModelInstanceStaysActive_AfterNavigateAwayAndBack()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            using var provider = Bootstrapper.Build();
            var mainVm = provider.GetRequiredService<MainViewModel>();

            mainVm.ShowUtilityCommand.Execute(PageKey.ComponentIntelligence);
            var first = mainVm.ActiveView;
            Assert.IsType<ComponentIntelligenceViewModel>(first);

            // Navigate away (e.g. to Logs) ...
            mainVm.ShowUtilityCommand.Execute(PageKey.Logs);
            Assert.IsType<LogsViewModel>(mainVm.ActiveView);

            // ... and back. Discovery performed on `first` must still be the active instance.
            mainVm.ShowUtilityCommand.Execute(PageKey.ComponentIntelligence);
            Assert.Same(first, mainVm.ActiveView);
        });
    }

    // =========================================================================
    // 4. XAML load: static shell present at ZERO results (never blank)
    // =========================================================================

    [Fact]
    public void XamlLoad_StaticShellPresent_AtZeroResults()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildCuratedVm(loc); // zero discovery, curated seed only
            var view = new ComponentIntelligenceView { DataContext = vm };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            // The shell controls (independent of discovery) must be present.
            Assert.Contains(Descendants<Button>(view), _ => true);
            Assert.Contains(Descendants<CheckBox>(view), _ => true);

            var expectedTitle = loc["ComponentIntelligence.Title"];
            var titleBlocks = Descendants<TextBlock>(view)
                .Where(tb => !string.IsNullOrEmpty(tb.Text) && tb.Text == expectedTitle)
                .ToList();
            Assert.Contains(titleBlocks, tb => !string.IsNullOrEmpty(tb.Text));
        });
    }

    // =========================================================================
    // 5. Zero-result state is VISIBLE (not blank)
    // =========================================================================

    [Fact]
    public void XamlLoad_ZeroResult_Visible_NotBlank()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildCuratedVm(loc); // no discovery performed
            var view = new ComponentIntelligenceView { DataContext = vm };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            var nonEmpty = Descendants<TextBlock>(view)
                .Where(tb => !string.IsNullOrEmpty(tb.Text))
                .ToList();
            Assert.True(nonEmpty.Count >= 3, $"Expected >=3 non-empty text blocks, got {nonEmpty.Count}");

            // The empty-state status message ("NoImage") is shown, not hidden/blank.
            var expectedStatus = loc["ComponentIntelligence.NoImage"];
            Assert.Contains(nonEmpty, tb => tb.Text == expectedStatus);
        });
    }

    // =========================================================================
    // 6. Populated discovery results produce VISIBLE, bound list items
    // =========================================================================

    [Fact]
    public void PopulatedDiscovery_ProducesVisibleListItems()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildDiscoveredVm(loc);
            vm.DiscoverAsync().GetAwaiter().GetResult();

            Assert.True(vm.HasInventory);
            Assert.True(vm.Entries.Count > 0);

            var view = new ComponentIntelligenceView { DataContext = vm };
            // Apply the control template + lay out so the ListView exists in the tree.
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();
            var listView = Descendants<ListView>(view).First();
            // Force all containers to generate (the default virtualizing panel only
            // materializes visible items; in a disconnected tree that can be zero).
            VirtualizingStackPanel.SetIsVirtualizing(listView, false);
            view.UpdateLayout();

            // The list is bound to the populated collection and renders real items.
            Assert.Equal(vm.Entries.Count, listView.Items.Count);
            var items = Descendants<ListViewItem>(view).ToList();
            Assert.Equal(vm.Entries.Count, items.Count);
        });
    }

    // =========================================================================
    // 7. Standard mode shows ONLY curated rows
    // =========================================================================

    [Fact]
    public void StandardMode_ShowsOnlyCurated()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildDiscoveredVm(loc);
            vm.DiscoverAsync().GetAwaiter().GetResult();
            vm.StandardMode = true;

            Assert.True(vm.StandardMode);
            Assert.All(vm.Entries, e => Assert.True(e.IsCurated));
            Assert.Equal(vm.CuratedCount, vm.Entries.Count);

            // And the rendered list reflects exactly those curated rows.
            var view = new ComponentIntelligenceView { DataContext = vm };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();
            var listView = Descendants<ListView>(view).First();
            VirtualizingStackPanel.SetIsVirtualizing(listView, false);
            view.UpdateLayout();

            Assert.Equal(vm.CuratedCount, listView.Items.Count);
            Assert.All(listView.Items.Cast<ComponentListItem>(), i => Assert.True(i.IsCurated));
        });
    }

    // =========================================================================
    // 8. Advanced mode shows UNCLASSIFIED rows too
    // =========================================================================

    [Fact]
    public void AdvancedMode_ShowsUnclassified()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildDiscoveredVm(loc);
            vm.DiscoverAsync().GetAwaiter().GetResult();
            vm.StandardMode = false;

            Assert.False(vm.StandardMode);
            Assert.Contains(vm.Entries, e => !e.IsCurated); // the unmatched Fabrikam package
            Assert.Equal(vm.CuratedCount + vm.UnclassifiedCount, vm.Entries.Count);

            var view = new ComponentIntelligenceView { DataContext = vm };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();
            var listView = Descendants<ListView>(view).First();
            VirtualizingStackPanel.SetIsVirtualizing(listView, false);
            view.UpdateLayout();

            Assert.Equal(vm.Entries.Count, listView.Items.Count);
            Assert.Contains(listView.Items.Cast<ComponentListItem>(), i => !i.IsCurated);
        });
    }

    // =========================================================================
    // 9. View construction cannot silently degrade to a blank page
    // =========================================================================

    [Fact]
    public void ViewConstruction_ExceptionCannotSilentlyDegradeToBlank()
    {
        RunSta(() =>
        {
            var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
            EnsureAppResources(loc);

            var vm = BuildCuratedVm(loc);

            // The real defect was SILENT: no exception, just a null-Content blank page.
            // Construct + lay out; it must either render a populated tree or throw —
            // never produce a null-Content/empty control.
            var view = new ComponentIntelligenceView { DataContext = vm };
            Assert.NotNull(view.Content); // root-cause guard

            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            // The title must actually resolve through locKey (a broken binding would
            // leave it empty rather than throwing in some configs) — guards Mode=OneWay.
            var expectedTitle = loc["ComponentIntelligence.Title"];
            var titles = Descendants<TextBlock>(view)
                .Where(tb => tb.Text == expectedTitle)
                .ToList();
            Assert.Contains(titles, tb => !string.IsNullOrEmpty(tb.Text));

            var nonEmpty = Descendants<TextBlock>(view).Count(tb => !string.IsNullOrEmpty(tb.Text));
            Assert.True(nonEmpty >= 3, $"Expected >=3 non-empty text blocks, got {nonEmpty}");
            Assert.Contains(Descendants<Button>(view), _ => true);
            Assert.Contains(Descendants<CheckBox>(view), _ => true);
        });
    }

    // =========================================================================
    // 10. Shell text loads under zh-CN and en-US
    // =========================================================================

    [Fact]
    public void ShellTextLoads_ZhCnAndEnUs()
    {
        var cases = new[]
        {
            ("zh-CN", "高级组件检查器"),
            ("en", "Component Inspector"),
        };

        foreach (var (culture, expectedTitle) in cases)
        {
            RunSta(() =>
            {
                var loc = RealLocalizer(CultureInfo.GetCultureInfo(culture));
                EnsureAppResources(loc);

                var vm = BuildCuratedVm(loc);
                var view = new ComponentIntelligenceView { DataContext = vm };
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                view.UpdateLayout();

                Assert.Contains(
                    Descendants<TextBlock>(view),
                    tb => tb.Text == expectedTitle);
            });
        }
    }
}
