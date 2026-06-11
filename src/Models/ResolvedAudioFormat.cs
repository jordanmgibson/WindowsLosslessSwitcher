namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Identifies the source used to resolve an audio format.
/// </summary>
public enum AudioFormatSource
{
    CatalogManifest,
    LocalFile,
    TierFallback,
}

/// <summary>
/// Represents how trustworthy a resolved format is.
/// </summary>
public enum ResolutionConfidence
{
    Exact,
    Tier,
}

/// <summary>
/// Represents a resolved format plus the metadata that explains how it was chosen.
/// </summary>
public sealed record ResolvedAudioFormat(
    int SampleRateHz,
    int BitDepth,
    ResolutionConfidence Confidence,
    AudioFormatSource Source,
    string Description)
{
    /// <summary>
    /// Gets the observation timestamp associated with the resolved source, when available.
    /// </summary>
    public DateTimeOffset? ObservedAtUtc { get; init; }
}
