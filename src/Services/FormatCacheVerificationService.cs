using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Verifies stale catalog cache entries in the background and updates the cache
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

            var formatChanged = fresh.SampleRateHz != entry.SampleRateHz || fresh.BitDepth != entry.BitDepth;
            if (!_store.TryApplyVerification(entry, fresh, out var cacheGeneration))
            {
                _logger.Info($"Format cache verification result was superseded for {track.UniqueKey}.");
                return null;
            }

            if (!formatChanged)
            {
                _logger.Info(
                    $"Format cache verification confirmed {entry.BitDepth}/{entry.SampleRateHz} for {track.UniqueKey}.");
                return null;
            }

            _logger.Info(
                $"Format cache verification updated {track.UniqueKey}: " +
                $"{entry.BitDepth}/{entry.SampleRateHz} -> {fresh.BitDepth}/{fresh.SampleRateHz} (applies next playback).");
            return new FormatCacheUpdateEventArgs(track, entry, fresh, cacheGeneration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"Format cache verification failed for {track.UniqueKey}: {ex.Message}");
            return null;
        }
    }
}
