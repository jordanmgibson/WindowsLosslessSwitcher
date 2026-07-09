using System.Globalization;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheVerificationTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DiagnosticsLogger _logger;
    private readonly FormatCacheStore _store;

    public FormatCacheVerificationTests()
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
        _store.Store(track.UniqueKey, CreateCatalogFormat(96000, 24, "song-123"));
        Assert.True(_store.TryGet(track.UniqueKey, out var entry));
        Assert.NotNull(entry);

        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) =>
                Task.FromResult<ResolvedAudioFormat?>(CreateCatalogFormat(192000, 24, "song-123"))),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal(96000, update.PreviousEntry.SampleRateHz);
        Assert.Equal(192000, update.UpdatedFormat.SampleRateHz);
        Assert.True(_store.TryGet(track.UniqueKey, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(192000, stored.SampleRateHz);
    }

    [Fact]
    public async Task VerifyAsync_TouchesLastVerifiedWithoutEventWhenFormatMatches()
    {
        var track = CreateTrack();
        _store.Store(track.UniqueKey, CreateCatalogFormat(96000, 24, "song-123"));
        Assert.True(_store.TryGet(track.UniqueKey, out var entry));
        Assert.NotNull(entry);

        var staleVerifiedAt = DateTimeOffset.UtcNow.AddDays(-40);
        _store.TouchLastVerified(track.UniqueKey, staleVerifiedAt);

        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) =>
                Task.FromResult<ResolvedAudioFormat?>(CreateCatalogFormat(96000, 24, "song-123"))),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.Null(update);
        Assert.True(_store.TryGet(track.UniqueKey, out var stored));
        Assert.NotNull(stored);
        Assert.True(stored.LastVerifiedAtUtc > staleVerifiedAt);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsNullWhenCatalogLookupFails()
    {
        var track = CreateTrack();
        _store.Store(track.UniqueKey, CreateCatalogFormat(96000, 24, "song-123"));
        Assert.True(_store.TryGet(track.UniqueKey, out var entry));
        Assert.NotNull(entry);

        var service = new FormatCacheVerificationService(
            _store,
            new DelegateResolver((_, _) => Task.FromResult<ResolvedAudioFormat?>(null)),
            _logger);

        var update = await service.VerifyAsync(track, entry, CancellationToken.None);

        Assert.Null(update);
        Assert.True(_store.TryGet(track.UniqueKey, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(96000, stored.SampleRateHz);
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

    private static FormatCacheEntry CreateEntry(DateTimeOffset lastVerifiedAtUtc) =>
        new(
            "AppleMusic||Track|Artist|Album",
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