using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using WinForge.Core.Services;

namespace WinForge.App.Localization;

/// <summary>
/// <see cref="ResourceManager"/>-backed <see cref="ILocalizationService"/>. Strings
/// are read from compiled <c>.resx</c> resources (invariant + culture satellites).
/// Resolution order for a key: active culture → default (English) culture → the key
/// itself, so a missing translation degrades gracefully instead of blanking text.
/// </summary>
public sealed class ResourceManagerLocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager;
    private readonly CultureInfo _defaultCulture;
    private CultureInfo _currentCulture;

    public ResourceManagerLocalizationService(ResourceManager resourceManager, CultureInfo defaultCulture)
    {
        _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
        _defaultCulture = defaultCulture ?? CultureInfo.GetCultureInfo("en");
        _currentCulture = _defaultCulture;
    }

    public CultureInfo CurrentCulture => _currentCulture;

    public event EventHandler? CultureChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
            {
                return key ?? string.Empty;
            }

            var value = _resourceManager.GetString(key, _currentCulture);
            if (value is null && !Equals(_currentCulture, _defaultCulture))
            {
                value = _resourceManager.GetString(key, _defaultCulture);
            }

            return value ?? key;
        }
    }

    public void SetCulture(CultureInfo culture)
    {
        if (culture is null)
        {
            throw new ArgumentNullException(nameof(culture));
        }

        if (Equals(culture, _currentCulture))
        {
            return;
        }

        _currentCulture = culture;

        // WPF indexer bindings (Path=[Key]) refresh when the indexer property
        // raises change. Notify "Item[]" so every localized string re-evaluates.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Contains(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        return _resourceManager.GetString(key, _currentCulture) is not null
            || _resourceManager.GetString(key, _defaultCulture) is not null;
    }
}
