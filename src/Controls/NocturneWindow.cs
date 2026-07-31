using System.Windows;
using System.Windows.Shell;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>
/// Base for the borderless Nocturne windows: custom <see cref="WindowChrome"/> with a 42px
/// caption drag region, no Aero caption buttons, DWM-rounded corners on Windows 11, and shared
/// caption-button handlers. The 1px #3f424d ring is each window's outermost Border; the thin
/// bottom glass strip keeps the standard DWM drop shadow alive on a borderless window.
/// </summary>
public class NocturneWindow : Window
{
    private readonly WindowChrome _chrome;

    public NocturneWindow()
    {
        WindowStyle = WindowStyle.None;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _chrome = new WindowChrome
        {
            CaptionHeight = 42,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, _chrome);
    }

    /// <summary>Enables the invisible resize border for resizable windows (dashboard).</summary>
    protected void EnableResizeBorder(double thickness) =>
        _chrome.ResizeBorderThickness = new Thickness(thickness);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCornerInterop.TryApplyRoundedCorners(this);
    }

    protected void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    protected void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
