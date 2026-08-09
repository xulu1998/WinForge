using WinForge.Core.Services;

namespace WinForge.App.Localization;

/// <summary>
/// In-memory <see cref="ILanguageSettingsStore"/> used by unit tests (and any
/// sandboxed run) where writing to disk is undesirable. Records the last saved
/// culture so tests can assert persistence without a file system.
/// </summary>
public sealed class InMemoryLanguageSettingsStore : ILanguageSettingsStore
{
    public string? SavedCulture { get; private set; }

    public string? LoadCulture() => SavedCulture;

    public void SaveCulture(string cultureName) => SavedCulture = cultureName;
}
