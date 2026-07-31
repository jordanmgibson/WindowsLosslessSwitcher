using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>
/// WPF has no letter-spacing, so the design's tracked kickers (10px uppercase, .12em) are
/// approximated by interleaving hair spaces (U+200A ≈ 0.1em) between characters. Set
/// <c>controls:Tracking.Text</c> instead of <c>Text</c> on a TextBlock to apply it.
/// </summary>
public static class Tracking
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(Tracking),
        new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Text = Apply((string?)e.NewValue);
    }

    internal static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            builder.Append(text[i]);
            if (i < text.Length - 1)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
