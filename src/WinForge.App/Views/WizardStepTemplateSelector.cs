using System.Windows;
using System.Windows.Controls;
using WinForge.App.Workflow;

namespace WinForge.App.Views;

/// <summary>
/// Picks the DataTemplate for the active workflow step. Each step renders a
/// different inner view (Source / Prepare / Customize / Review / Apply / Build)
/// driven by the step's <see cref="WorkflowStep"/>, not by the content type.
/// </summary>
public sealed class WizardStepTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SourceTemplate { get; set; }

    public DataTemplate? PrepareTemplate { get; set; }

    public DataTemplate? CustomizeTemplate { get; set; }

    public DataTemplate? ReviewTemplate { get; set; }

    public DataTemplate? ApplyTemplate { get; set; }

    public DataTemplate? BuildTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is WorkflowStepViewModel step)
        {
            return step.Step switch
            {
                WorkflowStep.Source => SourceTemplate,
                WorkflowStep.Prepare => PrepareTemplate,
                WorkflowStep.Customize => CustomizeTemplate,
                WorkflowStep.Review => ReviewTemplate,
                WorkflowStep.Apply => ApplyTemplate,
                WorkflowStep.Build => BuildTemplate,
                _ => null
            };
        }

        return null;
    }
}
