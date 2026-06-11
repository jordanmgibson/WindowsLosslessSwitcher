using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Reads the actual audio format of the local file backing a track, when one can be located.
/// </summary>
public interface ILocalTrackFileFormatReader
{
    /// <summary>
    /// Returns the format of the file backing <paramref name="track"/>, or null when no matching
    /// file can be found or read (missing cache, ambiguous metadata, or the file is locked).
    /// </summary>
    LocalTrackFileFormat? TryReadFormat(TrackSnapshot track);
}
