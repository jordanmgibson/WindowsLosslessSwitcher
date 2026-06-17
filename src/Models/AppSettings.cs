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

    public DeviceSelectionMode DeviceSelectionMode { get; set; } = DeviceSelectionMode.FollowDefault;

    public string? PinnedDeviceId { get; set; }

    public bool LaunchAtLogin { get; set; }

    public bool SwitchBitDepth { get; set; } = true;

    public bool PreferClosestSampleRateMultiple { get; set; }

    public int DefaultBitDepth
    {
        get => _defaultBitDepth;
        set => _defaultBitDepth = NormalizeBitDepth(value);
    }

    public bool EnableSwitchToasts { get; set; }

    public bool IncludeTrackMetadataInSwitchToasts { get; set; }

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
