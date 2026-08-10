namespace WinForge.App.Services;

/// <summary>
/// Platform file-picker abstraction so the ViewModel stays testable and never
/// touches WPF dialogs directly. The concrete implementation lives in the App
/// project (it needs <c>Microsoft.Win32.OpenFileDialog</c>).
/// </summary>
public interface IFilePicker
{
    /// <summary>
    /// Shows the ISO file picker. Returns the selected path, or null when the
    /// user cancels.
    /// </summary>
    string? PickIsoFile();

    /// <summary>
    /// Shows a folder picker for choosing an output directory. Returns the selected
    /// folder path, or null when the user cancels.
    /// </summary>
    string? PickFolder();
}
