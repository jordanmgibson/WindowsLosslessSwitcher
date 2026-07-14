using System.Globalization;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheVerificationTests : IDisposable
{
    private readonly string _directory;
    private readonly DiagnosticsLogger _logger;
    private readonly FormatCacheStore _store;

    public FormatCacheVerificationTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"));
        _logger = new DiagnosticsLogger(_directory);
        _store = new FormatCacheStore(Path.Combine(_directory, "format-cache.json"), _logger);
    }

    [Fact]
    public void IsVerificationDue_ReturnsFalseForFreshEntry()
    {
        var entry = CreateEntry(lastVerifiedAtUtc: DateTimeOffset.UtcNow);

        Assert.False(FormatCacheVerificationService.IsVerificationDue(entry, refreshDays: 30));
    }

    [Fact]
    public void IsVerificationDue_ReturnsTrueForStaleEntry()
    {
        var entry = CreateEntry(lastVerifiedAtUtc: DateTimeOffset.UtcNow.AddDays(-40));

        Assert.True(FormatCacheVerificationService.IsVerificationDue(entry, refreshDays: 30));
    }

    [Fact]
    public async Task VerifyAsync_UpdatesStoreAndReturnsEventArgsWhenFormatChanges()
    {
        var track = CreateTrack();
        var entry = StoreStaleEntry(track, CreateCatalogFormat(96000, 24, "song-123"));
        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) =>
                Task.FromResult<ResolvedAudioFormat?>(CreateCatalogFormat(192000, 24, "song-123"))),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal(96000, update.PreviousEntry.SampleRateHz);
        Assert.Equal(192000, update.UpdatedFormat.SampleRateHz);
        Assert.True(_store.TryGet(entry.UniqueKey, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(entry.CachedAtUtc, stored.CachedAtUtc);
        Assert.Equal(192000, stored.SampleRateHz);
        Assert.Equal(_store.ClearGeneration, update.CacheGeneration);
        Assert.True(_store.Clear());
        Assert.NotEqual(_store.ClearGeneration, update.CacheGeneration);
    }

    [Fact]
    public async Task VerifyAsync_RefreshesMetadataWithoutEventWhenFormatMatches()
    {
        var track = CreateTrack();
        var entry = StoreStaleEntry(track, CreateCatalogFormat(96000, 24, "song-123"));
        var updated = CreateCatalogFormat(96000, 24, "song-456") with
        {
            Description = "updated description",
        };
        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(updated)),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.Null(update);
        Assert.True(_store.TryGet(entry.UniqueKey, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(entry.CachedAtUtc, stored.CachedAtUtc);
        Assert.True(stored.LastVerifiedAtUtc > entry.LastVerifiedAtUtc);
        Assert.Equal("song-456", stored.CatalogSongId);
        Assert.Equal("updated description", stored.Description);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsNullWithoutMutationWhenCatalogLookupFails()
    {
        var track = CreateTrack();
        var entry = StoreStaleEntry(track, CreateCatalogFormat(96000, 24, "song-123"));
        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(null)),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.Null(update);
        Assert.True(_store.TryGet(entry.UniqueKey, out var stored));
        Assert.Equal(entry, stored);
    }

    [Fact]
    public async Task VerifyAsync_WhenCacheClearedDuringLookup_DoesNotRecreateEntryOrReturnUpdate()
    {
        var track = CreateTrack();
        var entry = StoreStaleEntry(track, CreateCatalogFormat(96000, 24, "song-123"));
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupResult = new TaskCompletionSource<ResolvedAudioFormat?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver(async (_, _) =>
            {
                lookupStarted.TrySetResult();
                return await lookupResult.Task;
            }),
            _logger);

        var verificationTask = service.VerifyAsync(track, entry, CancellationToken.None);
        await lookupStarted.Task;
        Assert.True(_store.Clear());
        lookupResult.SetResult(CreateCatalogFormat(192000, 24, "song-999"));

        Assert.Null(await verificationTask);
        Assert.False(_store.TryGet(entry.UniqueKey, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FormatCacheEntry StoreStaleEntry(TrackSnapshot track, ResolvedAudioFormat format)
    {
        var cacheKey = FormatCacheKey.Create("us", track);
        Assert.True(_store.Store(cacheKey, format, DateTimeOffset.UtcNow.AddDays(-40)));
        Assert.True(_store.TryGet(cacheKey, out var entry));
        return Assert.IsType<FormatCacheEntry>(entry);
    }

    private static FormatCacheEntry CreateEntry(DateTimeOffset lastVerifiedAtUtc) =>
        new(
            "cache-key",
            "song-123",
            96000,
            24,
            ResolutionConfidence.Exact,
            "Catalog manifest: 24/96",
            lastVerifiedAtUtc.AddDays(-1),
            lastVerifiedAtUtc);

    private static TrackSnapshot CreateTrack() =>
        new(
            "AppleMusic",
            null,
            "Track",
            "Artist",
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private static ResolvedAudioFormat CreateCatalogFormat(int sampleRateHz, int bitDepth, string catalogSongId) =>
        new(
            sampleRateHz,
            bitDepth,
            ResolutionConfidence.Exact,
            AudioFormatSource.CatalogManifest,
            string.Create(CultureInfo.InvariantCulture, $"Catalog manifest: {bitDepth}/{sampleRateHz / 1000.0:0.###}"))
        {
            CatalogSongId = catalogSongId,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class DelegateResolver(Func<TrackSnapshot, CancellationToken, Task<ResolvedAudioFormat?>> resolveAsync)
        : IFormatResolver
    {
        public string Name => nameof(DelegateResolver);

        public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken) =>
            resolveAsync(track, cancellationToken);
    }
}
