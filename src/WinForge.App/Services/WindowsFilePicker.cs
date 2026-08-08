using Microsoft.Win32;

namespace WinForge.App.Services;

/// <summary>
/// WPF implementation of <see cref="IFilePicker"/> using the native
/// OpenFileDialog. Lives in the App project; the ViewModel depends only on the
/// interface, never on the dialog directly.
/// </summary>
public sealed class WindowsFilePicker : IFilePicker
{
    public string? PickIsoFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Windows ISO (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Select a Windows ISO"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
