using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinForge.Core.Models;

namespace WinForge.App.Converters;

/// <summary>
/// Maps a <see cref="RecommendationLevel"/> onto a semantic color so the user can
/// read the recommendation at a glance (green = safe to remove, red = never remove).
/// Color is never the sole signal — the text caption is always shown alongside.
/// </summary>
public sealed class RecommendationToColorConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RecommendationLevel r)
        {
            return new SolidColorBrush(Colors.Gray);
        }

        return r switch
        {
            RecommendationLevel.RecommendedRemove => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x85, 0x5A)),
            RecommendationLevel.OptionalRemove => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9)),
            RecommendationLevel.UsuallyKeep => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB7, 0x79, 0x0E)),
            RecommendationLevel.AdvancedOnly => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0x6B, 0x20)),
            RecommendationLevel.NeverRemove => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC5, 0x30, 0x30)),
            _ => new SolidColorBrush(System.Windows.Media.Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
