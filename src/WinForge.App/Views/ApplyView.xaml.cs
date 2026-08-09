using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Apply step host. Reuses <see cref="PlanReviewView"/> (backed by the shared
/// <see cref="WinForge.App.ViewModels.PlanReviewViewModel"/>) for the execution UX:
/// the user validates and then writes the plan to the mounted working image.
/// </summary>
public partial class ApplyView : UserControl
{
    public ApplyView()
    {
        InitializeComponent();
    }
}
