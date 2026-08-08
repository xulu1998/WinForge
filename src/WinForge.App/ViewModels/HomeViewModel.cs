using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Home page. Shows the product intro, four status tiles bound to
/// <see cref="IAppState"/>, and a button that navigates to the Image page.
/// It reacts to app-state changes so the tiles stay in sync after an image is
/// selected elsewhere.
/// </summary>
public sealed class HomeViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly INavigationService _navigation;

    public HomeViewModel(IAppState appState, INavigationService navigation)
    {
        _appState = appState;
        _navigation = navigation;

        _appState.PropertyChanged += (_, e) => OnAppStateChanged(e.PropertyName);
        SelectImageCommand = new RelayCommand(_ => _navigation.NavigateTo(PageKey.Image));
    }

    public string SourceImageDisplay =>
        string.IsNullOrWhiteSpace(_appState.SourceImagePath) ? "Not selected" : _appState.SourceImagePath!;

    public string EditionDisplay => _appState.SelectedEdition?.Name ?? "Not selected";

    public string ConfigurationDisplay => _appState.ConfigurationLabel;

    public string BuildStatusDisplay => _appState.BuildStatus.ToString();

    public System.Windows.Input.ICommand SelectImageCommand { get; }

    private void OnAppStateChanged(string? propertyName)
    {
        // The derived display strings read live from app state; re-raise so the
        // UI refreshes regardless of which property changed.
        OnPropertyChanged(nameof(SourceImageDisplay));
        OnPropertyChanged(nameof(EditionDisplay));
        OnPropertyChanged(nameof(BuildStatusDisplay));
    }
}
