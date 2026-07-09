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

    public FormatCacheResolver(FormatCacheStore store, DiagnosticsLogger logger)
    {
        _store = store;
        _logger = logger;
    }

    public string Name => "FormatCacheResolver";

    public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        if (_store.TryGet(track.UniqueKey, out var entry) && entry is not null)
        {
            _logger.Info(
                $"Format cache hit for '{track.Title ?? track.UniqueKey}': {entry.BitDepth}/{entry.SampleRateHz}.");
            return Task.FromResult<ResolvedAudioFormat?>(entry.ToResolvedFormat());
        }

        return Task.FromResult<ResolvedAudioFormat?>(null);
    }
}