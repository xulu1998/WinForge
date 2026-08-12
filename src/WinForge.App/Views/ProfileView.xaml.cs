using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Stage 11.4 profile selector panel — the "what kind of Windows are you
/// building?" surface at the top of Customize (ADR-057..060). Pure view: all
/// logic lives in <see cref="WinForge.App.ViewModels.ProfileViewModel"/>.
/// </summary>
public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
    }
}
