using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForge.App.ViewModels;

namespace WinForge.App.Views;

/// <summary>
/// Knowledge-backed decision surface shared by the Customize Services / Privacy /
/// System / Personalization tabs (Stage 11.3, ADR-051/ADR-052). It reuses the
/// same master–detail interaction as the Apps tab (ADR-050): clicking a ROW
/// opens/switches the right-side detail panel; the checkbox is isolated so
/// selection and inspection stay independent; Enter opens/switches detail and
/// Space stays on the checkbox.
///
/// The code-behind hosts the routed-event handlers for row click / keyboard
/// (an EventSetter on ListViewItem cannot resolve a code-behind handler).
/// </summary>
public partial class OptimizationKnowledgeView : UserControl
{
    public OptimizationKnowledgeView()
    {
        InitializeComponent();
    }

    private void OnRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (FindRowItem(e.OriginalSource as DependencyObject) is { } item &&
            DataContext is OptimizationKnowledgeViewModel vm)
        {
            vm.ShowDetailCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnRowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (FindRowItem(e.OriginalSource as DependencyObject) is { } item &&
            DataContext is OptimizationKnowledgeViewModel vm)
        {
            vm.ShowDetailCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>Walks up from the event source to the owning ListViewItem and returns
    /// its bound optimization entry, or null when the source is not inside a row.</summary>
    private static OptimizationKnowledgeItem? FindRowItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ListViewItem item && item.DataContext is OptimizationKnowledgeItem ki)
            {
                return ki;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>True when the routed event originated inside a CheckBox (so it must
    /// not be treated as a row-detail click).</summary>
    private static bool IsWithinCheckBox(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.CheckBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
