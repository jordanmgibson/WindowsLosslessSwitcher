using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed record SwitchingStatus(
    string ResolverStatusText,
    string? ActiveDeviceName,
    TrackSnapshot? Track,
    ResolvedAudioFormat? RequestedFormat,
    AudioFormatCandidate? AppliedFormat,
    string? FailureReason,
    bool WasFormatChanged = false);
