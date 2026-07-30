using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Returns persisted catalog formats before slower network and local-file resolvers run.
/// </summary>
public sealed class FormatCacheResolver : IFormatResolver
{
    private readonly FormatCacheStore _store;
    private readonly DiagnosticsLogger _logger;
    private readonly string _storefront;

    public FormatCacheResolver(FormatCacheStore store, DiagnosticsLogger logger)
        : this(store, logger, AppleMusicCatalogResolver.DefaultStorefront)
    {
    }

    internal FormatCacheResolver(FormatCacheStore store, DiagnosticsLogger logger, string storefront)
    {
        _store = store;
        _logger = logger;
        _storefront = storefront;
    }

    public string Name => "FormatCacheResolver";

    public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = FormatCacheKey.Create(_storefront, track);
        if (_store.TryGet(cacheKey, out var entry) && entry is not null)
        {
            _logger.Info(
                $"Format cache hit for '{track.Title ?? track.UniqueKey}': {entry.BitDepth}/{entry.SampleRateHz}.");
            return Task.FromResult<ResolvedAudioFormat?>(entry.ToResolvedFormat());
        }

        return Task.FromResult<ResolvedAudioFormat?>(null);
    }
}
