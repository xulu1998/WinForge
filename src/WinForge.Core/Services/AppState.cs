using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Default in-memory implementation of <see cref="IAppState"/>.
/// </summary>
public sealed class AppState : IAppState
{
    private string? _sourceImagePath;
    private WindowsEditionInfo? _selectedEdition;
    private BuildStatus _buildStatus = BuildStatus.NotStarted;
    private readonly BuildPlan _configuration = new();

    public string? SourceImagePath
    {
        get => _sourceImagePath;
        set => SetField(ref _sourceImagePath, value);
    }

    public WindowsEditionInfo? SelectedEdition
    {
        get => _selectedEdition;
        set => SetField(ref _selectedEdition, value);
    }

    public BuildPlan Configuration => _configuration;

    public string ConfigurationLabel => "Default";

    public BuildStatus BuildStatus
    {
        get => _buildStatus;
        set => SetField(ref _buildStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
