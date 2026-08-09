using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinForge.App.Converters;

/// <summary>
/// Converts a boolean to a <see cref="FontWeight"/> so a status label can be
/// emphasised (bold) when the servicing workspace is mounted. <c>true</c> maps
/// to <see cref="FontWeights.Bold"/>, <c>false</c> to <see cref="FontWeights.Normal"/>.
/// </summary>
public sealed class BooleanToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? FontWeights.Bold : FontWeights.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
