using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WinForge.App.Converters;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;
using Xunit.Abstractions;

namespace WinForge.App.Tests;

/// <summary>
/// Diagnoses / regresses the systemic WPF binding defect where display-only
/// bindings to getter-only ViewModel properties (SelectedTotal, Total*, etc.)
/// are attached in TwoWay / OneWayToSource mode against a two-way-by-default
/// target (Run.Text) and throw:
///   "无法对 ... 只读属性 'X' 进行 TwoWay 或 OneWayToSource 绑定。"
///
/// The earlier XAML smoke test only loaded views with a NULL DataContext, so the
/// binding -> property resolution never happened and these errors stayed hidden.
/// This harness sets the REAL ViewModel as DataContext, forces a layout pass so
/// bindings actually attach, and fails on any WPF binding error (trace + thrown
/// exception + DispatcherUnhandledException).
///
/// All cases run on a SINGLE STA thread with a SINGLE Application to avoid the
/// multi-second cold WPF init cost of spinning up one Application per test.
/// </summary>
[Collection("WpfSta")]
public class CustomizeBindingRegressionTests
{
    private readonly ITestOutputHelper _output;

    public CustomizeBindingRegressionTests(ITestOutputHelper output) => _output = output;

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Lines { get; } = new();
        public override void Write(string? message) { if (message is not null) Lines.Add(message); }
        public override void WriteLine(string? message) { if (message is not null) Lines.Add(message); }
    }

    private sealed class FakeLoc : ILocalizationService
    {
        private readonly object _gate = new();
        private EventHandler? _cultureChanged;
        private System.ComponentModel.PropertyChangedEventHandler? _propertyChanged;
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged { add { lock (_gate) { _cultureChanged += value; } } remove { lock (_gate) { _cultureChanged -= value; } } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { lock (_gate) { _propertyChanged += value; } } remove { lock (_gate) { _propertyChanged -= value; } } }
        public string this[string key] => string.IsNullOrEmpty(key) ? (key ?? string.Empty) : ("[loc:" + key + "]");
        public bool Contains(string key) => true;
        public void SetCulture(CultureInfo culture) => CurrentCulture = culture;
    }

    // All WPF/STA assertions in this class run on ONE shared STA thread with ONE
    // Application. WPF allows only one live Application per AppDomain and stores
    // Application.Current as an AppDomain-wide static (NOT thread-local); spinning
    // a fresh STA thread per [Fact] would inherit a stale Application bound to a
    // now-shutdown dispatcher and crash the test host. RunWpf posts the test body
    // to the shared dispatcher and waits for it to finish.
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
                    InstallAppResources(CultureInfo.GetCultureInfo("en"));
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

    private static void InstallAppResources(CultureInfo culture)
    {
        if (Application.Current is null) new Application();
        var res = Application.Current!.Resources;
        if (!res.Contains("locKey")) res.Add("locKey", new LocKeyMultiConverter());
        if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new BooleanToVisibilityConverter());
        if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
        if (!res.Contains("NullToVis")) res.Add("NullToVis", new NullToVisibilityConverter());
        // App.xaml-level resources the knowledge views resolve (missing when the
        // test Application never loads App.xaml). Register them so this class is
        // self-contained regardless of xUnit class ordering.
        if (!res.Contains("NullEmptyToVis")) res.Add("NullEmptyToVis", new StringNullOrEmptyToVisibilityConverter());
        if (!res.Contains("recColor")) res.Add("recColor", new RecommendationToColorConverter());
        if (!res.Contains("riskColor")) res.Add("riskColor", new RiskToColorConverter());
        if (!res.Contains("StatusTile")) res.Add("StatusTile", new Style(typeof(Border)));
        // The real App.xaml maps view models to views via implicit DataTemplates;
        // the test Application never loads App.xaml, so register them here to make
        // CustomizeView tab/content rendering truthful in layout tests.
        InstallCustomizeDataTemplates(res);
        if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
        // BuildView references FieldLabel (TargetType=TextBlock); the other
        // Customize views don't, so it must be registered here for the Build page
        // render cases. Match the real App.xaml target type so the Style applies.
        if (!res.Contains("FieldLabel")) res.Add("FieldLabel", new Style(typeof(TextBlock)));
        var loc = new FakeLoc(); loc.SetCulture(culture); res["Loc"] = loc;
    }

    private sealed record Case(string Name, Func<FrameworkElement> Factory, CultureInfo Culture);

    /// <summary>
    /// Mirrors App.xaml's implicit view-model → view DataTemplates so CustomizeView
    /// actually renders the profile panel and the six knowledge tabs in tests.
    /// </summary>
    private static void InstallCustomizeDataTemplates(ResourceDictionary res)
    {
        void Ensure(Type vmType, Type viewType)
        {
            var key = new DataTemplateKey(vmType);
            if (res.Contains(key)) return;
            var template = new DataTemplate(vmType);
            template.VisualTree = new FrameworkElementFactory(viewType);
            res.Add(key, template);
        }

        Ensure(typeof(ProfileViewModel), typeof(ProfileView));
        Ensure(typeof(ComponentKnowledgeViewModel), typeof(ComponentKnowledgeView));
        Ensure(typeof(OptimizationKnowledgeViewModel), typeof(OptimizationKnowledgeView));
    }

    [Fact]
    public void AllCustomizeTabs_WithRealViewModels_HaveNoBindingErrors()
    {
        var c = BuildCustomizeGraph();
        var (wf, state) = WorkflowAndCommandTests.Build();
        var plan = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());

        var cases = new List<Case>();
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("zh-CN") })
        {
            cases.Add(new("PrivacyView", () => new PrivacyView { DataContext = new PrivacyViewModel(state, new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider()) }, culture));
            cases.Add(new("SystemView", () => new SystemView { DataContext = new SystemViewModel(state, new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider()) }, culture));
            cases.Add(new("ComponentsView", () => new ComponentsView { DataContext = c.Components }, culture));
            cases.Add(new("ComponentListTabView.Apps",
                () => new ComponentListTabView { DataContext = new ComponentListTabViewModel(c.Components, ComponentListKind.Apps, "Customize.Tab.Apps") }, culture));
            cases.Add(new("ComponentListTabView.Services",
                () => new ComponentListTabView { DataContext = new ComponentListTabViewModel(c.Components, ComponentListKind.Services, "Customize.Tab.Services") }, culture));
            cases.Add(new("PlanReviewView", () => new PlanReviewView { DataContext = plan }, culture));
            cases.Add(new("ComingSoonView", () => new ComingSoonView { DataContext = new ComingSoonViewModel() }, culture));
            // Stage 11.3 knowledge surfaces: Apps + Windows components share
            // ComponentKnowledgeView; Services/Privacy/System/Personalization share
            // OptimizationKnowledgeView. Render each with the REAL tab view model so
            // binding errors surface (Part T / Part L reuse).
            cases.Add(new("ComponentKnowledgeView.Apps",
                () => new ComponentKnowledgeView { DataContext = c.Tabs[0].Content }, culture));
            cases.Add(new("ComponentKnowledgeView.Components",
                () => new ComponentKnowledgeView { DataContext = c.Tabs[1].Content }, culture));
            cases.Add(new("OptimizationKnowledgeView.Services",
                () => new OptimizationKnowledgeView { DataContext = c.Tabs[2].Content }, culture));
            cases.Add(new("OptimizationKnowledgeView.Privacy",
                () => new OptimizationKnowledgeView { DataContext = c.Tabs[3].Content }, culture));
            cases.Add(new("OptimizationKnowledgeView.System",
                () => new OptimizationKnowledgeView { DataContext = c.Tabs[4].Content }, culture));
            cases.Add(new("OptimizationKnowledgeView.Personalization",
                () => new OptimizationKnowledgeView { DataContext = c.Tabs[5].Content }, culture));
            // Phase 12 — Settings/Storage disk-usage surface (Parts H/I/Q).
            // Phase 12.2 blocker regression: ScanAsync activates the scanned state so the
            // usage-summary TextBlock (previously a <Run> inline list that threw
            // 'System.Windows.Documents.Run.Text' on the real desktop) becomes VISIBLE during
            // the layout pass and its binding is forced to execute.
            cases.Add(new("SettingsView.Storage.Scanned",
                () =>
                {
                    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf12_render_" + Guid.NewGuid().ToString("N"));
                    var paths = new WorkspacePathProvider(root);
                    var runner = new FakeProcessRunner
                    {
                        Responder = req => new ProcessResult { ExitCode = 0, StandardOutput = "No mounted images found." },
                    };
                    var lifecycle = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), new InMemoryLoggerService());
                    var storage = new StorageViewModel(lifecycle, new FakeLoc());
                    storage.ScanAsync().GetAwaiter().GetResult();
                    return new StorageView { DataContext = storage };
                }, culture));
            cases.Add(new("SettingsView.Storage.RootErrorState",
                () =>
                {
                    var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf12_render_" + Guid.NewGuid().ToString("N"));
                    var paths = new WorkspacePathProvider(root);
                    var runner = new FakeProcessRunner
                    {
                        Responder = req => new ProcessResult { ExitCode = 0, StandardOutput = "No mounted images found." },
                    };
                    var lifecycle = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), new InMemoryLoggerService());
                    var settings = new WorkspaceRootSettingsService(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf12_roots_" + Guid.NewGuid().ToString("N") + ".json"));
                    var storage = new StorageViewModel(lifecycle, new FakeLoc(), settings);
                    storage.ScanAsync().GetAwaiter().GetResult();
                    // Drive-root candidate is rejected -> RootErrorText renders (red) inline.
                    storage.TrySetRoot(System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath()) ?? "C:\\");
                    return new StorageView { DataContext = storage };
                }, culture));
            // Stage 11.4 — profile selector panel (recommended configuration engine).
            cases.Add(new("ProfileView",
                () =>
                {
                    var ctx = new RecommendationContextService(
                        new RecommendationEngine(), new ProfileCatalog(), new AppState());
                    return new ProfileView
                    {
                        DataContext = new ProfileViewModel(
                            ctx, new FakeLoc(), () => Array.Empty<IRecommendationSubject>(), () => { }),
                    };
                }, culture));
            // Phase 10 Build page (Defect 2 audit): render BuildView with the
            // real BuildStepViewModel under both ADK-present and ADK-missing
            // states so the read-only binding audit catches any TwoWay/OneWayToSource
            // push onto a getter-only BuildStepViewModel property at runtime.
            cases.Add(new("BuildView.AdkPresent",
                () => new BuildView { DataContext = BuildBuildVm(true) }, culture));
            cases.Add(new("BuildView.AdkMissing",
                () => new BuildView { DataContext = BuildBuildVm(false) }, culture));
        }

        var failures = new List<string>();
        List<(string Name, Exception? Thrown, List<string> Errors)> results = new();
        RunWpf(() => { results = RunAllOnSingleSta(cases); });
        foreach (var (name, thrown, errors) in results)
        {
            foreach (var e in errors) _output.WriteLine($"[{name}] BINDING ERROR: {e}");
            if (thrown is not null) _output.WriteLine($"[{name}] THROWN: {thrown}");
            var relevant = errors.FindAll(e =>
                e.Contains("TwoWay") || e.Contains("OneWayToSource") || e.Contains("只读") ||
                e.Contains("read-only") || e.Contains("ReadOnly") || e.Contains("InvalidOperation") ||
                e.Contains("DispatcherUnhandledException"));
            if (thrown is not null || relevant.Count > 0)
            {
                failures.Add($"[{name}] thrown={(thrown?.Message ?? "none")}; errors={string.Join(" | ", errors)}");
            }
        }

        Assert.True(failures.Count == 0,
            "WPF binding errors detected on Customize tabs:\n" + string.Join("\n", failures));
    }

    private static List<(string Name, Exception? Thrown, List<string> Errors)> RunAllOnSingleSta(List<Case> cases)
    {
        // Runs on the shared STA dispatcher (invoked via RunWpf). The shared
        // Application is already created; we just attach the binding-error
        // listener and render every case on this one dispatcher.
        var results = new List<(string, Exception?, List<string>)>();
        Exception? capturedFatal = null;
        InstallAppResources(CultureInfo.GetCultureInfo("en"));
        var listener = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        var ds = PresentationTraceSources.DataBindingSource;
        ds.Switch.Level = SourceLevels.Error;
        ds.Listeners.Add(listener);

        var app = Application.Current!;
        app.Dispatcher.UnhandledExceptionFilter += (_, e) => { e.RequestCatch = true; };
        app.Dispatcher.UnhandledException += (_, e) =>
        {
            // Record but do not let it tear down the host.
            e.Handled = true;
        };

        var frame = new DispatcherFrame();
        using var watchdog = new System.Threading.Timer(_ => frame.Continue = false, null, 60000, Timeout.Infinite);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                foreach (var cs in cases)
                {
                    var errs = new List<string>();
                    Exception? thrown = null;
                    try
                    {
                        InstallAppResources(cs.Culture);
                        var view = cs.Factory();
                        view.Measure(new Size(900, 700));
                        view.Arrange(new Rect(0, 0, 900, 700));
                        view.UpdateLayout();
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }
                    finally
                    {
                        foreach (var l in listener.Lines) errs.Add(l);
                        listener.Lines.Clear();
                    }

                    results.Add((cs.Name + "[" + cs.Culture.Name + "]", thrown, errs));
                }
            }
            catch (Exception ex)
            {
                capturedFatal = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        ds.Listeners.Remove(listener);

        if (capturedFatal is not null)
        {
            results.Add(("FATAL", capturedFatal, new List<string>()));
        }

        return results;
    }

    private static CustomizeStepViewModel BuildCustomizeGraph()
    {
        var (wf, _) = WorkflowAndCommandTests.Build();
        return (CustomizeStepViewModel)wf.Steps[2].Content!;
    }

    /// <summary>Builds a real <see cref="BuildStepViewModel"/> for the WPF render
    /// tests using the headless Build fakes (no real ISO / mount / ADK).</summary>
    private static BuildStepViewModel BuildBuildVm(bool adkPresent)
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            SelectedEditionName = "Windows 11 Pro",
            WorkingDirectory = @"C:\ws",
            SourceIsoPath = @"C:\src.iso",
            SourceImageRelativePath = @"sources\install.wim",
            WorkingImagePath = @"C:\work\install.wim",
            MountDirectory = @"C:\work\mount",
            WorkingIndex = 1,
            State = ServicingWorkspaceState.Mounted
        };
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        IAdkToolLocator adk = adkPresent
            ? (IAdkToolLocator)new FakeAdkToolLocator()
            : new MissingAdkToolLocator();
        return new BuildStepViewModel(
            state, new FakeBuildService(), new RecordingFileSystem(), new FakeFilePicker(),
            adk, new InMemoryLoggerService(), new FakeLocalizationService());
    }

    /// <summary>
    /// Defect 2 (runtime): the Build progress bar binds <see cref="BuildStepViewModel.ProgressPercent"/>
    /// to ProgressBar.Value, which is two-way-by-default. With a getter-only source that throws
    /// "无法对只读属性进行 TwoWay 或 OneWayToSource 绑定". This proves the binding is explicitly
    /// OneWay (the fix), not TwoWay/OneWayToSource, by inspecting the resolved Binding after layout.
    /// </summary>
    [Fact]
    public void BuildView_ProgressPercent_Binding_Is_OneWay()
    {
        RunWpf(() =>
        {
            InstallAppResources(CultureInfo.GetCultureInfo("en"));
            var view = new BuildView { DataContext = BuildBuildVm(true) };
            view.Measure(new Size(900, 700));
            view.Arrange(new Rect(0, 0, 900, 700));
            view.UpdateLayout();

            var bar = (ProgressBar)view.FindName("BuildProgressBar")!;
            var binding = BindingOperations.GetBinding(bar, ProgressBar.ValueProperty);
            Assert.NotNull(binding);
            Assert.Equal(BindingMode.OneWay, binding!.Mode);
        });
    }

    /// <summary>
    /// Defect 1 (regression): the active Apps tab must show the discovered inventory
    /// IMMEDIATELY after discovery completes — without switching tabs or recreating the
    /// view. The ListView binds to the LIVE discovery collection (ListCollectionView over
    /// the shared ComponentsViewModel ObservableCollection), so post-discovery
    /// CollectionChanged flows straight into the visible list. Verified under en-US and
    /// zh-CN (identical behavior).
    /// </summary>
    [Fact]
    public void Discovery_ActiveAppsTab_RefreshesImmediately_WithoutTabRecreation()
    {
        RunWpf(() =>
        {
            foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("zh-CN") })
            {
                InstallAppResources(culture);
                var state = new AppState();
                state.CurrentServicingWorkspace = new ImageServicingWorkspace
                {
                    SelectedEditionName = "Windows 11 Pro",
                    State = ServicingWorkspaceState.Mounted
                };

                var discovery = new FakeCustomizationDiscoveryService();
                discovery.Inventory = new DiscoveryInventory
                {
                    Discovered = true,
                    AppxStatus = DiscoverySourceStatus.Success,
                    AppxPackages = new List<DiscoveredAppxPackage>
                    {
                        new DiscoveredAppxPackage
                        {
                            PackageName = "Microsoft.BingWeather",
                            DisplayName = "Bing Weather",
                            Present = true,
                            Risk = RiskClass.Safe
                        }
                    }
                };

                var components = new ComponentsViewModel(
                    state, new InMemoryLoggerService(), discovery, new FakeCustomizationDefinitionProvider());

                // Render the Apps tab ONCE and bind it to the live collection.
                var tabVm = new ComponentListTabViewModel(components, ComponentListKind.Apps, "Customize.Tab.Apps");
                var view = new ComponentListTabView { DataContext = tabVm };
                view.Measure(new Size(900, 700));
                view.Arrange(new Rect(0, 0, 900, 700));
                view.UpdateLayout();

                var listView = (ListView)view.FindName("ListView")!;
                var before = listView.Items.Count;

                // Complete discovery on the UI thread (the fake yields nothing).
#pragma warning disable xUnit1031 // intentional: block the STA worker thread, not the test thread
                components.DiscoverAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

                // Force layout so the live collection is reflected in the visual tree.
                view.UpdateLayout();
                var after = listView.Items.Count;

                Assert.True(before == 0, $"[{culture.Name}] expected empty list before discovery, got {before}");
                Assert.True(after == discovery.Inventory.AppxPackages.Count,
                    $"[{culture.Name}] expected {discovery.Inventory.AppxPackages.Count} item(s) after discovery, got {after}");
                Assert.True(discovery.DiscoverCalls == 1, $"[{culture.Name}] discovery should run exactly once");
                Assert.True(components.HasInventory, $"[{culture.Name}] HasInventory must be true after discovery");
            }
        });
    }

    // ---- Static XAML audit: every display-only binding to a getter-only
    //      ViewModel property must declare Mode=OneWay explicitly. A getter-only
    //      source can NEVER be the target of a TwoWay / OneWayToSource push, and
    //      against a two-way-by-default target (Run.Text) WPF throws
    //      "无法对只读属性进行 TwoWay 或 OneWayToSource 绑定". This audit locks the
    //      fix in without needing to render the visual tree. ----

    private static readonly HashSet<string> GetterOnlyProps = new(StringComparer.Ordinal)
    {
            "SelectedTotal", "TotalSelected", "TotalApps", "TotalPackages",
        "TotalRegistry", "TotalServices", "IsDiscovering", "HasInventory",
        "StatusMessage", "HasWarnings", "ProgressText", "ResultSummary",
        "IsMounted", "CanDiscover", "CanValidate", "CanApply",
        "ExecutionState", "ShowProtectedVisible", "Items", "Plan", "DiscoverCommand",
        // Stage 11.3 OptimizationKnowledgeViewModel display-only getter-only properties.
        "IsEmpty", "EmptyStateText", "FilterOptions", "ItemCount",
        // Stage 11.4 ProfileViewModel display-only getter-only properties. NOTE:
        // generic model tokens ("DisplayName", "Description", "Items", "HeaderKey")
        // are intentionally NOT listed — they collide with settable model
        // properties on other pages (e.g. Setting.Description) and would produce
        // false positives in this audit.
        "ActiveProfileCaption", "HasActiveProfiles", "CanToggleExtras", "HasExtraScenarios",
        "HasUnsupported", "TrimCount", "ManualCount",
        "KeepCount", "ConflictCount", "UnsupportedCount", "HasConflicts", "HasPreviewItems",
        "SummaryAdoptLabel", "SummaryConfirmLabel", "SummaryKeepLabel", "SummaryConflictLabel",
        "SummaryUnsupportedLabel", "RestoreVisible", "IsPreviewOpen", "Profiles", "ExtraScenarios",
        "PreviewGroups", "ShowPreviewCommand", "ClosePreviewCommand", "RestoreCommand",
        "Items",
        // Phase 10 BuildStepViewModel display-only getter-only properties (Defect 2 audit).
        "ProgressPercent", "CurrentStageText", "BuildModeText", "OutputPath",
        "OutputSizeText", "IsIndeterminate", "HasOutput", "AdkMissing",
        "SourceEdition", "LogText"
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate WinForge.sln (repo root).");
        }

        return dir.FullName;
    }

    /// <summary>
    /// Stage 11.4 final layout (approved): two-panel profile area — PRIMARY on the
    /// LEFT (~75%), EXTRA scenarios on the RIGHT (~25%) — side by side, all 7
    /// primary radio cards + all 5 extra checkboxes visible at once, compact total
    /// height, no horizontal scrollbar.
    /// </summary>
    [Fact]
    public void ProfilePanel_TwoColumn_Layout_Is_Compact()
    {
        RunWpf(() =>
        {
            InstallAppResources(CultureInfo.GetCultureInfo("en"));

            var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), new AppState());
            var profileVm = new ProfileViewModel(ctx, new FakeLoc(), () => Array.Empty<IRecommendationSubject>(), () => { });
            var profileView = new ProfileView { DataContext = profileVm };
            profileView.Measure(new Size(1200, 700));
            profileView.Arrange(new Rect(0, 0, 1200, 700));
            profileView.UpdateLayout();

            // Compact total footprint (two-panel + one summary row).
            Console.WriteLine($"[layout] ProfileView total height = {profileView.DesiredSize.Height}px");
            Assert.True(profileView.DesiredSize.Height <= 260,
                $"ProfileView is {profileView.DesiredSize.Height}px tall — must stay compact.");

            // 7 primary profiles as RADIO cards (mutually exclusive semantics).
            var radios = FindVisuals<RadioButton>(profileView)
                .Where(r => r.GroupName == "PrimaryProfile").ToList();
            Assert.Equal(7, radios.Count);
            radios[1].IsChecked = true;
            Assert.True(radios[1].IsChecked == true);
            Assert.False(radios[0].IsChecked == true);

            // All 5 extra scenarios visible at once (independent checkboxes).
            var checks = FindVisuals<CheckBox>(profileView).ToList();
            Assert.Equal(5, checks.Count);

            // Two columns side by side; primary column wider than extras column.
            var twoCol = FindVisuals<Grid>(profileView)
                .FirstOrDefault(g => g.ColumnDefinitions.Count == 2);
            Assert.NotNull(twoCol);
            Console.WriteLine($"[layout] primary col = {twoCol!.ColumnDefinitions[0].ActualWidth}px, extras col = {twoCol.ColumnDefinitions[1].ActualWidth}px");
            Assert.True(twoCol.ColumnDefinitions[0].ActualWidth > twoCol.ColumnDefinitions[1].ActualWidth,
                $"Primary col {twoCol.ColumnDefinitions[0].ActualWidth}px must exceed extras col {twoCol.ColumnDefinitions[1].ActualWidth}px.");
            Assert.True(twoCol.ColumnDefinitions[1].ActualWidth >= 200,
                $"Extras column {twoCol.ColumnDefinitions[1].ActualWidth}px too narrow.");

            // No horizontal scrollbar inside the profile panel.
            var scrolls = FindVisuals<ScrollViewer>(profileView)
                .Where(sv => sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled).ToList();
            Assert.True(scrolls.Count == 0,
                "Profile panel must not introduce horizontal scrollbars.");
        });
    }

    /// <summary>
    /// Stage 11.4 Part A: title + subtitle + scan/selected share ONE compact
    /// header block (target ~35-45% smaller than the previous layout).
    /// </summary>
    [Fact]
    public void Customize_Header_Is_Compact()
    {
        RunWpf(() =>
        {
            InstallAppResources(CultureInfo.GetCultureInfo("en"));

            var customize = BuildCustomizeWithProfiles();
            var view = new CustomizeView { DataContext = customize };
            view.Measure(new Size(1200, 700));
            view.Arrange(new Rect(0, 0, 1200, 700));
            view.UpdateLayout();

            var root = Assert.IsType<Grid>(view.Content);
            Assert.True(root.RowDefinitions.Count == 5, "Customize root must be a 5-row Grid (Auto header rows + star TabControl row).");
            // Rows 0..2 = title + subtitle + scan/selected. Keep the whole block compact.
            var headerHeight = root.RowDefinitions[0].ActualHeight
                               + root.RowDefinitions[1].ActualHeight
                               + root.RowDefinitions[2].ActualHeight;
            Console.WriteLine($"[layout] header rows = {root.RowDefinitions[0].ActualHeight:F0}+{root.RowDefinitions[1].ActualHeight:F0}+{root.RowDefinitions[2].ActualHeight:F0} = {headerHeight:F0}px");
            Assert.True(headerHeight <= 130,
                $"Header block is {headerHeight}px tall — must be compact (Part A).");
            // The TabControl row must be the star row (consumes remaining height).
            var star = root.RowDefinitions[4];
            Assert.True(star.Height == new GridLength(1, GridUnitType.Star),
                "TabControl row must be star-sized so the list gets the majority of the page.");
        });
    }

    /// <summary>
    /// Stage 11.4 Part F: the TabControl/list gets star height and shows several
    /// component rows at a normal 1200x700 viewport (acceptance: >= 6 rows).
    /// </summary>
    [Fact]
    public void Customize_List_Shows_Multiple_Rows_At_Normal_Size()
    {
        RunWpf(() =>
        {
            InstallAppResources(CultureInfo.GetCultureInfo("en"));

            var customize = BuildCustomizeWithRows();
            customize.SelectedTab = customize.Tabs[0]; // render the Apps knowledge tab
            var view = new CustomizeView { DataContext = customize };
            view.Measure(new Size(1200, 700));
            view.Arrange(new Rect(0, 0, 1200, 700));
            view.UpdateLayout();

            var tabs = FindVisuals<TabControl>(view).FirstOrDefault();
            Assert.NotNull(tabs);
            Assert.True(tabs.ActualHeight >= 250,
                $"TabControl height {tabs.ActualHeight}px too small — the component list is squeezed.");

            var list = FindVisuals<ListView>(view).FirstOrDefault();
            Assert.NotNull(list);
            Console.WriteLine($"[layout] TabControl = {tabs.ActualHeight:F0}px, ListView viewport = {list.ActualHeight:F0}px (~{list.ActualHeight / 38.0:F1} rows of 38px)");
            // Knowledge row ~38px tall -> >= 6 rows needs >= ~230px of list viewport.
            Assert.True(list.ActualHeight >= 225,
                $"ListView height {list.ActualHeight}px — need >= ~225px for >= 6 visible rows.");
            Assert.True(list.ActualHeight >= 225 / 38.0 * 6 * 0.99,
                $"ListView {list.ActualHeight}px does not show ~6 rows (38px each).");
        });
    }

    /// <summary>Profile-wired Customize graph with populated AppX rows (row-count test).</summary>
    private static CustomizeStepViewModel BuildCustomizeWithRows()
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C://wf//mount",
            },
        };
        var logger = new InMemoryLoggerService();
        var loc = new FakeLoc();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var ciService = new StaticCiService
        {
            Inventory = new ComponentInventory
            {
                Discovered = true,
                Categories = new[]
                {
                    new CategoryDiscoveryResult
                    {
                        Category = ComponentCategory.AppX,
                        Status = InventoryStatus.Success,
                        Items = new[] { "Microsoft.XboxApp", "Microsoft.BingWeather", "Microsoft.BingNews",
                                "Microsoft.BingSearch", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.WindowsMaps",
                                "Microsoft.YourPhone", "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp",
                                "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal" }
                            .Select(id => new RawAppxPackage
                            {
                                Category = ComponentCategory.AppX,
                                RawIdentity = id,
                                DisplayName = id,
                            } as IRawInventoryItem)
                            .ToList(),
                    },
                },
            },
        };
        var ciVm = new ComponentIntelligenceViewModel(state, logger, ciService,
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc, null, ctx);
        var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability }, ctx);
        var catalog = new OptimizationCatalog();
        var customize = new CustomizeStepViewModel(
            new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider()),
            knowledge,
            componentsKnowledge,
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Services, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Privacy, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.System, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Personalization, ctx),
            ctx,
            loc);
        knowledge.DiscoverAsync().GetAwaiter().GetResult();
        return customize;
    }

    private sealed class StaticCiService : IComponentIntelligenceService
    {
        public ComponentInventory Inventory { get; set; } = new();

        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(Inventory);
    }

    /// <summary>Profile-wired Customize graph (Stage 11.4 UX layout test).</summary>
    private static CustomizeStepViewModel BuildCustomizeWithProfiles()
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C://wf//mount",
            },
        };
        var logger = new InMemoryLoggerService();
        var loc = new FakeLoc();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var ciVm = new ComponentIntelligenceViewModel(state, logger, new NoopCiService(),
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc, null, ctx);
        var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability }, ctx);
        var catalog = new OptimizationCatalog();
        return new CustomizeStepViewModel(
            new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider()),
            knowledge,
            componentsKnowledge,
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Services, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Privacy, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.System, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Personalization, ctx),
            ctx,
            loc);
    }

    private sealed class NoopCiService : IComponentIntelligenceService
    {
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComponentInventory());
    }

    private static List<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(FindVisuals<T>(child));
        }

        return found;
    }

    [Fact]
    public void All_DisplayOnly_GetterOnly_Bindings_Use_ModeOneWay()
    {
        var viewsDir = Path.Combine(RepoRoot(), "src", "WinForge.App", "Views");
        var files = new[]
        {
            "CustomizeView.xaml", "ComponentsView.xaml", "PrivacyView.xaml",
            "SystemView.xaml", "PlanReviewView.xaml", "ComponentListTabView.xaml",
            "ComingSoonView.xaml", "BuildView.xaml", "OptimizationKnowledgeView.xaml",
            "ProfileView.xaml"
        };

        // Matches {Binding <body>} capturing the body (path + optional Mode/Converter).
        var bindingPattern = new Regex(@"\{Binding\s+([^}]+?)\}", RegexOptions.IgnoreCase);
        var modePattern = new Regex(@"Mode\s*=\s*(OneWay|TwoWay|OneWayToSource|Default)\b", RegexOptions.IgnoreCase);
        var failures = new List<string>();

        foreach (var file in files)
        {
            var path = Path.Combine(viewsDir, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match m in bindingPattern.Matches(text))
            {
                var body = m.Groups[1].Value.Trim();
                // The bound property is the first token (path), before any comma.
                var pathToken = body.Split(',')[0].Trim();
                var lastSegment = pathToken.Contains('.')
                    ? pathToken.Substring(pathToken.LastIndexOf('.') + 1)
                    : pathToken;

                if (!GetterOnlyProps.Contains(lastSegment))
                {
                    continue;
                }

                var modeMatch = modePattern.Match(body);
                var mode = modeMatch.Success ? modeMatch.Groups[1].Value : "Default";
                if (!string.Equals(mode, "OneWay", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{file}: {m.Value}  -> getter-only '{lastSegment}' is bound as Mode={mode}; must be OneWay");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Display-only bindings to getter-only properties must be Mode=OneWay:\n" + string.Join("\n", failures));
    }
}

/// <summary>
/// WPF/STA binding tests must NOT run in parallel: the harness spins up an STA
/// thread with a fresh <see cref="Application"/> + DispatcherFrame, and concurrent
/// Application/Dispatcher creation across many STA threads deadlocks or is
/// pathologically slow. Serializing this collection avoids that.
/// </summary>
[CollectionDefinition("WpfSta", DisableParallelization = true)]
public sealed class WpfStaCollection
{
}
