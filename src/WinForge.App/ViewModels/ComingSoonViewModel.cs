using WinForge.App.Mvvm;

namespace WinForge.App.ViewModels;

/// <summary>
/// Shared view model for the not-yet-implemented pages. The title is set by
/// <see cref="MainViewModel"/> when navigating to a future-phase page.
/// </summary>
public sealed class ComingSoonViewModel : ViewModelBase
{
    private string _title = "Coming soon";

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }
}
