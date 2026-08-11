using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Knowledge-backed decision surface for the Customize **Apps tab** (Stage 11.2 UX
/// rework, ADR-048 — the former separate "Component Knowledge" tab was removed and
/// this engine repurposed as the Apps tab). Renders curated components with human
/// names, recommendation/risk badges, a hover quick card, and a click-for-detail
/// panel, and selects items into the shared customization plan (non-destructive).
/// The code-behind is required so the BAML is actually loaded (without it the page
/// renders blank).
/// </summary>
public partial class ComponentKnowledgeView : UserControl
{
    public ComponentKnowledgeView()
    {
        InitializeComponent();
    }
}
