using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WinForge.App.Converters;
using WinForge.App.Views;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// WPF/XAML smoke tests. The unit suite does not render the real visual tree, so a
/// <c>Run.Text</c> localization binding defect that only throws when the Customize
/// visual tree is instantiated was never caught. These tests load the actual
/// Customize-related views on an STA thread with the same resources the app installs
/// at startup (the <c>locKey</c> converter, <c>BoolToVis</c>, the <c>PrimaryButton</c>
/// style, and the <c>Loc</c> localization service) and assert they parse/load without
/// a <see cref="System.Windows.Markup.XamlParseException"/>.
///
/// <para>
/// Root cause of the defect: <c>Run.Text</c> is a two-way-by-default dependency
/// property. Inside a <c>&lt;Run.Text&gt;</c> MultiBinding, the child
/// <c>&lt;Binding Source="..."/&gt;</c> elements inherit <c>Mode=TwoWay</c> and have no
/// Path, so WPF refuses to attach them ("双向绑定需要 Path 或 XPath"). The fix sets
/// <c>Mode="OneWay"</c> on those child bindings. <c>TextBlock.Text</c> is one-way-by
/// default, which is why the identical MultiBinding worked there but not on
/// <c>Run.Text</c>.
/// </para>
/// </summary>
[Collection("WpfSta")]
public class CustomizeXamlLoadRegressionTests
{
    // Fake localization service standing in for ResourceManagerLocalizationService.
    // The converter only requires `values[1] is ILocalizationService`, so this is enough.
    private sealed class FakeLoc : ILocalizationService
    {
        private readonly object _gate = new();
        private EventHandler? _cultureChanged;
        private PropertyChangedEventHandler? _propertyChanged;

        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");

        public event EventHandler? CultureChanged
        {
            add { lock (_gate) { _cultureChanged += value; } }
            remove { lock (_gate) { _cultureChanged -= value; } }
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { lock (_gate) { _propertyChanged += value; } }
            remove { lock (_gate) { _propertyChanged -= value; } }
        }

        public string this[string key] => string.IsNullOrEmpty(key) ? (key ?? string.Empty) : ("[loc:" + key + "]");
        public bool Contains(string key) => true;
        public void SetCulture(CultureInfo culture) => CurrentCulture = culture;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new Exception("STA load failed — see inner exception for the full WPF chain.", captured);
        }
    }

    private static void InstallAppResources(CultureInfo culture)
    {
        if (Application.Current is null)
        {
            new Application();
        }

        var app = Application.Current!;
        var res = app.Resources;
        if (!res.Contains("locKey"))
        {
            res.Add("locKey", new LocKeyMultiConverter());
        }
        if (!res.Contains("BoolToVis"))
        {
            res.Add("BoolToVis", new BooleanToVisibilityConverter());
        }
        if (!res.Contains("BoolToVisInv"))
        {
            res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
        }
        if (!res.Contains("NullToVis"))
        {
            res.Add("NullToVis", new NullToVisibilityConverter());
        }
        if (!res.Contains("StatusTile"))
        {
            res.Add("StatusTile", new Style(typeof(Border)));
        }
        if (!res.Contains("PrimaryButton"))
        {
            res.Add("PrimaryButton", new Style(typeof(Button)));
        }

        var loc = new FakeLoc();
        loc.SetCulture(culture);
        res["Loc"] = loc;
    }

    private static void LoadViewOnSta<T>(CultureInfo culture) where T : FrameworkElement, new()
    {
        RunSta(() =>
        {
            InstallAppResources(culture);
            var view = new T();
            // Force a layout pass so bindings/inlines actually connect and any
            // TwoWay/Path defect surfaces during attach (not just at parse time).
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
        });
    }

    [Fact]
    public void CustomizeView_Loads_WithoutXamlParseException()
        => LoadViewOnSta<CustomizeView>(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void SystemView_Loads_WithoutXamlParseException()
        => LoadViewOnSta<SystemView>(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void PrivacyView_Loads_WithoutXamlParseException()
        => LoadViewOnSta<PrivacyView>(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void ComponentsView_Loads_WithoutXamlParseException()
        => LoadViewOnSta<ComponentsView>(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void PlanReviewView_Loads_WithoutXamlParseException()
        => LoadViewOnSta<PlanReviewView>(CultureInfo.GetCultureInfo("en"));

    // ---- Culture switch must not re-introduce the Run.Text failure ----

    [Fact]
    public void CustomizeView_Loads_UnderZhCnCulture()
        => LoadViewOnSta<CustomizeView>(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void SystemView_Loads_UnderZhCnCulture()
        => LoadViewOnSta<SystemView>(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void PrivacyView_Loads_UnderZhCnCulture()
        => LoadViewOnSta<PrivacyView>(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void ComponentsView_Loads_UnderZhCnCulture()
        => LoadViewOnSta<ComponentsView>(CultureInfo.GetCultureInfo("zh-CN"));

    // ---- End-to-end: the real navigation sequence reaches Customize AND the
    //      Customize view (with its Run.Text MultiBindings) loads cleanly. ----

    [Fact]
    public void Source_Prepare_Mounted_Next_Customize_ThenViewLoads()
    {
        // Drive the exact real-desktop sequence with one persistent WorkflowViewModel.
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws;
        Assert.False(wf.CanGoNext);

        ws.State = ServicingWorkspaceState.Mounted; // in-place mutation (the real trigger)
        Assert.True(wf.CanGoNext);
        wf.GoNext();
        Assert.Equal(WorkflowStep.Customize, wf.CurrentStep!.Step);

        // Now instantiate the real Customize visual tree — this is where the
        // Run.Text XamlParseException previously fired.
        LoadViewOnSta<CustomizeView>(CultureInfo.GetCultureInfo("en"));
    }
}
