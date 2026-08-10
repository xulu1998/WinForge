using System.Windows.Forms;

namespace WinForge.App.Services;

/// <summary>
/// WPF implementation of <see cref="IFilePicker"/>. The ISO picker uses the native
/// WPF <c>Microsoft.Win32.OpenFileDialog</c> (returns <c>bool?</c>); the folder
/// picker uses the Windows Forms <c>FolderBrowserDialog</c> (returns
/// <c>DialogResult</c>). Both live in the App project so the ViewModel depends
/// only on the interface and never touches the dialogs directly.
/// </summary>
public sealed class WindowsFilePicker : IFilePicker
{
    public string? PickIsoFile()
    {
        // Microsoft.Win32.OpenFileDialog is the WPF file dialog (ShowDialog returns bool?).
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Windows ISO (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Select a Windows ISO"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the output folder for the built ISO",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
