using System.Globalization;
using WinForge.Core.Services;

namespace WinForge.App.Localization;

/// <summary>
/// Applies the persisted (or OS-default, falling back to English) language at
/// startup. Resolution order:
/// <list type="number">
///   <item><description>The persisted culture, if it parses to a known culture.</description></item>
///   <item><description>The OS UI culture, but only if we actually ship that language (currently en / zh-CN); otherwise English.</description></item>
///   <item><description>English as the guaranteed fallback.</description></item>
/// </list>
/// </summary>
public static class LocalizationBootstrap
{
    public static void Initialize(ILocalizationService localization, ILanguageSettingsStore store)
    {
        var culture = Resolve(store.LoadCulture());
        localization.SetCulture(culture);
    }

    internal static CultureInfo Resolve(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved))
        {
            try
            {
                return CultureInfo.GetCultureInfo(saved!);
            }
            catch
            {
                // fall through to OS / English
            }
        }

        var os = CultureInfo.CurrentUICulture;
        if (Equals(os.Name, "zh-CN") || Equals(os.TwoLetterISOLanguageName, "zh"))
        {
            return CultureInfo.GetCultureInfo("zh-CN");
        }

        return CultureInfo.GetCultureInfo("en");
    }
}
