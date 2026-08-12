using System.Globalization;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Settings page. Today it exposes the display-language control: switching the
/// language is applied immediately through <see cref="ILocalizationService"/> (no
/// restart) and persisted via <see cref="ILanguageSettingsStore"/>. Missing
/// translations fall back to English, never to a blank string.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly ILanguageSettingsStore _store;

    public SettingsViewModel(
        ILocalizationService localization,
        ILanguageSettingsStore store,
        StorageViewModel storage)
    {
        _localization = localization;
        _store = store;
        Storage = storage ?? throw new System.ArgumentNullException(nameof(storage));

        _localization.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentCultureName));
            OnPropertyChanged(nameof(IsEnglish));
            OnPropertyChanged(nameof(IsChinese));
        };

        SetLanguageCommand = new RelayCommand(p => SetLanguage((string)p!));
    }

    public string CurrentCultureName => _localization.CurrentCulture.Name;

    public bool IsEnglish => Equals(_localization.CurrentCulture.Name, "en");

    public bool IsChinese => Equals(_localization.CurrentCulture.Name, "zh-CN");

    public ICommand SetLanguageCommand { get; }

    /// <summary>Phase 12 disk-usage surface (Parts H/I).</summary>
    public StorageViewModel Storage { get; }

    private void SetLanguage(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        _localization.SetCulture(culture);
        _store.SaveCulture(culture.Name);
    }
}
