using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Publishes track snapshots from the currently playing Apple Music session.
/// </summary>
public interface ITrackSource : IAsyncDisposable
{
    /// <summary>
    /// Raised when a new track snapshot becomes available.
    /// </summary>
    event EventHandler<TrackSnapshot>? TrackChanged;

    /// <summary>Signals a resume even when the recording metadata has not changed.</summary>
    event EventHandler? PlaybackResumed { add { } remove { } }

    /// <summary>
    /// Starts monitoring for track changes.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
}
