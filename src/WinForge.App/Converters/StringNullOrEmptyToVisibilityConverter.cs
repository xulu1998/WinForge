using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinForge.App.Converters;

/// <summary>
/// Converts a null or empty/whitespace string to <see cref="Visibility.Collapsed"/>
/// and any non-empty string to <see cref="Visibility.Visible"/>. Used by the Apps
/// detail panel to show an explicit block reason ONLY for components that cannot
/// be removed (selectable components have an empty reason and stay collapsed).
/// </summary>
public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
