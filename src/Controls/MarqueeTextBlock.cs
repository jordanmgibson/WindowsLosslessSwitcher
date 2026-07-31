using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>
/// A single-line text presenter that scrolls horizontally (classic marquee) when the text does
/// not fit its available width, and sits still when it does. Font properties inherit through the
/// visual tree the same way they do for a plain TextBlock (set them via
/// <c>TextElement.FontSize</c>/<c>Foreground</c>/... on this element or an ancestor).
///
/// The scroll runs: hold → glide left just far enough to reveal the end → hold → snap back,
/// forever. The animation is stopped while the control is not visible so a hidden tray flyout
/// costs nothing.
/// </summary>
public sealed class MarqueeTextBlock : Decorator
{
    private const double HoldSeconds = 1.8;
    private const double PixelsPerSecond = 28.0;
    private const double EndPadding = 12.0;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarqueeTextBlock),
        new FrameworkPropertyMetadata(string.Empty, (d, _) => ((MarqueeTextBlock)d).OnTextChanged()));

    private readonly TextBlock _text;
    private readonly TranslateTransform _transform = new();
    private Storyboard? _storyboard;

    public MarqueeTextBlock()
    {
        _text = new TextBlock
        {
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap,
            RenderTransform = _transform,
        };
        Child = _text;
        ClipToBounds = true;

        SizeChanged += (_, _) => RestartMarquee();
        IsVisibleChanged += (_, _) => RestartMarquee();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // The child must measure to its natural (unconstrained) width so overflow is detectable,
    // while this element itself takes only the width its parent grants.
    protected override Size MeasureOverride(Size constraint)
    {
        _text.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var desired = _text.DesiredSize;
        return new Size(Math.Min(desired.Width, constraint.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _text.Arrange(new Rect(new Size(Math.Max(_text.DesiredSize.Width, arrangeSize.Width), arrangeSize.Height)));
        return arrangeSize;
    }

    private void OnTextChanged()
    {
        _text.Text = Text;
        RestartMarquee();
    }

    private void RestartMarquee()
    {
        StopMarquee();
        if (!IsVisible)
        {
            return;
        }

        var overflow = _text.DesiredSize.Width - ActualWidth;
        if (overflow <= 1)
        {
            return;
        }

        var distance = overflow + EndPadding;
        var glideSeconds = distance / PixelsPerSecond;
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var t = TimeSpan.Zero;
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(t)));
        t += TimeSpan.FromSeconds(HoldSeconds);
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t)));
        t += TimeSpan.FromSeconds(glideSeconds);
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t)));
        t += TimeSpan.FromSeconds(HoldSeconds);
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t)));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, _text);
        Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.(TranslateTransform.X)"));
        _storyboard = storyboard;
        storyboard.Begin(_text, isControllable: true);
    }

    private void StopMarquee()
    {
        if (_storyboard is not null)
        {
            _storyboard.Stop(_text);
            _storyboard = null;
        }

        _transform.X = 0;
        _text.RenderTransform = _transform;
    }
}
