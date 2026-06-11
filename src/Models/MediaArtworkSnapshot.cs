namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Captures artwork bytes and revision metadata for the active media session.
/// </summary>
public sealed record MediaArtworkSnapshot(
    byte[]? Bytes,
    string? ContentType,
    string? Revision,
    DateTimeOffset ObservedAtUtc)
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
