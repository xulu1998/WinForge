using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinForge.App.Mvvm;

/// <summary>
/// Base class for view models. Provides <see cref="INotifyPropertyChanged"/>
/// with a compiled-friendly <c>SetField</c> helper. Deliberately free of any
/// WPF reference so the pattern stays testable; commands live in this same
/// namespace and rely on <see cref="System.Windows.Input.ICommand"/>.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
