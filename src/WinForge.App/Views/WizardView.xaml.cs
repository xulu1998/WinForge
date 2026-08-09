using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Hosts the Stepper and the active step's content, plus the Back / Next controls.
/// The DataContext is the <see cref="WinForge.App.Workflow.WorkflowViewModel"/>.
/// </summary>
public partial class WizardView : UserControl
{
    public WizardView()
    {
        InitializeComponent();
    }
}
