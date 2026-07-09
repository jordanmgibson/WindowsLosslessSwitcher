using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class FormatCacheUpdateEventArgs : EventArgs
{
    public FormatCacheUpdateEventArgs(
        TrackSnapshot track,
        FormatCacheEntry previousEntry,
        ResolvedAudioFormat updatedFormat)
    {
        Track = track;
        PreviousEntry = previousEntry;
        UpdatedFormat = updatedFormat;
    }

    public TrackSnapshot Track { get; }

    public FormatCacheEntry PreviousEntry { get; }

    public ResolvedAudioFormat UpdatedFormat { get; }
}