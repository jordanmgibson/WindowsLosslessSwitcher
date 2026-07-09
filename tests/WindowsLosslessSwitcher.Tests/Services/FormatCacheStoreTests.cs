using System.Globalization;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheStoreTests : IDisposable
{
    private readonly string _databasePath;
    private readonly FormatCacheStore _store;

    public FormatCacheStoreTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"),
            "format-cache.db");
        _store = new FormatCacheStore(_databasePath, null);
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenEntryDoesNotExist()
    {
        var found = _store.TryGet("AppleMusic||Track|Artist|Album", out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void Store_RoundTripsCatalogFormat()
    {
        const string uniqueKey = "AppleMusic||Track|Artist|Album";
        var format = CreateCatalogFormat(96000, 24, "song-123");

        _store.Store(uniqueKey, format);

        Assert.True(_store.TryGet(uniqueKey, out var entry));
        Assert.NotNull(entry);
        Assert.Equal("song-123", entry.CatalogSongId);
        Assert.Equal(96000, entry.SampleRateHz);
        Assert.Equal(24, entry.BitDepth);
        Assert.Equal(ResolutionConfidence.Exact, entry.Confidence);
        Assert.Equal(AudioFormatSource.CachedCatalog, entry.ToResolvedFormat().Source);
        Assert.Equal("song-123", entry.ToResolvedFormat().CatalogSongId);
    }

    [Fact]
    public void Store_IgnoresNonCatalogSources()
    {
        const string uniqueKey = "AppleMusic||Track|Artist|Album";
        var format = new ResolvedAudioFormat(
            48000,
            24,
            ResolutionConfidence.Exact,
            AudioFormatSource.LocalFile,
            "local");

        _store.Store(uniqueKey, format);

        Assert.False(_store.TryGet(uniqueKey, out _));
    }

    [Fact]
    public void Store_PersistsAcrossStoreInstances()
    {
        const string uniqueKey = "AppleMusic||Track|Artist|Album";
        _store.Store(uniqueKey, CreateCatalogFormat(192000, 24, "song-456"));

        var reloaded = new FormatCacheStore(_databasePath, null);

        Assert.True(reloaded.TryGet(uniqueKey, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(192000, entry.SampleRateHz);
        Assert.Equal("song-456", entry.CatalogSongId);
    }

    [Fact]
    public void TouchLastVerified_UpdatesTimestampWithoutChangingFormat()
    {
        const string uniqueKey = "AppleMusic||Track|Artist|Album";
        _store.Store(uniqueKey, CreateCatalogFormat(96000, 24, "song-123"));
        Assert.True(_store.TryGet(uniqueKey, out var original));
        Assert.NotNull(original);

        var verifiedAt = original.LastVerifiedAtUtc.AddDays(40);
        _store.TouchLastVerified(uniqueKey, verifiedAt);

        Assert.True(_store.TryGet(uniqueKey, out var updated));
        Assert.NotNull(updated);
        Assert.Equal(original.SampleRateHz, updated.SampleRateHz);
        Assert.Equal(original.BitDepth, updated.BitDepth);
        Assert.Equal(verifiedAt.UtcDateTime, updated.LastVerifiedAtUtc.UtcDateTime);
    }

    [Fact]
    public void UpdateEntry_ReplacesStoredFormatAndPreservesCachedAt()
    {
        const string uniqueKey = "AppleMusic||Track|Artist|Album";
        _store.Store(uniqueKey, CreateCatalogFormat(96000, 24, "song-123"));
        Assert.True(_store.TryGet(uniqueKey, out var original));
        Assert.NotNull(original);

        var updatedFormat = CreateCatalogFormat(192000, 24, "song-999");
        _store.UpdateEntry(uniqueKey, updatedFormat);

        Assert.True(_store.TryGet(uniqueKey, out var updated));
        Assert.NotNull(updated);
        Assert.Equal(original.CachedAtUtc.UtcDateTime, updated.CachedAtUtc.UtcDateTime);
        Assert.True(updated.LastVerifiedAtUtc >= original.LastVerifiedAtUtc);
        Assert.Equal(192000, updated.SampleRateHz);
        Assert.Equal("song-999", updated.CatalogSongId);
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
}