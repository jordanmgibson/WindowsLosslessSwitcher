namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Describes the target device and the formats it reports as supported.
/// </summary>
public sealed record CurrentTargetDeviceCapabilitiesSnapshot(
    string? DeviceName,
    IReadOnlyList<AudioFormatCandidate> SupportedFormats);
