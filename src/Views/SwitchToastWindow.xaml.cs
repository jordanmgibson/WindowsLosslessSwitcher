using System.Windows;
using System.Windows.Threading;
using WindowsLosslessSwitcher.Controls;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.Views;

/// <summary>
/// The Nocturne switch toast — one window, two layouts (1c rich card / 1d minimal pill),
/// chosen by the content record. Bottom-right of the work area, 4-second auto-close, click on
/// the pill (or the rich card's X) dismisses.
/// </summary>
public partial class SwitchToastWindow : Window, ISwitchToastWindow
{
    // The 28px transparent margin exists only for the shadow; the visible card should still sit
    // 16px from the work-area edges.
    private const double OuterMargin = 28;
    private const double EdgeOffset = 16;

    private readonly DispatcherTimer _closeTimer;

    internal SwitchToastWindow(SwitchToastContent content)
    {
        InitializeComponent();

        if (content.Variant == ToastVariant.Pill)
        {
            RichRoot.Visibility = Visibility.Collapsed;
            PillRoot.Visibility = Visibility.Visible;
            PillRateText.Text = content.NewRateText ?? content.NewFormatText;
            PillBitsText.Text = content.NewBitsText is { Length: > 0 } bits ? $"{bits} · lossless" : "lossless";
            if (content.Artwork is not null)
            {
                PillArtworkBrush.ImageSource = content.Artwork;
                PillArtworkHost.Visibility = Visibility.Visible;
            }

            // The pill has no close button; clicking anywhere dismisses it.
            MouseLeftButtonUp += (_, _) => DismissNow();
        }
        else
        {
            KickerText.Text = Tracking.Apply(content.Kicker);
            if (content.OldFormatText is { Length: > 0 } old)
            {
                OldFormatText.Text = old;
            }
            else
            {
                OldFormatText.Visibility = Visibility.Collapsed;
                TransitionArrow.Visibility = Visibility.Collapsed;
            }

            NewFormatText.Text = content.NewFormatText ?? "";
            if (content.TrackLine is { Length: > 0 } trackLine)
            {
                TrackLineText.Text = trackLine;
                TrackLineText.Visibility = Visibility.Visible;
            }

            if (content.DeviceName is { Length: > 0 } device)
            {
                DeviceLineText.Text = device;
                DeviceLineText.Visibility = Visibility.Visible;
            }

            if (content.Artwork is not null)
            {
                RichArtworkBrush.ImageSource = content.Artwork;
                RichArtworkHost.Visibility = Visibility.Visible;
            }
        }

        Loaded += (_, _) => PositionInBottomRightCorner();

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };
        Closed += (_, _) => _closeTimer.Stop();
    }

    public void StartAutoClose() => _closeTimer.Start();

    private void PositionInBottomRightCorner()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth + OuterMargin - EdgeOffset;
        Top = workArea.Bottom - ActualHeight + OuterMargin - EdgeOffset;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => DismissNow();

    private void DismissNow()
    {
        _closeTimer.Stop();
        Close();
    }
}
