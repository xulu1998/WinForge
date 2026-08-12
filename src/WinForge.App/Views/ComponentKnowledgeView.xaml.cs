using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForge.App.ViewModels;

namespace WinForge.App.Views;

/// <summary>
/// Knowledge-backed decision surface for the Customize **Apps tab** (Stage 11.2 UX
/// rework, ADR-048 — the former separate "Component Knowledge" tab was removed and
/// this engine repurposed as the Apps tab). Renders curated components with human
/// names, recommendation/risk badges, a hover quick card, and a click-for-detail
/// panel, and selects items into the shared customization plan (non-destructive).
///
/// <para>Master–detail interaction (ADR-050): clicking a ROW (name / purpose /
/// badges / empty background) opens that component in the right-side detail panel;
/// clicking a DIFFERENT row switches the panel immediately. The checkbox is
/// isolated — its click is never reinterpreted as a row-detail selection, so removal
/// selection and inspection stay completely independent. Enter on a focused row
/// opens/switches detail (keyboard inspection); Space on the checkbox toggles
/// removal only.</para>
///
/// The code-behind is required so the BAML is actually loaded (without it the page
/// renders blank) and to host the routed-event handlers for row click / keyboard.
/// </summary>
public partial class ComponentKnowledgeView : UserControl
{
    public ComponentKnowledgeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Row (mouse) activation: opens or switches the detail panel to the clicked
    /// component. Clicks that originate on the checkbox are ignored so removal
    /// selection is never conflated with inspection. The handler is attached to the
    /// ListView (not an EventSetter on ListViewItem, which cannot resolve a
    /// code-behind handler) and resolves the row from the bubbled event's source.
    /// </summary>
    private void OnRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (FindRowItem(e.OriginalSource as DependencyObject) is { } item &&
            DataContext is ComponentKnowledgeViewModel vm)
        {
            vm.ShowDetailCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keyboard activation: Enter on a focused row opens/switches the detail panel.
    /// Space is deliberately left to the checkbox (removal toggle) and the ListView's
    /// own navigation, so keyboard row movement never toggles removal.
    /// </summary>
    private void OnRowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (FindRowItem(e.OriginalSource as DependencyObject) is { } item &&
            DataContext is ComponentKnowledgeViewModel vm)
        {
            vm.ShowDetailCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>Walks up from the event source to the owning ListViewItem and returns
    /// its bound component, or null when the source is not inside a row.</summary>
    private static ComponentKnowledgeItem? FindRowItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ListViewItem item && item.DataContext is ComponentKnowledgeItem ki)
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
