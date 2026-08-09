using System.Reflection;
using WinForge.App.Mvvm;

namespace WinForge.App.ViewModels;

/// <summary>
/// About page. Surfaces the product description (localized in XAML via the Loc
/// service) and the assembly version.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    public string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly().GetName().Version;
            return asm is null ? "0.0.0.0" : asm.ToString();
        }
    }
}
