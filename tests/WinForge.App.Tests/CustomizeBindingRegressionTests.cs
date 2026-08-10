using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinForge.App.Converters;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
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

    private static void InstallAppResources(CultureInfo culture)
    {
        if (Application.Current is null) new Application();
        var res = Application.Current!.Resources;
        if (!res.Contains("locKey")) res.Add("locKey", new LocKeyMultiConverter());
        if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new BooleanToVisibilityConverter());
        if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
        if (!res.Contains("NullToVis")) res.Add("NullToVis", new NullToVisibilityConverter());
        if (!res.Contains("StatusTile")) res.Add("StatusTile", new Style(typeof(Border)));
        if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
        var loc = new FakeLoc(); loc.SetCulture(culture); res["Loc"] = loc;
    }

    private sealed record Case(string Name, Func<FrameworkElement> Factory, CultureInfo Culture);

    [Fact]
    public void AllCustomizeTabs_WithRealViewModels_HaveNoBindingErrors()
    {
        var c = BuildCustomizeGraph();
        var (wf, state) = WorkflowAndCommandTests.Build();
        var plan = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());

        var cases = new List<Case>();
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("zh-CN") })
        {
            cases.Add(new("PrivacyView", () => new PrivacyView { DataContext = c.Privacy }, culture));
            cases.Add(new("SystemView", () => new SystemView { DataContext = c.System }, culture));
            cases.Add(new("ComponentsView", () => new ComponentsView { DataContext = c.Components }, culture));
            cases.Add(new("ComponentListTabView.Apps",
                () => new ComponentListTabView { DataContext = new ComponentListTabViewModel(c.Components, ComponentListKind.Apps, "Customize.Tab.Apps") }, culture));
            cases.Add(new("ComponentListTabView.Services",
                () => new ComponentListTabView { DataContext = new ComponentListTabViewModel(c.Components, ComponentListKind.Services, "Customize.Tab.Services") }, culture));
            cases.Add(new("PlanReviewView", () => new PlanReviewView { DataContext = plan }, culture));
            cases.Add(new("ComingSoonView", () => new ComingSoonView { DataContext = c.Experience }, culture));
        }

        var results = RunAllOnSingleSta(cases);
        var failures = new List<string>();
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
        var results = new List<(string, Exception?, List<string>)>();
        Exception? capturedFatal = null;
        var thread = new Thread(() =>
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                capturedFatal = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

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
        "ExecutionState", "ShowProtectedVisible", "Items", "Plan", "DiscoverCommand"
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

    [Fact]
    public void All_DisplayOnly_GetterOnly_Bindings_Use_ModeOneWay()
    {
        var viewsDir = Path.Combine(RepoRoot(), "src", "WinForge.App", "Views");
        var files = new[]
        {
            "CustomizeView.xaml", "ComponentsView.xaml", "PrivacyView.xaml",
            "SystemView.xaml", "PlanReviewView.xaml", "ComponentListTabView.xaml",
            "ComingSoonView.xaml"
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
