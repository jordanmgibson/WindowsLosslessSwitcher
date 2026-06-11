using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Resolves the target playback format for an Apple Music track.
/// </summary>
public interface IFormatResolver
{
    /// <summary>
    /// Gets a human-readable resolver name for diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Attempts to resolve the output format for the supplied track.
    /// </summary>
    Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken);
}
