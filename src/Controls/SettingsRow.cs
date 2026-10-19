using System.Windows;
using System.Windows.Controls;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>A wrapping settings label and description with a separate control column.</summary>
public sealed class SettingsRow : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
