using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Code-behind for <see cref="ComponentIntelligenceView"/>. The XAML root is a
/// <see cref="UserControl"/> whose BAML defines the component-intelligence prototype
/// (list + detail). Without this partial class WPF never generates a constructor that
/// calls <c>InitializeComponent()</c>, so the BAML is never loaded and the page renders
/// blank — the constructor below is what wires the compiled BAML into the control.
/// </summary>
public partial class ComponentIntelligenceView : UserControl
{
    public ComponentIntelligenceView()
    {
        InitializeComponent();
    }
}
