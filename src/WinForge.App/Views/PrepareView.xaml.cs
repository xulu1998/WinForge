using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Prepare step host. Presents the <see cref="WinForge.App.ViewModels.ImageViewModel"/>
/// working-image lifecycle (prepare / mount / unmount) once a source image and
/// edition have been chosen in the Source step.
/// </summary>
public partial class PrepareView : UserControl
{
    public PrepareView()
    {
        InitializeComponent();
    }
}
