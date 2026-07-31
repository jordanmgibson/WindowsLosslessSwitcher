namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Captures artwork bytes and revision metadata for the active media session.
/// <see cref="TrackUniqueKey"/> records which track the bytes were read for, so consumers can
/// refuse to show art that belongs to a different track (Windows' GSMTC thumbnail often lags a
/// track change and briefly serves the previous track's image).
/// </summary>
public sealed record MediaArtworkSnapshot(
    byte[]? Bytes,
    string? ContentType,
    string? Revision,
    DateTimeOffset ObservedAtUtc,
    string? TrackUniqueKey = null)
{
    /// <summary>
    /// Returns true when artwork bytes are available.
    /// </summary>
    public bool HasArtwork => Bytes is { Length: > 0 };

    /// <summary>
    /// Creates an empty snapshot representing missing artwork.
    /// </summary>
    public static MediaArtworkSnapshot CreateUnavailable() =>
        new(null, null, null, DateTimeOffset.UtcNow);
}
