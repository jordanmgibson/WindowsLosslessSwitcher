namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Captures the track metadata observed from Apple Music at a specific time.
/// </summary>
public sealed record TrackSnapshot(
    string SourceAppUserModelId,
    string? TrackId,
    string? Title,
    string? Artist,
    string? Album,
    string DetectionReason,
    DateTimeOffset DetectedAtUtc)
{
    /// <summary>
    /// Gets a stable key used to de-duplicate track processing.
    /// </summary>
    public string UniqueKey =>
        string.Join(
            "|",
            SourceAppUserModelId,
            TrackId ?? string.Empty,
            Title ?? string.Empty,
            Artist ?? string.Empty,
            Album ?? string.Empty);
}
