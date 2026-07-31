using System.Globalization;
using System.Windows.Data;

namespace WindowsLosslessSwitcher.Converters;

/// <summary>Formats a day count as "1 day" / "7 days" for the cache-refresh chips.</summary>
public sealed class DaysLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int days ? days == 1 ? "1 day" : $"{days} days" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
