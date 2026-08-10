using System;
using System.ComponentModel;
using System.Globalization;

namespace WinForge.Core.Services;

/// <summary>
/// Resolves localized UI strings by key for the active UI culture. Implementations
/// raise <see cref="PropertyChanged"/> with the indexer property name ("Item[]")
/// when the culture changes so WPF bindings refresh without an application restart.
///
/// <para>No UI code may branch on the active language directly — every visible
/// string flows through this service. Missing keys fall back to the default
/// (English) culture and, as a last resort, to the key itself, so an incomplete
/// translation never blanks the UI or throws.</para>
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>Currently active UI culture.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>
    /// Raised after <see cref="SetCulture"/> changes the active language. Subscribers
    /// (including the XAML layer) use this to refresh any cached/localized state.
    /// </summary>
    event EventHandler? CultureChanged;

    /// <summary>
    /// Resolves <paramref name="key"/> in the current culture, falling back to the
    /// default (English) culture and finally to the key itself when unknown.
    /// </summary>
    string this[string key] { get; }

    /// <summary>Switches the active language and notifies subscribers.</summary>
    void SetCulture(CultureInfo culture);

    /// <summary>True when <paramref name="key"/> has a translation in the current culture.</summary>
    bool Contains(string key);
}
