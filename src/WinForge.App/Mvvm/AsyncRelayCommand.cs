using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WinForge.App.Mvvm;

/// <summary>
/// An <see cref="ICommand"/> that invokes an asynchronous operation. Guards
/// against re-entrancy while the operation is in flight.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
        {
            return false;
        }

        return _canExecute is null || _canExecute(parameter);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    /// <summary>
    /// Awaitable variant of <see cref="Execute"/>. Lets callers (e.g. tests and
    /// callers that need completion signaling) await the underlying operation
    /// instead of relying on the fire-and-forget <c>async void</c> path.
    /// </summary>
    public Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();

        return ExecuteCoreAsync(parameter);
    }

    private async Task ExecuteCoreAsync(object? parameter)
    {
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
