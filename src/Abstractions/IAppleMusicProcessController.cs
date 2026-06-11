namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Restarts the Apple Music app process. Used as the last recovery escalation: once Apple Music's
/// renderer enters its degraded state (consecutive tracks wedging, pause/play nudges and track
/// changes all failing to restore audio), restarting the app is the only cure observed.
/// </summary>
public interface IAppleMusicProcessController
{
    /// <summary>
    /// Gracefully closes and relaunches Apple Music. Returns true once the relaunched process is
    /// running; the caller is responsible for waiting for the media session and resuming playback.
    /// </summary>
    Task<bool> TryRestartAsync(CancellationToken cancellationToken);
}
