using System.Globalization;
using System.Windows.Data;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.Converters;

public sealed class SampleRateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int rate ? AudioFormatTextFormatter.FormatSampleRate(rate, culture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
