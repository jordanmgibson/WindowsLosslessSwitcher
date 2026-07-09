namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Represents a persisted catalog format cache row.
/// </summary>
public sealed record FormatCacheEntry(
    string UniqueKey,
    string? CatalogSongId,
    int SampleRateHz,
    int BitDepth,
    ResolutionConfidence Confidence,
    string Description,
    DateTimeOffset CachedAtUtc,
    DateTimeOffset LastVerifiedAtUtc)
{
    public ResolvedAudioFormat ToResolvedFormat() =>
        new(
            SampleRateHz,
            BitDepth,
            Confidence,
            AudioFormatSource.CachedCatalog,
            Description)
        {
            CatalogSongId = CatalogSongId,
            ObservedAtUtc = LastVerifiedAtUtc,
        };
}