using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheResolverTests : IDisposable
{
    private readonly string _directory;
    private readonly DiagnosticsLogger _logger;
    private readonly FormatCacheStore _store;

    public FormatCacheResolverTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"));
        _logger = new DiagnosticsLogger(_directory);
        _store = new FormatCacheStore(Path.Combine(_directory, "format-cache.json"), _logger);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullOnCacheMiss()
    {
        var resolver = new FormatCacheResolver(_store, _logger);

        var result = await resolver.ResolveAsync(CreateTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsCachedFormatAndEntryOnHit()
    {
        var track = CreateTrack();
        _store.Store(
            FormatCacheKey.Create("us", track),
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
        Assert.NotNull(result.CachedCatalogEntry);
        Assert.Equal(result.ObservedAtUtc, result.CachedCatalogEntry.LastVerifiedAtUtc);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotReturnEntryFromAnotherStorefront()
    {
        var track = CreateTrack();
        _store.Store(
            FormatCacheKey.Create("jp", track),
            new ResolvedAudioFormat(
                96000,
                24,
                ResolutionConfidence.Exact,
                AudioFormatSource.CatalogManifest,
                "Catalog manifest: 24/96"));
        var resolver = new FormatCacheResolver(_store, _logger, "us");

        var result = await resolver.ResolveAsync(track, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolverChain_ShortCircuitsOnCacheHit()
    {
        var track = CreateTrack();
        _store.Store(
            FormatCacheKey.Create("us", track),
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
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
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
