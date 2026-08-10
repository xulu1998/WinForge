using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Settings page host. Today it exposes the display-language control, which
/// switches the UI culture immediately (no restart) and persists the choice.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
