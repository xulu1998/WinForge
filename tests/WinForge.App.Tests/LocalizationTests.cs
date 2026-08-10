using System.Collections;
using System.Globalization;
using System.Resources;
using WinForge.App.Localization;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// LOCALIZATION coverage: English resolution, graceful fallback, runtime culture
/// switching, persistence, and zh-CN parity (the satellite assembly is deployed
/// alongside the tests, so Chinese values resolve at runtime).
/// </summary>
public class LocalizationTests
{
    private static ResourceManagerLocalizationService EnglishService()
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(HomeViewModel).Assembly);
        return new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
    }

    [Fact]
    public void Localization_English_Resolves_Known_Key()
    {
        var svc = EnglishService();
        Assert.Equal("WinForge", svc["Home.Title"]);
        Assert.Equal("Workflow", svc["Nav.Workflow"]);
    }

    [Fact]
    public void Localization_Missing_Key_Returns_Key()
    {
        var svc = EnglishService();
        Assert.Equal("No.Such.Key", svc["No.Such.Key"]);
    }

    [Fact]
    public void Localization_Contains_Distinguishes_Known_And_Unknown()
    {
        var svc = EnglishService();
        Assert.True(svc.Contains("Home.Title"));
        Assert.False(svc.Contains("No.Such.Key"));
    }

    [Fact]
    public void Localization_SetCulture_Fires_CultureChanged_And_PropertyChanged()
    {
        var svc = EnglishService();
        var cultureFired = false;
        var propFired = false;
        svc.CultureChanged += (_, _) => cultureFired = true;
        svc.PropertyChanged += (_, e) => { if (e?.PropertyName == "Item[]") propFired = true; };

        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));

        Assert.True(cultureFired);
        Assert.True(propFired);
    }

    [Fact]
    public void Localization_SetCulture_Same_Culture_Is_NoOp()
    {
        var svc = EnglishService();
        var count = 0;
        svc.CultureChanged += (_, _) => count++;
        svc.SetCulture(CultureInfo.GetCultureInfo("en"));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Localization_Unknown_Culture_Falls_Back_To_English()
    {
        var svc = EnglishService();
        svc.SetCulture(CultureInfo.GetCultureInfo("fr")); // no fr satellite
        Assert.Equal("WinForge", svc["Home.Title"]);
        Assert.Equal("Workflow", svc["Nav.Workflow"]);
    }

    [Fact]
    public void Localization_Runtime_Switch_To_ZhCn_Returns_Chinese()
    {
        var svc = EnglishService();
        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("按你的方式构建 Windows。", svc["Home.Subtitle"]);
        Assert.Equal("工作流", svc["Nav.Workflow"]);
    }

    [Fact]
    public void Localization_Switching_Back_To_English_Returns_English()
    {
        var svc = EnglishService();
        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        svc.SetCulture(CultureInfo.GetCultureInfo("en"));
        Assert.Equal("Build Windows your way.", svc["Home.Subtitle"]);
    }

    [Fact]
    public void Localization_ZhCn_Satellite_Contains_All_En_Keys_With_Values()
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(HomeViewModel).Assembly);
        var enSet = rm.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!;
        var zh = CultureInfo.GetCultureInfo("zh-CN");

        foreach (DictionaryEntry entry in enSet)
        {
            var key = (string)entry.Key;
            var zhValue = rm.GetString(key, zh);
            Assert.False(string.IsNullOrEmpty(zhValue), $"zh-CN missing or empty for key '{key}'");
        }
    }

    [Fact]
    public void Localization_Persistence_RoundTrips_Through_Store()
    {
        var store = new InMemoryLanguageSettingsStore();
        Assert.Null(store.LoadCulture());
        store.SaveCulture("zh-CN");
        Assert.Equal("zh-CN", store.LoadCulture());
    }

    [Fact]
    public void Localization_Bootstrap_Initialize_Uses_Saved_Culture()
    {
        var store = new InMemoryLanguageSettingsStore();
        store.SaveCulture("zh-CN");
        var svc = EnglishService();
        LocalizationBootstrap.Initialize(svc, store);
        Assert.Equal("zh-CN", svc.CurrentCulture.Name);
    }

    [Fact]
    public void Localization_Bootstrap_Initialize_Invalid_Saved_Resolves_To_A_Supported_Culture()
    {
        // An empty/whitespace saved value is not a usable culture, so Initialize
        // must not keep a bogus value — it resolves to a shipped language (en, or
        // zh-CN when the OS UI culture is Chinese).
        var store = new InMemoryLanguageSettingsStore();
        store.SaveCulture(string.Empty);
        var svc = EnglishService();
        LocalizationBootstrap.Initialize(svc, store);
        Assert.Contains(svc.CurrentCulture.Name, new[] { "en", "zh-CN" });
    }
}
