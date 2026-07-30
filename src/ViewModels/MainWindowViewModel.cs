using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.ViewModels;

/// <summary>
/// Exposes the WPF settings window state and command surface.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly int[] SupportedDefaultBitDepths = [16, 24];

    private DeviceSelectionMode _selectedMode;
    private string? _selectedDeviceId;
    private bool _launchAtLogin;
    private bool _switchBitDepth;
    private bool _preferClosestSampleRateMultiple;
    private int _defaultBitDepth = 24;
    private bool _enableSwitchToasts;
    private bool _includeTrackMetadataInSwitchToasts;
    private int _formatCacheRefreshDays = 30;
    private string _formatCacheStatusText = "Cached formats are refreshed automatically as tracks play.";
    private string _resolverStatusText = "Idle";
    private string _currentTrackText = "No track detected";
    private string _requestedFormatText = "-";
    private string _appliedFormatText = "-";
    private string _failureReasonText = "-";
    private string _activeTargetDeviceNameText = "No active target device";
    private string _supportedSampleRatesText = "No supported formats detected.";
    private string _supportedBitDepthsText = "No supported formats detected.";
    private string _supportedFormatsText = "No supported formats detected.";
    private string _supportedFormatsDiagnosticsText = "-";
    private string _appVersionText = "Version 0.1.0";
    private string _updateStatusText = "Updates are not configured yet.";
    private string? _updatePrimaryActionText;
    private UpdateActionKind _updatePrimaryActionKind = UpdateActionKind.None;
    private bool _canCheckForUpdates;
    private bool _canRunUpdatePrimaryAction;
    private bool _canOpenReleasesPage;
    private string _originalFormatText = "Original format not captured yet.";
    private bool _canRestoreOriginalFormat;
    private string? _appleMusicStorefront;
    private bool _enableVerboseDiagnostics = true;
    private bool _restartAppleMusicOnPlaybackFailure = true;
    private List<int> _allowedSampleRates = [];
    private List<int> _allowedBitDepths = [];
    private bool _syncingFormatOptions;

    public MainWindowViewModel()
    {
        RefreshDevicesCommand = new RelayCommand(() => RefreshRequested?.Invoke());
        ExportDiagnosticsCommand = new RelayCommand(() => ExportDiagnosticsRequested?.Invoke());
        RestoreOriginalFormatCommand = new RelayCommand(
            () => RestoreOriginalFormatRequested?.Invoke(),
            () => CanRestoreOriginalFormat);
        ClearFormatCacheCommand = new RelayCommand(() => ClearFormatCacheRequested?.Invoke());
        CheckForUpdatesCommand = new RelayCommand(
            () => CheckForUpdatesRequested?.Invoke(),
            () => CanCheckForUpdates);
        RunUpdatePrimaryActionCommand = new RelayCommand(
            () => RunUpdatePrimaryActionRequested?.Invoke(),
            () => CanRunUpdatePrimaryAction);
        OpenReleasesPageCommand = new RelayCommand(
            () => OpenReleasesPageRequested?.Invoke(),
            () => CanOpenReleasesPage);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? RefreshRequested;

    public event Action? ExportDiagnosticsRequested;

    public event Action? CheckForUpdatesRequested;

    public event Action? RunUpdatePrimaryActionRequested;

    public event Action? OpenReleasesPageRequested;

    public event Action? RestoreOriginalFormatRequested;

    public event Action? ClearFormatCacheRequested;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public ObservableCollection<FormatOptionItem> SampleRateOptions { get; } = [];

    public ObservableCollection<FormatOptionItem> BitDepthOptions { get; } = [];

    public IReadOnlyList<DeviceSelectionModeOption> DeviceModes { get; } =
    [
        new(DeviceSelectionMode.FollowDefault, "Default Playback Device"),
        new(DeviceSelectionMode.PinnedDevice, "Selected Device"),
    ];

    public IReadOnlyList<int> DefaultBitDepthOptions { get; } = SupportedDefaultBitDepths;

    public IReadOnlyList<int> FormatCacheRefreshDayOptions { get; } = AppSettings.SupportedFormatCacheRefreshDays;

    public RelayCommand RefreshDevicesCommand { get; }

    public RelayCommand ExportDiagnosticsCommand { get; }

    public RelayCommand RestoreOriginalFormatCommand { get; }

    public RelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand RunUpdatePrimaryActionCommand { get; }

    public RelayCommand OpenReleasesPageCommand { get; }

    public RelayCommand ClearFormatCacheCommand { get; }

    public DeviceSelectionMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetField(ref _selectedMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSelectedDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsSelectedDeviceSelectionVisible));
        }
    }

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set => SetField(ref _selectedDeviceId, value);
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set => SetField(ref _launchAtLogin, value);
    }

    public bool SwitchBitDepth
    {
        get => _switchBitDepth;
        set
        {
            if (!SetField(ref _switchBitDepth, value))
            {
                return;
            }

            OnPropertyChanged(nameof(UseFixedBitDepthSelection));
        }
    }

    public int DefaultBitDepth
    {
        get => _defaultBitDepth;
        set => SetField(ref _defaultBitDepth, AppSettings.NormalizeBitDepth(value));
    }

    public bool PreferClosestSampleRateMultiple
    {
        get => _preferClosestSampleRateMultiple;
        set => SetField(ref _preferClosestSampleRateMultiple, value);
    }

    public bool EnableSwitchToasts
    {
        get => _enableSwitchToasts;
        set
        {
            if (!SetField(ref _enableSwitchToasts, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSwitchToastMetadataOptionVisible));
        }
    }

    public bool IncludeTrackMetadataInSwitchToasts
    {
        get => _includeTrackMetadataInSwitchToasts;
        set => SetField(ref _includeTrackMetadataInSwitchToasts, value);
    }

    public int FormatCacheRefreshDays
    {
        get => _formatCacheRefreshDays;
        set => SetField(ref _formatCacheRefreshDays, AppSettings.SnapToNearestRefreshDaysPreset(value));
    }

    public string FormatCacheStatusText
    {
        get => _formatCacheStatusText;
        set => SetField(ref _formatCacheStatusText, value);
    }

    public string ResolverStatusText
    {
        get => _resolverStatusText;
        set => SetField(ref _resolverStatusText, value);
    }

    public string CurrentTrackText
    {
        get => _currentTrackText;
        set => SetField(ref _currentTrackText, value);
    }

    public string RequestedFormatText
    {
        get => _requestedFormatText;
        set => SetField(ref _requestedFormatText, value);
    }

    public string AppliedFormatText
    {
        get => _appliedFormatText;
        set => SetField(ref _appliedFormatText, value);
    }

    public string FailureReasonText
    {
        get => _failureReasonText;
        set => SetField(ref _failureReasonText, value);
    }

    public string ActiveTargetDeviceNameText
    {
        get => _activeTargetDeviceNameText;
        set => SetField(ref _activeTargetDeviceNameText, value);
    }

    public string SupportedSampleRatesText
    {
        get => _supportedSampleRatesText;
        set => SetField(ref _supportedSampleRatesText, value);
    }

    public string SupportedBitDepthsText
    {
        get => _supportedBitDepthsText;
        set => SetField(ref _supportedBitDepthsText, value);
    }

    public string SupportedFormatsText
    {
        get => _supportedFormatsText;
        set => SetField(ref _supportedFormatsText, value);
    }

    public string SupportedFormatsDiagnosticsText
    {
        get => _supportedFormatsDiagnosticsText;
        set => SetField(ref _supportedFormatsDiagnosticsText, value);
    }

    public string AppVersionText
    {
        get => _appVersionText;
        set => SetField(ref _appVersionText, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetField(ref _updateStatusText, value);
    }

    public string? UpdatePrimaryActionText
    {
        get => _updatePrimaryActionText;
        set => SetField(ref _updatePrimaryActionText, value);
    }

    public bool CanCheckForUpdates
    {
        get => _canCheckForUpdates;
        set
        {
            if (!SetField(ref _canCheckForUpdates, value))
            {
                return;
            }

            CheckForUpdatesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanRunUpdatePrimaryAction
    {
        get => _canRunUpdatePrimaryAction;
        set
        {
            if (!SetField(ref _canRunUpdatePrimaryAction, value))
            {
                return;
            }

            RunUpdatePrimaryActionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanOpenReleasesPage
    {
        get => _canOpenReleasesPage;
        set
        {
            if (!SetField(ref _canOpenReleasesPage, value))
            {
                return;
            }

            OpenReleasesPageCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Two-letter Apple Music storefront override, or null for OS-region auto-detection.
    /// Normalized on the way in; the resolver validates and falls back at startup, so transient
    /// invalid text is harmless to hold. Applies after an app restart.
    /// </summary>
    public string? AppleMusicStorefront
    {
        get => _appleMusicStorefront;
        set => SetField(
            ref _appleMusicStorefront,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant());
    }

    public bool EnableVerboseDiagnostics
    {
        get => _enableVerboseDiagnostics;
        set => SetField(ref _enableVerboseDiagnostics, value);
    }

    public bool RestartAppleMusicOnPlaybackFailure
    {
        get => _restartAppleMusicOnPlaybackFailure;
        set => SetField(ref _restartAppleMusicOnPlaybackFailure, value);
    }

    /// <summary>
    /// Persisted rate allow-list. Empty means unrestricted (all boxes checked). Updated only by
    /// user toggles — device roster churn never rewrites it.
    /// </summary>
    public IReadOnlyList<int> AllowedSampleRates => _allowedSampleRates;

    /// <summary>Persisted depth allow-list; same semantics as <see cref="AllowedSampleRates"/>.</summary>
    public IReadOnlyList<int> AllowedBitDepths => _allowedBitDepths;

    public bool HasFormatOptions => SampleRateOptions.Count > 0 || BitDepthOptions.Count > 0;

    public bool HasNoFormatOptions => !HasFormatOptions;

    public string OriginalFormatText
    {
        get => _originalFormatText;
        set => SetField(ref _originalFormatText, value);
    }

    public bool CanRestoreOriginalFormat
    {
        get => _canRestoreOriginalFormat;
        set
        {
            if (!SetField(ref _canRestoreOriginalFormat, value))
            {
                return;
            }

            RestoreOriginalFormatCommand.RaiseCanExecuteChanged();
        }
    }

    public bool UseFixedBitDepthSelection => !SwitchBitDepth;

    public bool IsSelectedDeviceSelectionEnabled => SelectedMode == DeviceSelectionMode.PinnedDevice;

    public bool IsSelectedDeviceSelectionVisible => SelectedMode == DeviceSelectionMode.PinnedDevice;

    public bool IsSwitchToastMetadataOptionVisible => EnableSwitchToasts;

    public bool HasUpdatePrimaryAction =>
        _updatePrimaryActionKind is not UpdateActionKind.None and not UpdateActionKind.OpenReleasesPage &&
        !string.IsNullOrWhiteSpace(UpdatePrimaryActionText);

    public void ReplaceDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(device);
        }
    }

    public void UpdateStatus(Services.SwitchingStatus status)
    {
        ResolverStatusText = status.ResolverStatusText;
        CurrentTrackText = status.Track is null
            ? "No track detected"
            : $"{status.Track.Title ?? "Unknown Title"} - {status.Track.Artist ?? "Unknown Artist"}";
        RequestedFormatText = status.RequestedFormat is null
            ? "-"
            : $"{AudioFormatTextFormatter.Format(status.RequestedFormat)} ({status.RequestedFormat.Source})";
        AppliedFormatText = status.AppliedFormat is null
            ? "-"
            : AudioFormatTextFormatter.Format(status.AppliedFormat);
        FailureReasonText = string.IsNullOrWhiteSpace(status.FailureReason) ? "-" : status.FailureReason;
    }

    public void UpdateActiveTargetCapabilities(CurrentTargetDeviceCapabilitiesSnapshot snapshot)
    {
        ActiveTargetDeviceNameText = snapshot.DeviceName ?? "No active target device";
        SupportedSampleRatesText = AudioFormatTextFormatter.BuildSupportedSampleRatesText(snapshot.SupportedFormats);
        SupportedBitDepthsText = AudioFormatTextFormatter.BuildSupportedBitDepthsText(snapshot.SupportedFormats);
        SupportedFormatsText = AudioFormatTextFormatter.BuildSupportedFormatsText(snapshot.SupportedFormats);
        SupportedFormatsDiagnosticsText = string.IsNullOrWhiteSpace(snapshot.ProbeDiagnostics) ? "-" : snapshot.ProbeDiagnostics;
        SyncFormatOptions(
            SampleRateOptions,
            snapshot.SupportedFormats.Select(format => format.SampleRateHz),
            _allowedSampleRates,
            AudioFormatTextFormatter.FormatSampleRate,
            OnSampleRateOptionChanged);
        SyncFormatOptions(
            BitDepthOptions,
            snapshot.SupportedFormats.Select(format => format.BitDepth),
            _allowedBitDepths,
            depth => $"{depth}-bit",
            OnBitDepthOptionChanged);
        OnPropertyChanged(nameof(HasFormatOptions));
        OnPropertyChanged(nameof(HasNoFormatOptions));
    }

    /// <summary>
    /// Seeds the persisted allow-lists from settings at startup, before any capability snapshot
    /// arrives, so the first checkbox roster reflects the stored configuration.
    /// </summary>
    public void SeedAllowedFormats(IReadOnlyList<int> allowedSampleRates, IReadOnlyList<int> allowedBitDepths)
    {
        _allowedSampleRates = allowedSampleRates.ToList();
        _allowedBitDepths = allowedBitDepths.ToList();
    }

    // Diff-based: this runs on every coordinator status change, so existing item instances (and
    // their checked state) must survive; only genuinely added/removed roster values change the
    // collection. Runs under the sync guard so programmatic IsChecked writes never persist.
    private void SyncFormatOptions(
        ObservableCollection<FormatOptionItem> options,
        IEnumerable<int> rosterValues,
        IReadOnlyCollection<int> configured,
        Func<int, string> formatLabel,
        PropertyChangedEventHandler onItemChanged)
    {
        _syncingFormatOptions = true;
        try
        {
            var desired = rosterValues.Where(value => value > 0).Distinct().OrderBy(value => value).ToList();
            for (var i = options.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(options[i].Value))
                {
                    options[i].PropertyChanged -= onItemChanged;
                    options.RemoveAt(i);
                }
            }

            for (var i = 0; i < desired.Count; i++)
            {
                if (i < options.Count && options[i].Value == desired[i])
                {
                    continue;
                }

                var isChecked = configured.Count == 0 || configured.Contains(desired[i]);
                var item = new FormatOptionItem(desired[i], formatLabel(desired[i]), isChecked);
                item.PropertyChanged += onItemChanged;
                options.Insert(i, item);
            }

            // A configuration written for different hardware can leave every shown box unchecked;
            // the policy treats that as unrestricted, so the display does too.
            if (options.Count > 0 && options.All(option => !option.IsChecked))
            {
                foreach (var option in options)
                {
                    option.IsChecked = true;
                }
            }
        }
        finally
        {
            _syncingFormatOptions = false;
        }
    }

    private void OnSampleRateOptionChanged(object? sender, PropertyChangedEventArgs e) =>
        HandleFormatOptionToggle(SampleRateOptions, sender as FormatOptionItem, ref _allowedSampleRates, nameof(AllowedSampleRates));

    private void OnBitDepthOptionChanged(object? sender, PropertyChangedEventArgs e) =>
        HandleFormatOptionToggle(BitDepthOptions, sender as FormatOptionItem, ref _allowedBitDepths, nameof(AllowedBitDepths));

    private void HandleFormatOptionToggle(
        ObservableCollection<FormatOptionItem> options,
        FormatOptionItem? item,
        ref List<int> configured,
        string persistencePropertyName)
    {
        if (_syncingFormatOptions || item is null)
        {
            return;
        }

        // At least one box stays checked: silently revert the uncheck that would empty the group.
        if (!item.IsChecked && options.All(option => !option.IsChecked))
        {
            _syncingFormatOptions = true;
            try
            {
                item.IsChecked = true;
            }
            finally
            {
                _syncingFormatOptions = false;
            }

            return;
        }

        // All boxes checked persists as empty = unrestricted, which stays correct if the device
        // later reports formats that are not on screen today.
        configured = options.All(option => option.IsChecked)
            ? []
            : options.Where(option => option.IsChecked).Select(option => option.Value).ToList();
        OnPropertyChanged(persistencePropertyName);
    }

    public void UpdateAppVersion(UpdateStatusSnapshot snapshot)
    {
        AppVersionText = $"Version {snapshot.CurrentVersion}";
        UpdateStatusText = snapshot.StatusText;
        _updatePrimaryActionKind = snapshot.PrimaryActionKind;
        UpdatePrimaryActionText = snapshot.PrimaryActionLabel;
        CanCheckForUpdates = snapshot.CanCheckForUpdates;
        CanRunUpdatePrimaryAction = snapshot.CanRunPrimaryAction &&
            snapshot.PrimaryActionKind is not UpdateActionKind.OpenReleasesPage;
        CanOpenReleasesPage = snapshot.CanOpenReleasesPage;
        OnPropertyChanged(nameof(HasUpdatePrimaryAction));
    }

    private bool SetField<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record DeviceSelectionModeOption(DeviceSelectionMode Mode, string DisplayName);

/// <summary>
/// One checkable sample-rate or bit-depth entry in the allowed-formats section.
/// </summary>
public sealed class FormatOptionItem : INotifyPropertyChanged
{
    private bool _isChecked;

    internal FormatOptionItem(int value, string label, bool isChecked)
    {
        Value = value;
        Label = label;
        _isChecked = isChecked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Value { get; }

    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}
