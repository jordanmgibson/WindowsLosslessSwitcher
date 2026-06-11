using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;

namespace WindowsLosslessSwitcher;

public partial class App : Application
{
    private readonly SettingsService _settingsService = new();
    private readonly StartupRegistrationService _startupRegistrationService = new();
    private readonly StartupInstanceGuard _instanceGuard = new();
    private readonly DiagnosticsLogger _logger = new();
    private readonly IAppUpdater _appUpdater;
    private readonly AppleMusicPaths _paths = new();
    private readonly BinaryPlistReader _plistReader = new();
    private readonly MainWindowViewModel _viewModel = new();

    private TrayIconHost? _trayIconHost;
    private SwitchToastService? _switchToastService;
    private MainWindow? _mainWindow;
    private SwitchingCoordinator? _coordinator;
    private AppSettings? _settings;
    private SwitchingStatus? _latestStatus;

    public App()
    {
        _appUpdater = new AppUpdater(_logger);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launchMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (!_instanceGuard.TryAcquire())
        {
            _logger.Warn("Startup ownership: duplicate instance rejected.");
            if (!launchMinimized)
            {
                System.Windows.MessageBox.Show(
                    "Windows Lossless Switcher is already running.",
                    "Windows Lossless Switcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _logger.Info("Startup ownership: primary instance.");

        _settings = _settingsService.Load();
        _viewModel.SelectedMode = _settings.DeviceSelectionMode;
        _viewModel.SelectedDeviceId = _settings.PinnedDeviceId;
        _viewModel.LaunchAtLogin = _settings.LaunchAtLogin;
        _viewModel.SwitchBitDepth = _settings.SwitchBitDepth;
        _viewModel.PreferClosestSampleRateMultiple = _settings.PreferClosestSampleRateMultiple;
        _viewModel.DefaultBitDepth = _settings.DefaultBitDepth;
        _viewModel.EnableSwitchToasts = _settings.EnableSwitchToasts;
        _viewModel.IncludeTrackMetadataInSwitchToasts = _settings.IncludeTrackMetadataInSwitchToasts;
        UpdateOriginalFormatRestoreState();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RefreshRequested += RefreshDevices;
        _viewModel.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        _viewModel.RunUpdatePrimaryActionRequested += OnRunUpdatePrimaryActionRequested;
        _viewModel.OpenReleasesPageRequested += OnOpenReleasesPageRequested;
        _viewModel.RestoreOriginalFormatRequested += OnRestoreOriginalFormatRequested;

        _mainWindow = new MainWindow(_viewModel);
        _mainWindow.WindowHidden += () => _trayIconHost?.UpdateStatus(_viewModel.ResolverStatusText);
        _mainWindow.DiagnosticsExportRequested += path => _logger.Export(path);
        MainWindow = _mainWindow;

        _trayIconHost = new TrayIconHost();
        _trayIconHost.OpenRequested += ShowMainWindow;
        _trayIconHost.ExitRequested += ExitApplication;
        _trayIconHost.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        _trayIconHost.RunUpdatePrimaryActionRequested += OnRunUpdatePrimaryActionRequested;
        _trayIconHost.OpenReleasesRequested += OnOpenReleasesPageRequested;
        _trayIconHost.CurrentFormatTextProvider = GetTrayCurrentFormatText;
        _trayIconHost.UpdateStatus("Resolver: Starting");
        _trayIconHost.UpdateCurrentFormat(AudioFormatTextFormatter.BuildTrayCurrentFormatText(null));
        _switchToastService = new SwitchToastService();
        _appUpdater.StatusChanged += OnAppUpdaterStatusChanged;
        _viewModel.UpdateAppVersion(_appUpdater.CurrentStatus);
        _trayIconHost.UpdateVersion(_appUpdater.CurrentStatus);

        // Order matters: the catalog resolver runs first and is the authoritative cloud-vs-local test —
        // a confident catalog match yields the exact Apple Music format. When the catalog does not match
        // (local files, or tracks it can't identify), LocalDeviceMaxResolver applies the actual format
        // read from the track's PlayCache file, or the device's highest supported format when the file
        // can't be read. TierFallbackResolver is the terminal safety net when there is no usable device.
        var audioEndpointController = new CoreAudioEndpointController();
        var resolverChain = new ResolverChain(
            [
                new AppleMusicCatalogResolver(_logger),
                new LocalDeviceMaxResolver(
                    audioEndpointController,
                    new PlayCacheTrackFormatReader(_paths, _logger),
                    _logger),
                new TierFallbackResolver(_paths, _plistReader, _logger),
            ]);

        var appleMusicTrackSource = new AppleMusicTrackSource(_logger);
        _coordinator = new SwitchingCoordinator(
            appleMusicTrackSource,
            appleMusicTrackSource,
            resolverChain,
            audioEndpointController,
            _logger,
            new AppleMusicProcessController(_logger));
        _coordinator.StatusChanged += OnCoordinatorStatusChanged;
        // Seed settings before the track source starts so target-device UI and original-format capture
        // use the user's configured selection immediately.
        _coordinator.UpdateSettings(_settings);
        RefreshDevices();
        CaptureOriginalTargetFormatIfMissing();
        await _appUpdater.InitializeAsync(CancellationToken.None);
        await _coordinator.StartAsync(_settings, CancellationToken.None);
        ApplyStartupRegistration();
        _ = CheckForUpdatesAsync(userInitiated: false);

        if (!launchMinimized)
        {
            ShowMainWindow();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
        }

        _trayIconHost?.Dispose();
        _switchToastService?.Dispose();
        _appUpdater.StatusChanged -= OnAppUpdaterStatusChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CheckForUpdatesRequested -= OnCheckForUpdatesRequested;
        _viewModel.RunUpdatePrimaryActionRequested -= OnRunUpdatePrimaryActionRequested;
        _viewModel.OpenReleasesPageRequested -= OnOpenReleasesPageRequested;
        _viewModel.RestoreOriginalFormatRequested -= OnRestoreOriginalFormatRequested;
        _instanceGuard.Dispose();
        base.OnExit(e);
    }

    private void OnCoordinatorStatusChanged(object? sender, SwitchingStatus status)
    {
        _latestStatus = status;
        // BeginInvoke (not Invoke): a synchronous dispatch blocks the coordinator's switching
        // pipeline for hundreds of milliseconds per status update, delaying the track-change
        // mute/pause and letting the new track play audibly at the old format.
        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.UpdateStatus(status);
            UpdateActiveTargetCapabilities();
            _trayIconHost?.UpdateStatus(status.ResolverStatusText);
            _trayIconHost?.UpdateCurrentFormat(GetTrayCurrentFormatText());

            if (_settings?.EnableSwitchToasts == true &&
                status.WasFormatChanged &&
                status.AppliedFormat is not null)
            {
                var toastTrack = _settings.IncludeTrackMetadataInSwitchToasts ? status.Track : null;
                _switchToastService?.ShowSwitchedFormat(status.ActiveDeviceName, status.AppliedFormat, toastTrack);
            }
        });
    }

    private void OnAppUpdaterStatusChanged(object? sender, UpdateStatusSnapshot status)
    {
        Dispatcher.Invoke(() =>
        {
            _viewModel.UpdateAppVersion(status);
            _trayIconHost?.UpdateVersion(status);
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        if (e.PropertyName is not nameof(MainWindowViewModel.SelectedMode) &&
            e.PropertyName is not nameof(MainWindowViewModel.SelectedDeviceId) &&
            e.PropertyName is not nameof(MainWindowViewModel.LaunchAtLogin) &&
            e.PropertyName is not nameof(MainWindowViewModel.SwitchBitDepth) &&
            e.PropertyName is not nameof(MainWindowViewModel.PreferClosestSampleRateMultiple) &&
            e.PropertyName is not nameof(MainWindowViewModel.DefaultBitDepth) &&
            e.PropertyName is not nameof(MainWindowViewModel.EnableSwitchToasts) &&
            e.PropertyName is not nameof(MainWindowViewModel.IncludeTrackMetadataInSwitchToasts))
        {
            return;
        }

        _settings.DeviceSelectionMode = _viewModel.SelectedMode;
        _settings.PinnedDeviceId = _viewModel.SelectedDeviceId;
        _settings.LaunchAtLogin = _viewModel.LaunchAtLogin;
        _settings.SwitchBitDepth = _viewModel.SwitchBitDepth;
        _settings.PreferClosestSampleRateMultiple = _viewModel.PreferClosestSampleRateMultiple;
        _settings.DefaultBitDepth = _viewModel.DefaultBitDepth;
        _settings.EnableSwitchToasts = _viewModel.EnableSwitchToasts;
        _settings.IncludeTrackMetadataInSwitchToasts = _viewModel.IncludeTrackMetadataInSwitchToasts;
        _settingsService.Save(_settings);
        _coordinator?.UpdateSettings(_settings);
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedMode) or nameof(MainWindowViewModel.SelectedDeviceId))
        {
            UpdateActiveTargetCapabilities();
        }

        ApplyStartupRegistration();
        _trayIconHost?.UpdateCurrentFormat(GetTrayCurrentFormatText());
    }

    private void OnCheckForUpdatesRequested() => _ = CheckForUpdatesAsync(userInitiated: true);

    private void OnRunUpdatePrimaryActionRequested() => _ = RunUpdatePrimaryActionAsync();

    private void OnOpenReleasesPageRequested() => _appUpdater.OpenReleasesPage();

    private void OnRestoreOriginalFormatRequested() => _ = RestoreOriginalFormatAsync();

    private void RefreshDevices()
    {
        var devices = _coordinator?.GetRenderDevices() ?? Array.Empty<AudioDeviceInfo>();
        _viewModel.ReplaceDevices(devices);
        if (string.IsNullOrWhiteSpace(_viewModel.SelectedDeviceId))
        {
            _viewModel.SelectedDeviceId = devices.FirstOrDefault(device => device.IsDefault)?.Id;
        }

        UpdateActiveTargetCapabilities(forceRefresh: true);
        _trayIconHost?.UpdateCurrentFormat(GetTrayCurrentFormatText());
    }

    private void CaptureOriginalTargetFormatIfMissing()
    {
        if (_settings is null || _coordinator is null)
        {
            return;
        }

        if (TryGetOriginalTargetFormat(_settings, out _))
        {
            UpdateOriginalFormatRestoreState();
            return;
        }

        var snapshot = _coordinator.GetCurrentTargetDeviceFormat();
        if (string.IsNullOrWhiteSpace(snapshot.DeviceId) || snapshot.Format is null)
        {
            _logger.Warn("Original target format capture skipped because no target device format is available yet.");
            UpdateOriginalFormatRestoreState();
            return;
        }

        _settings.OriginalTarget = new OriginalTargetSnapshot(
            snapshot.DeviceId,
            snapshot.DeviceName,
            snapshot.Format.SampleRateHz,
            snapshot.Format.BitDepth,
            snapshot.Format.Channels,
            DateTimeOffset.UtcNow);
        _settingsService.Save(_settings);
        _logger.Info($"Captured original target format {snapshot.Format.DisplayName} for {snapshot.DeviceName ?? snapshot.DeviceId}.");
        UpdateOriginalFormatRestoreState();
    }

    private async Task RestoreOriginalFormatAsync()
    {
        if (_settings is null || _coordinator is null)
        {
            return;
        }

        if (!TryGetOriginalTargetFormat(_settings, out var originalFormat) || originalFormat is null)
        {
            UpdateOriginalFormatRestoreState("Original format not captured yet.");
            return;
        }

        var originalTarget = _settings.OriginalTarget;
        if (originalTarget is null || string.IsNullOrWhiteSpace(originalTarget.DeviceId))
        {
            UpdateOriginalFormatRestoreState("Original target device was not captured.");
            return;
        }

        _viewModel.OriginalFormatText = $"Restoring original format: {BuildOriginalFormatText(_settings)}";
        _viewModel.CanRestoreOriginalFormat = false;

        using var restoreTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var result = await _coordinator.RestoreOriginalFormatAsync(
                originalTarget.DeviceId,
                originalTarget.DeviceName,
                originalFormat,
                restoreTimeoutCts.Token);

            _viewModel.OriginalFormatText = result.Succeeded
                ? $"Restored original format. Saved original: {BuildOriginalFormatText(_settings)}"
                : $"Restore failed: {result.Message}";
        }
        catch (OperationCanceledException) when (restoreTimeoutCts.IsCancellationRequested)
        {
            _logger.Warn("Restore original format timed out.");
            _viewModel.OriginalFormatText = "Restore timed out.";
        }
        catch (Exception ex)
        {
            _logger.Error("Restore original format failed unexpectedly.", ex);
            _viewModel.OriginalFormatText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            _viewModel.CanRestoreOriginalFormat = TryGetOriginalTargetFormat(_settings, out _);
            UpdateActiveTargetCapabilities(forceRefresh: true);
            _trayIconHost?.UpdateCurrentFormat(GetTrayCurrentFormatText());
        }
    }

    private void UpdateOriginalFormatRestoreState(string? statusOverride = null)
    {
        if (_settings is null)
        {
            _viewModel.OriginalFormatText = "Original format not captured yet.";
            _viewModel.CanRestoreOriginalFormat = false;
            return;
        }

        _viewModel.OriginalFormatText = statusOverride ?? BuildOriginalFormatText(_settings);
        _viewModel.CanRestoreOriginalFormat = TryGetOriginalTargetFormat(_settings, out _);
    }

    private static string BuildOriginalFormatText(AppSettings settings)
    {
        if (!TryGetOriginalTargetFormat(settings, out var format) || format is null)
        {
            return "Original format not captured yet.";
        }

        var originalTarget = settings.OriginalTarget;
        if (originalTarget is null)
        {
            return "Original format not captured yet.";
        }

        var deviceName = string.IsNullOrWhiteSpace(originalTarget.DeviceName)
            ? "Captured target device"
            : originalTarget.DeviceName;
        var capturedText = $" (captured {originalTarget.CapturedAtUtc.ToLocalTime():g})";
        return $"{deviceName}: {AudioFormatTextFormatter.Format(format)}{capturedText}";
    }

    private static bool TryGetOriginalTargetFormat(AppSettings settings, out AudioFormatCandidate? format)
    {
        format = null;
        var originalTarget = settings.OriginalTarget;
        if (originalTarget is null ||
            string.IsNullOrWhiteSpace(originalTarget.DeviceId) ||
            originalTarget.SampleRateHz <= 0 ||
            originalTarget.BitDepth <= 0 ||
            originalTarget.Channels <= 0)
        {
            return false;
        }

        format = new AudioFormatCandidate(
            originalTarget.SampleRateHz,
            originalTarget.BitDepth,
            originalTarget.Channels);
        return true;
    }

    private void ApplyStartupRegistration()
    {
        if (_settings is null || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        _startupRegistrationService.SetEnabled(_settings.LaunchAtLogin, Environment.ProcessPath);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _mainWindow?.AllowCloseAndClose();
        Shutdown();
    }

    private string GetTrayCurrentFormatText()
    {
        var snapshot = _coordinator?.GetCurrentTargetDeviceFormat();
        return AudioFormatTextFormatter.BuildTrayCurrentFormatText(snapshot?.Format);
    }

    private void UpdateActiveTargetCapabilities(bool forceRefresh = false)
    {
        var snapshot = _coordinator?.GetCurrentTargetDeviceCapabilities(forceRefresh) ??
            new CurrentTargetDeviceCapabilitiesSnapshot(null, []);
        _viewModel.UpdateActiveTargetCapabilities(snapshot);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        try
        {
            await _appUpdater.CheckForUpdatesAsync(userInitiated, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed unexpectedly.", ex);
        }
    }

    private async Task RunUpdatePrimaryActionAsync()
    {
        try
        {
            await _appUpdater.RunPrimaryActionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Update action failed unexpectedly.", ex);
        }
    }
}
