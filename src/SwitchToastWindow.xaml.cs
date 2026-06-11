using System.Windows;
using System.Windows.Threading;

namespace WindowsLosslessSwitcher;

public partial class SwitchToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    public SwitchToastWindow(string title, string message, string? deviceName, string? trackDetails)
    {
        InitializeComponent();
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        if (!string.IsNullOrWhiteSpace(trackDetails))
        {
            TrackDetailsTextBlock.Text = trackDetails;
            TrackDetailsTextBlock.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            DeviceTextBlock.Text = deviceName;
            DeviceTextBlock.Visibility = Visibility.Visible;
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
        Left = workArea.Right - ActualWidth - 16;
        Top = workArea.Bottom - ActualHeight - 16;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _closeTimer.Stop();
        Close();
    }
}
