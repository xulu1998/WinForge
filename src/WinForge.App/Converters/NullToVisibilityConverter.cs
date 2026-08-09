using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinForge.App.Converters;

/// <summary>
/// Converts a null reference to <see cref="Visibility.Collapsed"/> and any
/// non-null value to <see cref="Visibility.Visible"/>. Used to show an optional
/// status/error string in the Image page only when one is present.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
