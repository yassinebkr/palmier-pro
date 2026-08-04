using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace PalmierShell.Core;

/// Tolerant double ↔ text converter for inspector numeric fields. Display uses
/// the current culture (comma decimals on fr-FR). ConvertBack ignores empty or
/// unparseable text instead of throwing — clearing or mid-typing a field must
/// never raise InvalidCastException.
public sealed class DoubleTextConverter : IValueConverter {
    public static readonly DoubleTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        if (value is not string text) return BindingOperations.DoNothing;
        text = text.Trim();
        if (text.Length == 0) return BindingOperations.DoNothing;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            ? v
            : BindingOperations.DoNothing;
    }
}
