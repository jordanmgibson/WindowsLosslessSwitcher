using System.Drawing;
using System.Windows.Forms;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Owns the system tray icon, its menu, and lightweight status text.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private static readonly Uri AppIconResourceUri = new("pack://application:,,,/Assets/WLS.ico", UriKind.Absolute);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _currentFormatItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _updateStatusItem;
    private readonly ToolStripMenuItem _checkForUpdatesItem;
    private readonly ToolStripMenuItem _updatePrimaryActionItem;
    private readonly ToolStripMenuItem _openReleasesItem;
    private readonly Icon _trayIcon;

    public TrayIconHost()
    {
        _statusItem = new ToolStripMenuItem("Resolver: Idle") { Enabled = false };
        _currentFormatItem = new ToolStripMenuItem(AudioFormatTextFormatter.BuildTrayCurrentFormatText(null)) { Enabled = false };
        _versionItem = new ToolStripMenuItem("Version 0.1.0") { Enabled = false };
        _updateStatusItem = new ToolStripMenuItem("Updates are not configured yet.") { Enabled = false };

        _checkForUpdatesItem = new ToolStripMenuItem("Check for updates");
        _checkForUpdatesItem.Click += (_, _) => CheckForUpdatesRequested?.Invoke();

        _updatePrimaryActionItem = new ToolStripMenuItem("Update") { Visible = false };
        _updatePrimaryActionItem.Click += (_, _) => RunUpdatePrimaryActionRequested?.Invoke();

        _openReleasesItem = new ToolStripMenuItem("Open releases");
        _openReleasesItem.Click += (_, _) => OpenReleasesRequested?.Invoke();

        var openItem = new ToolStripMenuItem("Open Settings");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_currentFormatItem);
        menu.Items.Add(_versionItem);
        menu.Items.Add(_updateStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_checkForUpdatesItem);
        menu.Items.Add(_updatePrimaryActionItem);
        menu.Items.Add(_openReleasesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) => RefreshCurrentFormat();

        _trayIcon = LoadTrayIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = "Windows Lossless Switcher",
            Visible = true,
            Icon = _trayIcon,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public event Action? CheckForUpdatesRequested;

    public event Action? RunUpdatePrimaryActionRequested;

    public event Action? OpenReleasesRequested;

    public Func<string>? CurrentFormatTextProvider { get; set; }

    public void UpdateStatus(string statusText)
    {
        _statusItem.Text = statusText;
        _notifyIcon.Text = statusText.Length > 63 ? statusText[..63] : statusText;
    }

    public void UpdateCurrentFormat(string formatText)
    {
        _currentFormatItem.Text = formatText;
    }

    public void UpdateVersion(UpdateStatusSnapshot status)
    {
        _versionItem.Text = $"Version {status.CurrentVersion}";
        _updateStatusItem.Text = status.StatusText.Length > 63
            ? $"{status.StatusText[..60]}..."
            : status.StatusText;
        _checkForUpdatesItem.Enabled = status.CanCheckForUpdates;
        _updatePrimaryActionItem.Visible =
            status.PrimaryActionKind is not UpdateActionKind.None and not UpdateActionKind.OpenReleasesPage;
        _updatePrimaryActionItem.Text = status.PrimaryActionLabel ?? "Update";
        _updatePrimaryActionItem.Enabled = status.CanRunPrimaryAction;
        _openReleasesItem.Enabled = status.CanOpenReleasesPage;
    }

    private void RefreshCurrentFormat()
    {
        if (CurrentFormatTextProvider is null)
        {
            return;
        }

        UpdateCurrentFormat(CurrentFormatTextProvider());
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
