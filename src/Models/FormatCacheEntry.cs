namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Represents a persisted catalog format cache entry. A <see cref="NoMatch"/> entry records that
/// the catalog lookup found nothing usable for this track (a local file, or a track without a
/// lossless manifest) so replays skip the catalog search entirely; its format fields are zero and
/// it never resolves to a format. Older builds skip NoMatch entries harmlessly during validation
/// (zero sample rate fails their per-entry check).
/// </summary>
public sealed record FormatCacheEntry(
    string UniqueKey,
    string? CatalogSongId,
    int SampleRateHz,
    int BitDepth,
    ResolutionConfidence Confidence,
    string Description,
    DateTimeOffset CachedAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    bool NoMatch = false)
{
    public ResolvedAudioFormat ToResolvedFormat() =>
        NoMatch
            ? throw new InvalidOperationException("A no-match cache entry carries no format.")
            : new(
            SampleRateHz,
            BitDepth,
            Confidence,
            AudioFormatSource.CachedCatalog,
            Description)
        {
            CatalogSongId = CatalogSongId,
            ObservedAtUtc = LastVerifiedAtUtc,
            CachedCatalogEntry = this,
        };
}
