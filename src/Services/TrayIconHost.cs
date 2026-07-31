using System.Drawing;
using System.Windows.Forms;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Owns the system tray icon and its tooltip. The old Win32 context menu is gone: a single
/// click (either button) raises <see cref="FlyoutRequested"/> and the app shows the Nocturne
/// tray flyout; a double click still opens the main window directly.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private static readonly Uri AppIconResourceUri = new("pack://application:,,,/Assets/WLS.ico", UriKind.Absolute);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;

    public TrayIconHost()
    {
        _trayIcon = LoadTrayIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = "Windows Lossless Switcher",
            Visible = true,
            Icon = _trayIcon,
        };

        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button is MouseButtons.Left or MouseButtons.Right)
            {
                FlyoutRequested?.Invoke();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? FlyoutRequested;

    public void UpdateStatus(string statusText)
    {
        // Win32 NotifyIcon tooltip text is capped at 63 characters.
        _notifyIcon.Text = statusText.Length > 63 ? statusText[..63] : statusText;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(AppIconResourceUri);
            if (resource?.Stream is null)
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            using var resourceStream = resource.Stream;
            using var icon = new Icon(resourceStream);
            return (Icon)icon.Clone();
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
