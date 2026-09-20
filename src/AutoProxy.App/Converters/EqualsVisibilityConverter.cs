using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AutoProxy.App.Converters;

/// <summary>
/// Returns Visible when the bound value's string form equals the converter
/// parameter (case-insensitive), otherwise Collapsed.
/// </summary>
public class EqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equals = string.Equals(
            value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        return equals ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
