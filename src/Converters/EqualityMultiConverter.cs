using System.Globalization;
using System.Windows.Data;

namespace WindowsLosslessSwitcher.Converters;

/// <summary>
/// True when both bound values are equal (string comparison). Used for chip groups whose
/// selected value lives on the view model while the chip's own value comes from the item —
/// XAML cannot pass a binding as ConverterParameter, so both sides are bindings here.
/// </summary>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 &&
        string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
