using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher.Views;

/// <summary>
/// The 1e tray flyout: mini now-playing row, status line, and the tray menu as Nocturne rows.
/// Shown near the tray on a tray-icon click; dismisses on deactivation or Esc.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    // The visible panel sits inside a 16px transparent shadow margin.
    private const double ShadowMargin = 16;
    private const double EdgeOffset = 8;

    public TrayFlyoutWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Hide();
            }
        };
    }

    public event Action? OpenSettingsRequested;

    public event Action? ExitRequested;

    /// <summary>Positions the flyout near the tray (cursor) and shows it.</summary>
    public void ShowNearTray()
    {
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        var cursorX = cursor.X / dpi.DpiScaleX;
        var cursorY = cursor.Y / dpi.DpiScaleY;
        var workArea = SystemParameters.WorkArea;

        Left = Math.Clamp(
            cursorX - ActualWidth / 2,
            workArea.Left + EdgeOffset - ShadowMargin,
            workArea.Right - ActualWidth - EdgeOffset + ShadowMargin);
        // Taskbar at the bottom (the common case) → open upward; taskbar at the top → downward.
        Top = cursorY > workArea.Top + workArea.Height / 2
            ? workArea.Bottom - ActualHeight - EdgeOffset + ShadowMargin
            : workArea.Top + EdgeOffset - ShadowMargin;
        Activate();
    }

    private void MenuRow_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void OpenSettingsRow_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenSettingsRequested?.Invoke();
    }

    private void ExitRow_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke();
    }
}
