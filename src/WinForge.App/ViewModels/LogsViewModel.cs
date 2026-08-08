using System.Collections.ObjectModel;
using System.Windows;
using WinForge.App.Mvvm;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Logs page. Mirrors the logger's live entries into an
/// <see cref="ObservableCollection{T}"/> so the UI updates in real time.
/// Cross-thread additions are marshalled to the UI dispatcher.
/// </summary>
public sealed class LogsViewModel : ViewModelBase
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogsViewModel(ILoggerService logger)
    {
        foreach (var entry in logger.Entries)
        {
            Entries.Add(entry);
        }

        logger.EntryAdded += (_, entry) =>
        {
            var app = Application.Current;
            if (app is null || app.Dispatcher.CheckAccess())
            {
                Entries.Add(entry);
            }
            else
            {
                app.Dispatcher.Invoke(() => Entries.Add(entry));
            }
        };
    }
}
