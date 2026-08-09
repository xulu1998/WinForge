using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinForge.App.Converters;

/// <summary>
/// Converts a boolean to the inverse of <see cref="Visibility"/>: <c>true</c>
/// becomes <see cref="Visibility.Collapsed"/> and <c>false</c> becomes
/// <see cref="Visibility.Visible"/>. Used to hide an element when a flag is set.
/// </summary>
public sealed class BooleanToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
