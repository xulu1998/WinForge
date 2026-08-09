namespace WinForge.Core.Services;

/// <summary>
/// Persists the user's chosen UI language between sessions. The stored value is a
/// culture name such as "en" or "zh-CN"; the loader is responsible for validating it.
/// </summary>
public interface ILanguageSettingsStore
{
    /// <summary>Returns the persisted culture name, or null when nothing was saved.</summary>
    string? LoadCulture();

    /// <summary>Persists the culture name. Implementations must never throw on failure.</summary>
    void SaveCulture(string cultureName);
}
