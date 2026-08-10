using System.Windows.Controls;

namespace WinForge.App.Views;

/// <summary>
/// Customize step host. Renders the six tabs (Apps / Windows components / Services
/// / Privacy / System / Experience) defined by <see cref="WinForge.App.ViewModels.CustomizeStepViewModel"/>.
/// Each tab's content is resolved through the application's <c>DataType</c> data
/// templates, reusing the existing Components / Privacy / System / ComingSoon views.
/// </summary>
public partial class CustomizeView : UserControl
{
    public CustomizeView()
    {
        InitializeComponent();
    }
}
