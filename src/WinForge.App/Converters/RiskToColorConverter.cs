using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinForge.Core.Models;

namespace WinForge.App.Converters;

/// <summary>
/// Maps a <see cref="RiskLevel"/> onto a semantic color (green = low, red = critical)
/// for the component detail panel. Color is paired with the text caption, never used alone.
/// </summary>
public sealed class RiskToColorConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RiskLevel r)
        {
            return new SolidColorBrush(Colors.Gray);
        }

        return r switch
        {
            RiskLevel.Low => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x85, 0x5A)),
            RiskLevel.Medium => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB7, 0x79, 0x0E)),
            RiskLevel.High => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0x6B, 0x20)),
            RiskLevel.Critical => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC5, 0x30, 0x30)),
            _ => new SolidColorBrush(System.Windows.Media.Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
