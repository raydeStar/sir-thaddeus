using System.Globalization;
using System.Windows.Data;

namespace SirThaddeus.DesktopRuntime.Converters;

/// <summary>
/// Two-way converter that maps a string property to a boolean (for RadioButton IsChecked).
/// Returns true when the bound value equals the ConverterParameter (case-insensitive).
/// On ConvertBack, returns the ConverterParameter string when true.
/// </summary>
public sealed class StringMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string ?? "";
        var param = parameter as string ?? "";
        return string.Equals(str.Trim(), param.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
            return (parameter as string ?? "").Trim();
        return System.Windows.Data.Binding.DoNothing;
    }
}
