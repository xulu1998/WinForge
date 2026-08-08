using System.Collections.ObjectModel;
using System.Threading;
using WinForge.App.Mvvm;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Logs page. Mirrors the logger's live entries into an
/// <see cref="ObservableCollection{T}"/> — the only WPF-bound collection in the
/// pipeline — so the UI updates in real time.
///
/// Logging may occur on background threads (future DISM/Process workers). The
/// source logger never touches this collection; instead it raises
/// <see cref="ILoggerService.EntryAdded"/> and this view model marshals the
/// mutation back to the UI thread via the <see cref="SynchronizationContext"/>
/// that was current when the view model was constructed (the Dispatcher context
/// on the WPF UI thread). The <see cref="ObservableCollection{T}"/> is therefore
/// only ever modified from the UI thread, eliminating cross-thread exceptions.
/// </summary>
public sealed class LogsViewModel : ViewModelBase
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    // Captured on the UI thread during App.OnStartup. In a headless/CI run with
    // no synchronization context this is null and updates are applied inline,
    // which is correct because the test thread is effectively the "UI" thread.
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public LogsViewModel(ILoggerService logger)
    {
        // Seed with everything already recorded. Entries is a thread-safe
        // snapshot, so this enumeration is safe even if a background writer is
        // concurrently active.
        foreach (var entry in logger.Entries)
        {
            Entries.Add(entry);
        }

        logger.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        // Already on the UI thread (or no context was captured) -> add inline.
        if (_uiContext is null || _uiContext == SynchronizationContext.Current)
        {
            Entries.Add(entry);
            return;
        }

        // Background thread -> marshal the mutation back to the UI thread so the
        // ObservableCollection is never modified off the UI thread.
        _uiContext.Post(_ => Entries.Add(entry), null);
    }
}
