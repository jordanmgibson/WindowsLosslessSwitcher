using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace WindowsLosslessSwitcher.Converters;

/// <summary>
/// True when the bound value equals the converter parameter (string comparison of both sides,
/// so it works for enums and ints alike). ConvertBack maps true back to the parameter, which
/// makes it usable for segmented RadioButton controls bound to a single property.
/// </summary>
public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var text = parameter.ToString() ?? string.Empty;
        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, text);
        }

        if (targetType == typeof(int) || targetType == typeof(int?))
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        return Binding.DoNothing;
    }
}
