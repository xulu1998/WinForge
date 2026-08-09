using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Source step host. Presents the <see cref="WinForge.App.ViewModels.ImageViewModel"/>
/// source/inspection/edition sections so the user can pick the Windows image and
/// target edition before moving to Prepare.
/// </summary>
public partial class SourceView : UserControl
{
    public SourceView()
    {
        InitializeComponent();
    }
}
