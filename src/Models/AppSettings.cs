using System.Text.Json.Serialization;

namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Controls how the app selects devices and applies format changes.
/// </summary>
public enum DeviceSelectionMode
{
    FollowDefault,
    PinnedDevice,
}

/// <summary>
/// Represents the persisted user settings for Windows Lossless Switcher.
/// </summary>
public sealed class AppSettings
{
    private int _defaultBitDepth = 24;
    private int _formatCacheRefreshDays = 30;
    private List<int> _allowedSampleRates = [];
    private List<int> _allowedBitDepths = [];

    public DeviceSelectionMode DeviceSelectionMode { get; set; } = DeviceSelectionMode.FollowDefault;

    public string? PinnedDeviceId { get; set; }

    public bool LaunchAtLogin { get; set; }

    public bool SwitchBitDepth { get; set; } = true;

    public bool PreferClosestSampleRateMultiple { get; set; }

    /// <summary>
    /// Optional allow-list of sample rates (Hz) the physical hardware accepts. When non-empty,
    /// resolved rates are clamped to this list before being applied — for chains where a virtual
    /// cable or bridge between the switched endpoint and the real DAC reports capabilities the
    /// hardware does not have. Empty (the default) leaves rate selection unrestricted.
    /// </summary>
    public List<int> AllowedSampleRates
    {
        get => _allowedSampleRates;
        set => _allowedSampleRates = value is null
            ? []
            : value.Where(rate => rate > 0).Distinct().OrderBy(rate => rate).ToList();
    }

    /// <summary>
    /// Optional allow-list of bit depths the physical hardware accepts; companion to
    /// <see cref="AllowedSampleRates"/>. Not clamped to 16/24 like <see cref="DefaultBitDepth"/> —
    /// devices legitimately report 32-bit. Empty (the default) leaves depth selection unrestricted.
    /// </summary>
    public List<int> AllowedBitDepths
    {
        get => _allowedBitDepths;
        set => _allowedBitDepths = value is null
            ? []
            : value.Where(depth => depth > 0).Distinct().OrderBy(depth => depth).ToList();
    }

    public int DefaultBitDepth
    {
        get => _defaultBitDepth;
        set => _defaultBitDepth = NormalizeBitDepth(value);
    }

    public bool EnableSwitchToasts { get; set; }

    public bool IncludeTrackMetadataInSwitchToasts { get; set; }

    /// <summary>
    /// Re-checks cached catalog formats in the background after this many days. Must be one of
    /// <see cref="SupportedFormatCacheRefreshDays"/>; other values snap to the nearest preset.
    /// The current track keeps playing at the cached format; updates apply on the next playback.
    /// </summary>
    public int FormatCacheRefreshDays
    {
        get => _formatCacheRefreshDays;
        set => _formatCacheRefreshDays = SnapToNearestRefreshDaysPreset(value);
    }

    public OriginalTargetSnapshot? OriginalTarget { get; set; }

    public bool EnableVerboseDiagnostics { get; set; } = true;

    /// <summary>
    /// Optional Apple Music storefront (two-letter region code, e.g. "us", "gb") used for catalog
    /// lookups. When null/blank the storefront is detected from the OS region, falling back to "us".
    /// </summary>
    public string? AppleMusicStorefront { get; set; }

    /// <summary>
    /// Last-resort recovery: when playback wedges repeatedly and every lighter recovery fails,
    /// restart Apple Music automatically. The play queue/position may reset, but the alternative
    /// is silence until the user restarts Apple Music themselves.
    /// </summary>
    public bool RestartAppleMusicOnPlaybackFailure { get; set; } = true;

    [JsonIgnore]
    public string? SettingsPath { get; set; }

    /// <summary>
    /// Normalizes unsupported bit depths to the app default.
    /// </summary>
    public static int NormalizeBitDepth(int bitDepth) =>
        bitDepth is 16 or 24 ? bitDepth : 24;

    /// <summary>
    /// Preset refresh intervals exposed in settings. Arbitrary day counts are not supported.
    /// </summary>
    public static IReadOnlyList<int> SupportedFormatCacheRefreshDays { get; } =
        [1, 7, 14, 30, 60, 90, 180, 365];

    public const int DefaultFormatCacheRefreshDays = 30;

    /// <summary>
    /// Snaps unsupported refresh intervals to the nearest supported preset.
    /// </summary>
    public static int SnapToNearestRefreshDaysPreset(int days)
    {
        if (SupportedFormatCacheRefreshDays.Contains(days))
        {
            return days;
        }

        // Distance in long: with an int, days near int.MinValue (a corrupt settings file) overflows
        // Math.Abs and inverts the snap.
        return SupportedFormatCacheRefreshDays
            .OrderBy(option => Math.Abs((long)option - days))
            .ThenBy(option => option)
            .First();
    }
}

/// <summary>
/// Captures the original target device format so it can be restored later.
/// </summary>
public sealed record OriginalTargetSnapshot(
    string DeviceId,
    string? DeviceName,
    int SampleRateHz,
    int BitDepth,
    int Channels,
    DateTimeOffset CapturedAtUtc);
