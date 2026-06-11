namespace WindowsLosslessSwitcher.Models;

public enum MediaTransportPlaybackStatus
{
    Unknown,
    Closed,
    Stopped,
    Changing,
    Paused,
    Playing,
}

public sealed record MediaTransportState(
    MediaTransportPlaybackStatus PlaybackStatus,
    bool CanPause,
    bool CanPlay,
    TimeSpan? TimelinePosition,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsPlaying => PlaybackStatus == MediaTransportPlaybackStatus.Playing;

    public static MediaTransportState CreateUnavailable() =>
        new(MediaTransportPlaybackStatus.Unknown, false, false, null, DateTimeOffset.UtcNow);
}
