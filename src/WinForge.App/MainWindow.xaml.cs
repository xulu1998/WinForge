using System.Windows;

namespace WinForge.App;

/// <summary>
/// Host window for the navigation shell. The data context is assigned by
/// <see cref="App.OnStartup"/>; this code-behind only handles UI lifecycle.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
