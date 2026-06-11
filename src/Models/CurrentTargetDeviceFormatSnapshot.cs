namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Describes the currently selected target device and its active format.
/// </summary>
public sealed record CurrentTargetDeviceFormatSnapshot(
    string? DeviceId,
    string? DeviceName,
    AudioFormatCandidate? Format);
