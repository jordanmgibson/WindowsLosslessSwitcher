using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Color = System.Windows.Media.Color;

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
    private ImageSource? _artworkImage;
    private Color _artworkGlowColor = Color.FromRgb(0x42, 0x3A, 0x6A);
    private string _trackTitleText = "Nothing playing";
    private string _trackMetaText = "Waiting for Apple Music";
    private SwitchPhase _phase = SwitchPhase.Idle;
    private string _phaseText = "Watching Apple Music media session";
    private bool _isBusyPhase;
    private string _requestedFormatDisplayText = "—";
    private string _appliedFormatDisplayText = "—";
    private string _sourceChipText = "";
    private string _sourceRawText = "—";
    private string _wasLine = "";
    private string _currentDeviceFormatText = "Unknown";
    private string _updateBadgeText = "Up to date";
    private AudioFormatCandidate? _previousAppliedFormat;
    private AudioFormatCandidate? _lastAppliedFormat;
    private string? _detectedStorefront;
    private string? _lastActivityText;

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
        OpenAllSettingsCommand = new RelayCommand(() => OpenSettingsWindowRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? RefreshRequested;

    public event Action? ExportDiagnosticsRequested;

    public event Action? CheckForUpdatesRequested;

    public event Action? RunUpdatePrimaryActionRequested;

    public event Action? OpenReleasesPageRequested;

    public event Action? RestoreOriginalFormatRequested;

    public event Action? ClearFormatCacheRequested;

    public event Action? OpenSettingsWindowRequested;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public ObservableCollection<ActivityLogEntry> ActivityLog { get; } = [];

    public ObservableCollection<StatusChipItem> StatusRateChips { get; } = [];

    public ObservableCollection<StatusChipItem> StatusBitChips { get; } = [];

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

    public RelayCommand OpenAllSettingsCommand { get; }

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
            OnPropertyChanged(nameof(DeviceModeChipText));
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
        set
        {
            if (!SetField(
                ref _appleMusicStorefront,
                string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant()))
            {
                return;
            }

            OnPropertyChanged(nameof(StorefrontNoteText));
        }
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

    // ── Nocturne surface state ────────────────────────────────────────────────────────────────

    /// <summary>Decoded, frozen album artwork for the current track, or null for placeholder.</summary>
    public ImageSource? ArtworkImage
    {
        get => _artworkImage;
        private set
        {
            if (SetField(ref _artworkImage, value))
            {
                OnPropertyChanged(nameof(HasArtwork));
            }
        }
    }

    public bool HasArtwork => _artworkImage is not null;

    /// <summary>Dominant artwork hue used for the hero glow and placeholder gradient.</summary>
    public Color ArtworkGlowColor
    {
        get => _artworkGlowColor;
        private set => SetField(ref _artworkGlowColor, value);
    }

    public string TrackTitleText
    {
        get => _trackTitleText;
        private set => SetField(ref _trackTitleText, value);
    }

    public string TrackMetaText
    {
        get => _trackMetaText;
        private set => SetField(ref _trackMetaText, value);
    }

    public SwitchPhase Phase
    {
        get => _phase;
        private set => SetField(ref _phase, value);
    }

    public string PhaseText
    {
        get => _phaseText;
        private set => SetField(ref _phaseText, value);
    }

    /// <summary>True while detecting/resolving/switching — drives the pulsing status dot.</summary>
    public bool IsBusyPhase
    {
        get => _isBusyPhase;
        private set => SetField(ref _isBusyPhase, value);
    }

    /// <summary>Requested format without the source suffix, for the stat cards ("—" when unknown).</summary>
    public string RequestedFormatDisplayText
    {
        get => _requestedFormatDisplayText;
        private set => SetField(ref _requestedFormatDisplayText, value);
    }

    public string AppliedFormatDisplayText
    {
        get => _appliedFormatDisplayText;
        private set => SetField(ref _appliedFormatDisplayText, value);
    }

    /// <summary>"Catalog match" / "Local file" / "Fallback", or empty to hide the chip.</summary>
    public string SourceChipText
    {
        get => _sourceChipText;
        private set => SetField(ref _sourceChipText, value);
    }

    /// <summary>Raw <see cref="AudioFormatSource"/> name for the details card.</summary>
    public string SourceRawText
    {
        get => _sourceRawText;
        private set => SetField(ref _sourceRawText, value);
    }

    /// <summary>"was 24-bit / 44.1 kHz" or "format carried over unchanged" under the hero readout.</summary>
    public string WasLine
    {
        get => _wasLine;
        private set => SetField(ref _wasLine, value);
    }

    /// <summary>The applied format before the most recent switch — feeds the toast old→new row.</summary>
    public AudioFormatCandidate? PreviousAppliedFormat
    {
        get => _previousAppliedFormat;
        private set => SetField(ref _previousAppliedFormat, value);
    }

    /// <summary>Bare current device format ("24-bit / 192 kHz") for the tray flyout chip.</summary>
    public string CurrentDeviceFormatText
    {
        get => _currentDeviceFormatText;
        private set => SetField(ref _currentDeviceFormatText, value);
    }

    public string UpdateBadgeText
    {
        get => _updateBadgeText;
        private set => SetField(ref _updateBadgeText, value);
    }

    public string DeviceModeChipText =>
        SelectedMode == DeviceSelectionMode.PinnedDevice ? "Pinned" : "Follows default";

    public string AllowedFormatsNoteText =>
        SampleRateOptions.All(option => option.IsChecked) && BitDepthOptions.All(option => option.IsChecked)
            ? "Everything checked — no restriction applied."
            : "Resolved formats are clamped to the checked set, preferring the same 44.1 / 48 family. At least one of each stays enabled.";

    public string StorefrontNoteText =>
        AppleMusicStorefront is { Length: > 0 } storefront
            ? $"Override: “{storefront}”"
            : $"Detected from the Windows region: {_detectedStorefront ?? "auto"}";

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

        TrackTitleText = status.Track?.Title is { Length: > 0 } title ? title : "Nothing playing";
        TrackMetaText = BuildTrackMetaText(status.Track);
        RequestedFormatDisplayText = status.RequestedFormat is null
            ? "—"
            : AudioFormatTextFormatter.Format(status.RequestedFormat);
        SourceChipText = BuildSourceChipText(status.RequestedFormat?.Source);
        SourceRawText = status.RequestedFormat?.Source.ToString() ?? "—";

        var phase = DerivePhase(status);
        Phase = phase;
        PhaseText = BuildPhaseText(phase, status);
        IsBusyPhase = phase is SwitchPhase.Detecting or SwitchPhase.Resolving or SwitchPhase.Switching;

        if (status.AppliedFormat is not null)
        {
            AppliedFormatDisplayText = AudioFormatTextFormatter.Format(status.AppliedFormat);

            // Previous-format bookkeeping must only advance on terminal statuses: intermediate
            // "Switching: Waiting for device" updates already carry the NEW format and would
            // otherwise erase the old format before "Playback restored" lands.
            if (phase is SwitchPhase.Restored or SwitchPhase.NoChange)
            {
                if (status.WasFormatChanged &&
                    _lastAppliedFormat is not null &&
                    _lastAppliedFormat != status.AppliedFormat)
                {
                    PreviousAppliedFormat = _lastAppliedFormat;
                    WasLine = $"was {AudioFormatTextFormatter.Format(_lastAppliedFormat)}";
                }
                else if (phase == SwitchPhase.NoChange)
                {
                    WasLine = "format carried over unchanged";
                }

                if (_lastAppliedFormat != status.AppliedFormat)
                {
                    _lastAppliedFormat = status.AppliedFormat;
                    RebuildStatusChips();
                }
            }
        }

        RecordActivity(phase, status);
    }

    /// <summary>
    /// Derives the friendly phase from the coordinator's raw status text plus the payload —
    /// terminal "Resolver: {Source}" statuses read as no-change when a format is applied and as
    /// failures when only a failure reason is present.
    /// </summary>
    internal static SwitchPhase DerivePhase(Services.SwitchingStatus status)
    {
        var text = status.ResolverStatusText;
        if (text.StartsWith("Switching: Playback restored", StringComparison.Ordinal))
        {
            return SwitchPhase.Restored;
        }

        if (text.StartsWith("Switching:", StringComparison.Ordinal))
        {
            return SwitchPhase.Switching;
        }

        return text switch
        {
            "Resolver: Waiting for Apple Music" or "Resolver: Starting" or "Resolver: Idle" => SwitchPhase.Idle,
            "Resolver: Detecting track" => SwitchPhase.Detecting,
            "Resolver: Resolving format" => SwitchPhase.Resolving,
            "Resolver: Error" or "Resolver: Failed" or "Resolver: No target device" => SwitchPhase.Failed,
            _ when text.StartsWith("Resolver: ", StringComparison.Ordinal) =>
                status.AppliedFormat is not null
                    ? SwitchPhase.NoChange
                    : string.IsNullOrWhiteSpace(status.FailureReason) ? SwitchPhase.Idle : SwitchPhase.Failed,
            _ => SwitchPhase.Idle,
        };
    }

    internal static string BuildPhaseText(SwitchPhase phase, Services.SwitchingStatus status) => phase switch
    {
        SwitchPhase.Idle => "Watching Apple Music media session",
        SwitchPhase.Detecting => "Track change — output muted",
        SwitchPhase.Resolving => "Matching in Apple Music catalog…",
        SwitchPhase.Switching when status.ResolverStatusText.Contains("Restarting Apple Music", StringComparison.Ordinal) =>
            "Restarting Apple Music…",
        SwitchPhase.Switching when status.ResolverStatusText.Contains("Recovering", StringComparison.Ordinal) =>
            "Recovering playback…",
        SwitchPhase.Switching => "Switching device format…",
        SwitchPhase.Restored => "Playback restored",
        SwitchPhase.NoChange => "No switch needed — format already matches",
        SwitchPhase.Failed => string.IsNullOrWhiteSpace(status.FailureReason)
            ? "Something went wrong — see diagnostics"
            : status.FailureReason!,
        _ => "Watching Apple Music media session",
    };

    private static string BuildTrackMetaText(TrackSnapshot? track)
    {
        if (track is null)
        {
            return "Waiting for Apple Music";
        }

        var artist = track.Artist;
        var album = track.Album;
        if (artist is { Length: > 0 } && album is { Length: > 0 })
        {
            return $"{artist} — {album}";
        }

        return artist is { Length: > 0 } ? artist : album is { Length: > 0 } ? album : "Unknown artist";
    }

    private static string BuildSourceChipText(AudioFormatSource? source) => source switch
    {
        AudioFormatSource.CatalogManifest or AudioFormatSource.CachedCatalog => "Catalog match",
        AudioFormatSource.LocalFile => "Local file",
        AudioFormatSource.TierFallback => "Fallback",
        _ => "",
    };

    private void RecordActivity(SwitchPhase phase, Services.SwitchingStatus status)
    {
        var formatText = status.AppliedFormat is null ? null : AudioFormatTextFormatter.Format(status.AppliedFormat);
        var title = status.Track?.Title;
        var text = phase switch
        {
            SwitchPhase.Restored when status.WasFormatChanged && formatText is not null =>
                title is { Length: > 0 }
                    ? $"Switched to {formatText} for “{title}”"
                    : $"Switched to {formatText}",
            SwitchPhase.NoChange when formatText is not null && title is { Length: > 0 } =>
                $"“{title}” — format already matched ({formatText})",
            SwitchPhase.Failed when !string.IsNullOrWhiteSpace(status.FailureReason) =>
                $"Failed: {status.FailureReason}",
            _ => null,
        };

        // Coordinator statuses can republish; only genuinely new lines belong in the log.
        if (text is null || text == _lastActivityText)
        {
            return;
        }

        _lastActivityText = text;
        AddActivity(text);
    }

    public void AddActivity(string text) => AddActivity(text, DateTimeOffset.Now);

    public void AddActivity(string text, DateTimeOffset timestamp)
    {
        ActivityLog.Insert(0, new ActivityLogEntry(timestamp.ToString("HH:mm"), text));
        while (ActivityLog.Count > 8)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }
    }

    /// <summary>Called by the composition root with the decoded artwork (or null to clear).</summary>
    public void UpdateArtwork(ImageSource? image, Color? glowColor)
    {
        ArtworkImage = image;
        ArtworkGlowColor = glowColor ?? Color.FromRgb(0x42, 0x3A, 0x6A);
    }

    /// <summary>
    /// Called by the composition root with the current target-device format. Also seeds the
    /// previous-format bookkeeping so the first switch after launch has an honest "was …" line.
    /// </summary>
    public void UpdateCurrentDeviceFormat(AudioFormatCandidate? format)
    {
        CurrentDeviceFormatText = AudioFormatTextFormatter.FormatOrUnknown(format);
        _lastAppliedFormat ??= format;
    }

    /// <summary>Seeds the storefront the catalog resolver detected/uses (for the cache page note).</summary>
    public void SeedStorefrontInfo(string? detectedStorefront)
    {
        _detectedStorefront = detectedStorefront;
        OnPropertyChanged(nameof(StorefrontNoteText));
    }

    private void RebuildStatusChips()
    {
        StatusRateChips.Clear();
        foreach (var option in SampleRateOptions)
        {
            StatusRateChips.Add(new StatusChipItem(
                option.Label,
                _lastAppliedFormat?.SampleRateHz == option.Value,
                option.IsChecked));
        }

        StatusBitChips.Clear();
        foreach (var option in BitDepthOptions)
        {
            StatusBitChips.Add(new StatusChipItem(
                option.Label,
                _lastAppliedFormat?.BitDepth == option.Value,
                option.IsChecked));
        }
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
        OnPropertyChanged(nameof(AllowedFormatsNoteText));
        RebuildStatusChips();
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
        OnPropertyChanged(nameof(AllowedFormatsNoteText));
        RebuildStatusChips();
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
        UpdateBadgeText = HasUpdatePrimaryAction ||
            (snapshot.LatestVersion is { Length: > 0 } latest &&
             !string.Equals(latest, snapshot.CurrentVersion, StringComparison.OrdinalIgnoreCase))
            ? "Update available"
            : "Up to date";
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

/// <summary>One line in the Status page activity list.</summary>
public sealed record ActivityLogEntry(string TimeText, string Text);

/// <summary>
/// A read-only chip on the Status page: lit when it is the currently applied value, dimmed with
/// strikethrough when the allow-list excludes it.
/// </summary>
public sealed record StatusChipItem(string Label, bool IsActive, bool IsAllowed);

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
