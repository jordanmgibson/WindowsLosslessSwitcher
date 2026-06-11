namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Captures the latest media-session state exposed to transport controls.
/// </summary>
public sealed record MediaSessionSnapshot(
    string SourceAppUserModelId,
    string? Title,
    string? Artist,
    string? Album,
    MediaTransportPlaybackStatus PlaybackStatus,
    bool CanPause,
    bool CanPlay,
    bool CanGoNext,
    bool CanGoPrevious,
    bool CanShuffle,
    bool CanRepeat,
    bool? IsShuffleActive,
    MediaTransportRepeatMode RepeatMode,
    string? ArtworkRevision,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>
    /// Returns true when the current session can be toggled between play and pause.
    /// </summary>
    public bool CanTogglePlayPause => CanPause || CanPlay;

    /// <summary>
    /// Creates an empty snapshot representing an unavailable session.
    /// </summary>
    public static MediaSessionSnapshot CreateUnavailable() =>
        new(
            string.Empty,
            null,
            null,
            null,
            MediaTransportPlaybackStatus.Unknown,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            MediaTransportRepeatMode.Unknown,
            null,
            DateTimeOffset.UtcNow);
}
