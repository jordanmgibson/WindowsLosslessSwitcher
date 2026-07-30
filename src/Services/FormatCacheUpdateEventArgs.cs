using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class FormatCacheUpdateEventArgs : EventArgs
{
    public FormatCacheUpdateEventArgs(
        TrackSnapshot track,
        FormatCacheEntry previousEntry,
        ResolvedAudioFormat updatedFormat,
        long cacheGeneration)
    {
        Track = track;
        PreviousEntry = previousEntry;
        UpdatedFormat = updatedFormat;
        CacheGeneration = cacheGeneration;
    }

    public TrackSnapshot Track { get; }

    public FormatCacheEntry PreviousEntry { get; }

    public ResolvedAudioFormat UpdatedFormat { get; }

    internal long CacheGeneration { get; }
}
