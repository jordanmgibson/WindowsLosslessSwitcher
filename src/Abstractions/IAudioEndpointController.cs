using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Provides access to Windows render devices and mix-format management.
/// </summary>
public interface IAudioEndpointController
{
    /// <summary>
    /// Returns the current list of render devices.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetRenderDevices();

    /// <summary>
    /// Returns the default render device, if one exists.
    /// </summary>
    AudioDeviceInfo? GetDefaultRenderDevice();

    /// <summary>
    /// Returns the currently applied shared-mode device format.
    /// </summary>
    AudioFormatCandidate? GetCurrentDeviceFormat(string deviceId);

    /// <summary>
    /// Returns the current mix format reported by Core Audio.
    /// </summary>
    AudioFormatCandidate? GetCurrentMixFormat(string deviceId);

    /// <summary>
    /// Returns the supported shared-mode formats for the target device.
    /// </summary>
    IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string deviceId, bool forceRefresh = false);

    /// <summary>
    /// Returns human-readable diagnostics for the supplied format.
    /// </summary>
    string? DescribeSupportedFormat(string deviceId, AudioFormatCandidate format);

    /// <summary>
    /// Returns diagnostics from the last apply attempt for the device.
    /// </summary>
    string? GetLastApplyDiagnostics(string deviceId);

    /// <summary>
    /// Returns the current master peak value for playback activity checks.
    /// </summary>
    float? GetMasterPeakValue(string deviceId);

    /// <summary>
    /// Returns whether the named process currently has an active (running) audio session on the
    /// device, or null when session state cannot be read.
    /// </summary>
    bool? IsProcessSessionActive(string deviceId, string processName);

    /// <summary>
    /// Returns the peak meter value of the named process's own audio sessions on the device
    /// (the maximum across its sessions), 0 when the process has no audible session, or null
    /// when session state cannot be read. Unlike the endpoint master peak, this excludes audio
    /// from other applications and reads upstream of the endpoint mute.
    /// </summary>
    float? GetProcessSessionPeak(string deviceId, string processName);

    /// <summary>
    /// Returns whether the endpoint is currently muted, or null when the state cannot be read.
    /// </summary>
    bool? GetMasterMute(string deviceId);

    /// <summary>
    /// Attempts to set the endpoint master mute state.
    /// </summary>
    bool TrySetMasterMute(string deviceId, bool muted);

    /// <summary>
    /// Attempts to apply the supplied format and verifies the resulting format.
    /// </summary>
    bool TryApplyFormat(string deviceId, AudioFormatCandidate format, out AudioFormatCandidate? verifiedDeviceFormat, out string failureReason);
}
