using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// A single entry in the left navigation rail.
/// </summary>
public sealed class NavItem : ViewModelBase
{
    private bool _isActive;

    public NavItem(PageKey key, string title, ICommand command)
    {
        Key = key;
        Title = title;
        Command = command;
    }

    public PageKey Key { get; }

    public string Title { get; }

    public ICommand Command { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}
