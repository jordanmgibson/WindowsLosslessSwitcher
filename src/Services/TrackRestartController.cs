using System.Diagnostics;

namespace WindowsLosslessSwitcher.Services;

internal sealed record RestartPlaybackSnapshot(string TrackKey, TimeSpan? Position, bool CanSeek, bool CanPrevious, bool IsPaused);

/// <summary>Apple Music can acknowledge a seek without moving. Never treat that as a restart.</summary>
internal static class TrackRestartController
{
    internal static readonly TimeSpan SafePreviousPosition = TimeSpan.FromSeconds(5);

    internal static async Task<bool> TryRestartAsync(
        string expectedTrackKey,
        Func<CancellationToken, Task<RestartPlaybackSnapshot>> read,
        Func<CancellationToken, Task<bool>> seek,
        Func<CancellationToken, Task<bool>> previous,
        CancellationToken cancellationToken,
        TimeSpan? confirmationTimeout = null)
    {
        var timeout = confirmationTimeout ?? TimeSpan.FromMilliseconds(900);
        var snapshot = await read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(snapshot) || !snapshot.IsPaused) return false;
        if (AtStart(snapshot)) return true;

        if (snapshot.CanSeek)
        {
            if (await seek(cancellationToken) && await ConfirmAsync()) return true;
        }

        // Re-read after a slow/no-op seek: a user skip must never become Previous on the new song.
        snapshot = await read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(snapshot) || !snapshot.IsPaused) return false;
        if (AtStart(snapshot)) return true;
        if (!snapshot.CanPrevious || snapshot.Position is null || snapshot.Position < SafePreviousPosition) return false;
        return await previous(cancellationToken) && await ConfirmAsync();

        bool Matches(RestartPlaybackSnapshot value) => value.TrackKey == expectedTrackKey;
        static bool AtStart(RestartPlaybackSnapshot value) => value.Position is { } position && position >= TimeSpan.Zero && position <= TimeSpan.FromMilliseconds(500);

        async Task<bool> ConfirmAsync()
        {
            var elapsed = Stopwatch.StartNew();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await read(cancellationToken);
                if (!Matches(current) || !current.IsPaused) return false;
                if (AtStart(current)) return true;
                if (elapsed.Elapsed >= timeout) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            } while (true);
        }
    }
}
