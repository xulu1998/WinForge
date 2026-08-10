using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WinForge.App.Converters;
using WinForge.App.Localization;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// WPF/XAML smoke tests for <see cref="ComponentIntelligenceView"/>. The unit suite
/// does not render the real visual tree, so a binding / resource defect that only
/// throws when the visual tree is instantiated would never be caught. These load the
/// actual view on an STA thread with the same resources the app installs at startup
/// (the <c>locKey</c> converter, <c>BoolToVis</c>, the <c>PrimaryButton</c> style, the
/// <c>Loc</c> service, and the two new color converters <c>recColor</c> /
/// <c>riskColor</c>) and assert it parses/lays out without a
/// <see cref="System.Windows.Markup.XamlParseException"/>.
///
/// <para>A third test goes further and sets a real DataContext so the ListView item
/// template (with its <c>recColor</c>/<c>riskColor</c> foreground bindings) and the
/// detail panel (Runs, ItemsControls, Expander header) actually attach and render —
/// exercising every binding the prototype uses.</para>
/// </summary>
[Collection("WpfSta")]
public class ComponentIntelligenceXamlLoadRegressionTests
{
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

    private sealed class NoopService : IComponentIntelligenceService
    {
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComponentInventory());
    }

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
            throw new Exception("STA load failed — see inner exception for the full WPF chain.", captured);
        }
    }

    private static void InstallAppResources(ILocalizationService loc)
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
        // The two new color converters referenced by ComponentIntelligenceView.
        if (!res.Contains("recColor")) res.Add("recColor", new RecommendationToColorConverter());
        if (!res.Contains("riskColor")) res.Add("riskColor", new RiskToColorConverter());

        res["Loc"] = loc;
    }

    private static ResourceManagerLocalizationService RealLocalizer(CultureInfo culture)
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(ComponentIntelligenceView).Assembly);
        return new ResourceManagerLocalizationService(rm, culture);
    }

    private static void LoadViewOnSta(CultureInfo culture, ComponentIntelligenceViewModel? dataContext = null)
    {
        RunSta(() =>
        {
            var loc = dataContext is null ? new FakeLoc() : (ILocalizationService)RealLocalizer(culture);
            InstallAppResources(loc);

            var view = dataContext is null
                ? new ComponentIntelligenceView()
                : new ComponentIntelligenceView { DataContext = dataContext };

            // Force a layout pass so bindings/inlines actually connect and any
            // resource / TwoWay / Path defect surfaces during attach.
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
        });
    }

    [Fact]
    public void ComponentIntelligenceView_Loads_WithoutXamlParseException()
        => LoadViewOnSta(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void ComponentIntelligenceView_Loads_UnderZhCnCulture()
        => LoadViewOnSta(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void ComponentIntelligenceView_Loads_WithRealDataContext_ExercisesItemAndDetailTemplates()
    {
        // Build a real ViewModel (seeded from the curated catalog) so the ListView
        // item template and the detail panel both bind to genuine ComponentListItem
        // data — this is what would surface a converter / binding failure at runtime.
        var loc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
        var vm = new ComponentIntelligenceViewModel(
            new AppState(), new InMemoryLoggerService(), new NoopService(), new CuratedComponentCatalog(), loc);

        LoadViewOnSta(CultureInfo.GetCultureInfo("en"), vm);
    }
}
