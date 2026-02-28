using System.Globalization;
using System.Windows.Data;

namespace SirThaddeus.DesktopRuntime.Converters;

/// <summary>
/// Converts a boolean value to one of two text strings.
/// ConverterParameter format: "TrueText|FalseText" (e.g. "KILL VOICE SERVER|START VOICE SERVER")
/// </summary>
public sealed class BooleanToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var paramStr = parameter as string ?? "True|False";
        var parts = paramStr.Split('|');
        var trueText = parts.Length > 0 ? parts[0] : "True";
        var falseText = parts.Length > 1 ? parts[1] : "False";

        return value is true ? trueText : falseText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
