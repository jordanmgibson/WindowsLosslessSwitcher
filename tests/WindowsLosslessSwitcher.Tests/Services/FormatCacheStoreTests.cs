using System.Globalization;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatCacheStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _cachePath;
    private readonly FormatCacheStore _store;

    public FormatCacheStoreTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"));
        _cachePath = Path.Combine(_directory, "format-cache.json");
        _store = new FormatCacheStore(_cachePath, null);
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenEntryDoesNotExist()
    {
        var found = _store.TryGet("missing", out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void Store_RoundTripsCatalogFormat()
    {
        const string uniqueKey = "cache-key";
        var format = CreateCatalogFormat(96000, 24, "song-123");

        Assert.True(_store.Store(uniqueKey, format));

        Assert.True(_store.TryGet(uniqueKey, out var entry));
        Assert.NotNull(entry);
        Assert.Equal("song-123", entry.CatalogSongId);
        Assert.Equal(96000, entry.SampleRateHz);
        Assert.Equal(24, entry.BitDepth);
        Assert.Equal(ResolutionConfidence.Exact, entry.Confidence);
        Assert.Equal(AudioFormatSource.CachedCatalog, entry.ToResolvedFormat().Source);
        Assert.Same(entry, entry.ToResolvedFormat().CachedCatalogEntry);
    }

    [Fact]
    public void Store_IgnoresNonCatalogSources()
    {
        var format = new ResolvedAudioFormat(
            48000,
            24,
            ResolutionConfidence.Exact,
            AudioFormatSource.LocalFile,
            "local");

        Assert.False(_store.Store("cache-key", format));
        Assert.False(_store.TryGet("cache-key", out _));
    }

    [Fact]
    public void Store_PersistsAcrossStoreInstances()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(192000, 24, "song-456")));

        var reloaded = new FormatCacheStore(_cachePath, null);

        Assert.True(reloaded.TryGet("cache-key", out var entry));
        Assert.NotNull(entry);
        Assert.Equal(192000, entry.SampleRateHz);
        Assert.Equal("song-456", entry.CatalogSongId);
    }

    [Fact]
    public void TryApplyVerification_ReplacesMetadataAndPreservesCachedAt()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));
        Assert.True(_store.TryGet("cache-key", out var original));
        Assert.NotNull(original);

        var updatedFormat = CreateCatalogFormat(192000, 24, "song-999") with
        {
            Description = "updated description",
        };
        Assert.True(_store.TryApplyVerification(original, updatedFormat));

        Assert.True(_store.TryGet("cache-key", out var updated));
        Assert.NotNull(updated);
        Assert.Equal(original.CachedAtUtc, updated.CachedAtUtc);
        Assert.True(updated.LastVerifiedAtUtc >= original.LastVerifiedAtUtc);
        Assert.Equal(192000, updated.SampleRateHz);
        Assert.Equal("song-999", updated.CatalogSongId);
        Assert.Equal("updated description", updated.Description);
    }

    [Fact]
    public void TryApplyVerification_AfterClear_DoesNotRecreateEntry()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));
        Assert.True(_store.TryGet("cache-key", out var original));
        Assert.NotNull(original);
        Assert.True(_store.Clear());

        var applied = _store.TryApplyVerification(
            original,
            CreateCatalogFormat(192000, 24, "song-999"));

        Assert.False(applied);
        Assert.False(_store.TryGet("cache-key", out _));
    }

    [Fact]
    public void TryApplyVerification_DoesNotOverwriteReplacementEntry()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));
        Assert.True(_store.TryGet("cache-key", out var original));
        Assert.NotNull(original);
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(48000, 24, "song-new")));

        var applied = _store.TryApplyVerification(
            original,
            CreateCatalogFormat(192000, 24, "song-old-verification"));

        Assert.False(applied);
        Assert.True(_store.TryGet("cache-key", out var current));
        Assert.NotNull(current);
        Assert.Equal(48000, current.SampleRateHz);
        Assert.Equal("song-new", current.CatalogSongId);
    }

    [Fact]
    public void Clear_RemovesEntriesAcrossStoreInstances()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        Assert.True(_store.Clear());

        Assert.False(_store.TryGet("cache-key", out _));
        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.False(reloaded.TryGet("cache-key", out _));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":999,\"catalogResolverVersion\":1,\"entries\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"catalogResolverVersion\":999,\"entries\":{}}")]
    public void TryGet_InvalidDocument_FailsOpenAndNextStoreRepairsFile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_cachePath, json);
        var store = new FormatCacheStore(_cachePath, null);

        Assert.False(store.TryGet("cache-key", out _));
        Assert.True(store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.True(reloaded.TryGet("cache-key", out _));
    }

    [Fact]
    public void Store_WhenDestinationCannotBeReplaced_FailsOpen()
    {
        var blockedPath = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(blockedPath);
        var store = new FormatCacheStore(blockedPath, null);

        var stored = store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123"));

        Assert.False(stored);
        Assert.False(store.TryGet("cache-key", out _));
    }

    [Fact]
    public void FormatCacheKey_IsStorefrontSpecificNormalizedAndCollisionSafe()
    {
        var track = CreateTrack(" Track ", " Artist ", " Album ");
        var normalized = CreateTrack("Track", "Artist", "Album");

        Assert.Equal(FormatCacheKey.Create("US", track), FormatCacheKey.Create("us", normalized));
        Assert.NotEqual(FormatCacheKey.Create("us", normalized), FormatCacheKey.Create("jp", normalized));
        Assert.NotEqual(
            FormatCacheKey.Create("us", CreateTrack("A|B", "C", null)),
            FormatCacheKey.Create("us", CreateTrack("A", "B|C", null)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static TrackSnapshot CreateTrack(string title, string artist, string? album) =>
        new(
            "AppleMusic",
            null,
            title,
            artist,
            album,
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
}
