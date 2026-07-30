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
        // The compare-and-swap in TryApplyVerification only works against a resolver that does NOT
        // write to the store itself: a store-backed resolver would replace the entry during the
        // lookup, making every verification report "superseded" and updates silently stop surfacing.
        if (catalogResolver is AppleMusicCatalogResolver { WritesToCacheStore: true })
        {
            throw new ArgumentException(
                "The verification resolver must not write to the format cache store; construct it without one.",
                nameof(catalogResolver));
        }

        _store = store;
        _catalogResolver = catalogResolver;
        _logger = logger;
    }

    public static bool IsVerificationDue(FormatCacheEntry entry, int refreshDays) =>
        // A LastVerifiedAtUtc in the future means the entry was written under a wrong clock
        // (dead RTC before NTP sync); treat it as due rather than pinning it for decades.
        entry.LastVerifiedAtUtc > DateTimeOffset.UtcNow ||
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
                // Defer the retry to the next refresh window: without the touch, a delisted or
                // unmatchable track would re-verify on every single playback.
                _store.TryTouchVerification(entry, DateTimeOffset.UtcNow);
                _logger.Warn(
                    $"Format cache verification returned no result for {track.UniqueKey}; " +
                    "keeping the cached format and deferring the next attempt.");
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
