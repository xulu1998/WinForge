using System;
using System.Globalization;
using System.Windows.Data;
using WinForge.App.Workflow;
using WinForge.Core.Services;

namespace WinForge.App.Converters;

/// <summary>
/// Resolves a localization key (from the first binding) through the
/// <see cref="ILocalizationService"/> supplied as the second binding. Using a
/// MultiBinding means the result re-evaluates both when the key changes and when
/// the language changes (the localization service raises PropertyChanged on
/// culture switch), so strings refresh without a restart.
/// </summary>
public sealed class LocKeyMultiConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[1] is ILocalizationService loc && values[0] is string key)
        {
            return loc[key];
        }

        return values.Length >= 1 ? (values[0]?.ToString() ?? string.Empty) : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a workflow step state to its localized caption via the Loc service.</summary>
public sealed class StateToCaptionConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[1] is ILocalizationService loc && values[0] is WorkflowStepState state)
        {
            var key = state switch
            {
                WorkflowStepState.NotAvailable => "StepState.NotAvailable",
                WorkflowStepState.Available => "StepState.Available",
                WorkflowStepState.Current => "StepState.Current",
                WorkflowStepState.Completed => "StepState.Completed",
                WorkflowStepState.RequiresAttention => "StepState.RequiresAttention",
                _ => "StepState.Available"
            };
            return loc[key];
        }

        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders the Stepper badge text: a check for completed, a warning for attention,
/// a barred circle for unavailable, otherwise the step's ordinal number. State is
/// communicated by glyph + number, not by color alone.
/// </summary>
public sealed class StepBadgeConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not WorkflowStepState state)
        {
            return string.Empty;
        }

        return state switch
        {
            WorkflowStepState.Completed => "✓",
            WorkflowStepState.RequiresAttention => "!",
            WorkflowStepState.NotAvailable => "⊘",
            _ => values[1]?.ToString() ?? string.Empty
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
