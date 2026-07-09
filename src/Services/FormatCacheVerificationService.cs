using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Verifies stale catalog cache entries in the background and updates the database
/// if the Apple Music format has changed.
/// </summary>
internal sealed class FormatCacheVerificationService
{
    private readonly FormatCacheStore _store;
    private readonly IFormatResolver _catalogResolver;
    private readonly DiagnosticsLogger _logger;

    public FormatCacheVerificationService(
        FormatCacheStore store,
        IFormatResolver catalogResolver,
        DiagnosticsLogger logger)
    {
        _store = store;
        _catalogResolver = catalogResolver;
        _logger = logger;
    }

    public static bool IsVerificationDue(FormatCacheEntry entry, int refreshDays) =>
        DateTimeOffset.UtcNow - entry.LastVerifiedAtUtc >= TimeSpan.FromDays(refreshDays);

    public async Task<FormatCacheUpdateEventArgs?> VerifyAsync(
        TrackSnapshot track,
        FormatCacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await _catalogResolver.ResolveAsync(track, cancellationToken);
            if (fresh is null)
            {
                _logger.Warn($"Format cache verification returned no result for {track.UniqueKey}.");
                return null;
            }

            if (fresh.SampleRateHz == entry.SampleRateHz && fresh.BitDepth == entry.BitDepth)
            {
                _store.TouchLastVerified(track.UniqueKey, DateTimeOffset.UtcNow);
                _logger.Info(
                    $"Format cache verification confirmed {entry.BitDepth}/{entry.SampleRateHz} for {track.UniqueKey}.");
                return null;
            }

            _store.UpdateEntry(track.UniqueKey, fresh);
            _logger.Info(
                $"Format cache verification updated {track.UniqueKey}: " +
                $"{entry.BitDepth}/{entry.SampleRateHz} -> {fresh.BitDepth}/{fresh.SampleRateHz} (applies next playback).");
            return new FormatCacheUpdateEventArgs(track, entry, fresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"Format cache verification failed for {track.UniqueKey}: {ex.Message}");
            return null;
        }
    }
}