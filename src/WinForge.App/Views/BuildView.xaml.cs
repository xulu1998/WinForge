using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Build step host — an honest placeholder. It deliberately does not fake an ISO
/// rebuild or image export; it only reflects whether the Apply step has completed.
/// </summary>
public partial class BuildView : UserControl
{
    public BuildView()
    {
        InitializeComponent();
    }
}
