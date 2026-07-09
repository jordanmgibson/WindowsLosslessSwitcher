using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheResolverTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DiagnosticsLogger _logger;
    private readonly FormatCacheStore _store;

    public FormatCacheResolverTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"),
            "format-cache.db");
        _logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        _store = new FormatCacheStore(_databasePath, _logger);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullOnCacheMiss()
    {
        var resolver = new FormatCacheResolver(_store, _logger);
        var track = CreateTrack();

        var result = await resolver.ResolveAsync(track, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsCachedFormatOnHit()
    {
        var track = CreateTrack();
        _store.Store(
            track.UniqueKey,
            new ResolvedAudioFormat(
                96000,
                24,
                ResolutionConfidence.Exact,
                AudioFormatSource.CatalogManifest,
                "Catalog manifest: 24/96")
            {
                CatalogSongId = "song-123",
            });

        var resolver = new FormatCacheResolver(_store, _logger);

        var result = await resolver.ResolveAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(AudioFormatSource.CachedCatalog, result.Source);
        Assert.Equal(96000, result.SampleRateHz);
        Assert.Equal(24, result.BitDepth);
        Assert.Equal("song-123", result.CatalogSongId);
    }

    [Fact]
    public async Task ResolverChain_ShortCircuitsOnCacheHit()
    {
        var track = CreateTrack();
        _store.Store(
            track.UniqueKey,
            new ResolvedAudioFormat(
                96000,
                24,
                ResolutionConfidence.Exact,
                AudioFormatSource.CatalogManifest,
                "Catalog manifest: 24/96"));

        var cacheResolver = new FormatCacheResolver(_store, _logger);
        var downstream = new RecordingResolver();
        var chain = new ResolverChain([cacheResolver, downstream]);

        var result = await chain.ResolveAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(AudioFormatSource.CachedCatalog, result.Source);
        Assert.Equal(0, downstream.CallCount);
    }

    public void Dispose()
    {
        FormatCacheStore.ReleaseConnectionsForTesting();
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateTrack_UsesExpectedUniqueKeyFormat()
    {
        var track = CreateTrack();

        Assert.Equal("AppleMusic||Track|Artist|Album", track.UniqueKey);
    }

    private static TrackSnapshot CreateTrack() =>
        new(
            "AppleMusic",
            null,
            "Track",
            "Artist",
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private sealed class RecordingResolver : IFormatResolver
    {
        public string Name => nameof(RecordingResolver);

        public int CallCount { get; private set; }

        public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<ResolvedAudioFormat?>(new ResolvedAudioFormat(
                44100,
                16,
                ResolutionConfidence.Exact,
                AudioFormatSource.CatalogManifest,
                "downstream"));
        }
    }
}