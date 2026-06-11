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

    /// <summary>
    /// Starts monitoring for track changes.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
}
