using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Exposes playback transport controls for the active media session.
/// </summary>
public interface IMediaTransportController
{
    /// <summary>
    /// Returns a snapshot of the current session's transport state, or an
    /// unavailable state when no media session is active.
    /// </summary>
    MediaTransportState GetPlaybackState();

    /// <summary>
    /// Attempts to pause playback. Returns false when no session is active
    /// or the session does not currently allow pausing.
    /// </summary>
    Task<bool> TryPauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to resume playback. Returns false when no session is active
    /// or the session does not currently allow playing.
    /// </summary>
    Task<bool> TryPlayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to toggle between play and pause. Returns false when no session
    /// is active or the session does not support the toggle command.
    /// </summary>
    Task<bool> TryTogglePlayPauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to seek the timeline to <paramref name="position"/>. Returns false
    /// when no session is active or the session does not allow seeking.
    /// </summary>
    Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to skip to the next track. Returns false when no session is
    /// active or the session does not currently allow skipping forward.
    /// </summary>
    Task<bool> TrySkipNextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to skip to the previous track. Returns false when no session is
    /// active or the session does not currently allow skipping back.
    /// </summary>
    Task<bool> TrySkipPreviousAsync(CancellationToken cancellationToken);
}
