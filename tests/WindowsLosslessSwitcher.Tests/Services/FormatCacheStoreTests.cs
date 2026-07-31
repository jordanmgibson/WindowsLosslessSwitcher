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
    public void Store_WhenCacheFileTemporarilyUnreadable_DoesNotWipeExistingEntries()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        var reloaded = new FormatCacheStore(_cachePath, null);
        // The discriminating assertions are the post-unlock ones: a store that latched itself
        // initialized-empty during the failed read would keep returning misses (and would have
        // been willing to persist that empty state), while the guarded store retries the read and
        // recovers the intact entries.
        using (new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.False(reloaded.TryGet("cache-key", out _));
            Assert.False(reloaded.Store("other-key", CreateCatalogFormat(48000, 24, "song-456")));
        }

        // The on-disk document was never replaced while unreadable.
        var untouched = new FormatCacheStore(_cachePath, null);
        Assert.True(untouched.TryGet("cache-key", out var preserved));
        Assert.NotNull(preserved);
        Assert.Equal(96000, preserved.SampleRateHz);
        Assert.False(untouched.TryGet("other-key", out _));

        // And the paused store recovers once the file is readable again.
        Assert.True(reloaded.TryGet("cache-key", out _));
        Assert.True(reloaded.Store("other-key", CreateCatalogFormat(48000, 24, "song-456")));
        Assert.True(reloaded.TryGet("other-key", out _));
    }

    [Fact]
    public void Clear_ReportsFailureWhileFileIsLockedAndClearsWithoutLoadingAfterRelease()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        var reloaded = new FormatCacheStore(_cachePath, null);
        using (new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.False(reloaded.TryGet("cache-key", out _));
            // Windows cannot rename over an open file, so the empty replacement cannot land while
            // the lock is held; Clear must report the failure rather than pretend.
            Assert.False(reloaded.Clear());
        }

        // Once the lock is released, Clear succeeds without ever having read the old content.
        Assert.True(reloaded.Clear());
        Assert.False(reloaded.TryGet("cache-key", out _));
        var fresh = new FormatCacheStore(_cachePath, null);
        Assert.False(fresh.TryGet("cache-key", out _));

        // Clear leaves the store initialized: a document written behind its back must not be
        // re-read (this process is the sole writer of the cache file).
        WriteCacheDocument(BuildEntryJson("cache-key", DateTimeOffset.UtcNow));
        Assert.False(reloaded.TryGet("cache-key", out _));
    }

    [Fact]
    public void Load_SkipsInvalidEntriesAndKeepsValidOnes()
    {
        WriteCacheDocument(
            BuildEntryJson("good-key", DateTimeOffset.UtcNow),
            // Key/UniqueKey mismatch: a cross-key-poisoned pair is dropped, not fatal.
            "\"mismatched\":{\"uniqueKey\":\"other\",\"sampleRateHz\":96000,\"bitDepth\":24,\"confidence\":\"exact\",\"description\":\"d\",\"cachedAtUtc\":\"2026-01-01T00:00:00+00:00\",\"lastVerifiedAtUtc\":\"2026-01-01T00:00:00+00:00\"}",
            "\"zero-rate\":{\"uniqueKey\":\"zero-rate\",\"sampleRateHz\":0,\"bitDepth\":24,\"confidence\":\"exact\",\"description\":\"d\",\"cachedAtUtc\":\"2026-01-01T00:00:00+00:00\",\"lastVerifiedAtUtc\":\"2026-01-01T00:00:00+00:00\"}",
            "\"null-entry\":null");
        var store = new FormatCacheStore(_cachePath, null);

        Assert.True(store.TryGet("good-key", out _));
        Assert.False(store.TryGet("mismatched", out _));
        Assert.False(store.TryGet("zero-rate", out _));
        Assert.False(store.TryGet("null-entry", out _));
    }

    [Fact]
    public void Store_EvictsOldestVerifiedEntriesBeyondCap()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WriteCacheDocument(Enumerable.Range(0, FormatCacheStore.MaxEntries)
            .Select(i => BuildEntryJson($"key-{i:D4}", baseTime.AddMinutes(i)))
            .ToArray());
        var store = new FormatCacheStore(_cachePath, null);

        Assert.True(store.Store("new-key", CreateCatalogFormat(96000, 24, "song-new")));

        Assert.False(store.TryGet("key-0000", out _));
        Assert.True(store.TryGet("key-0001", out _));
        Assert.True(store.TryGet("new-key", out _));

        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.False(reloaded.TryGet("key-0000", out _));
        Assert.True(reloaded.TryGet("new-key", out _));
    }

    [Fact]
    public void TryTouchVerification_BumpsLastVerifiedWithoutChangingFormat()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));
        Assert.True(_store.TryGet("cache-key", out var original));
        Assert.NotNull(original);
        var touchedAt = original.LastVerifiedAtUtc.AddDays(1);

        Assert.True(_store.TryTouchVerification(original, touchedAt));

        Assert.True(_store.TryGet("cache-key", out var touched));
        Assert.NotNull(touched);
        Assert.Equal(touchedAt, touched.LastVerifiedAtUtc);
        Assert.Equal(original.SampleRateHz, touched.SampleRateHz);
        Assert.Equal(original.BitDepth, touched.BitDepth);
        Assert.Equal(original.CatalogSongId, touched.CatalogSongId);
        Assert.Equal(original.CachedAtUtc, touched.CachedAtUtc);

        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.True(reloaded.TryGet("cache-key", out var persisted));
        Assert.NotNull(persisted);
        Assert.Equal(touchedAt, persisted.LastVerifiedAtUtc);
    }

    [Fact]
    public void TryTouchVerification_IsSupersededByClearAndNewerStore()
    {
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));
        Assert.True(_store.TryGet("cache-key", out var original));
        Assert.NotNull(original);

        Assert.True(_store.Clear());
        Assert.False(_store.TryTouchVerification(original, DateTimeOffset.UtcNow));
        Assert.False(_store.TryGet("cache-key", out _));

        Assert.True(_store.Store("cache-key", CreateCatalogFormat(48000, 24, "song-new")));
        Assert.True(_store.TryGet("cache-key", out var replacement));
        Assert.NotNull(replacement);
        Assert.False(_store.TryTouchVerification(original, DateTimeOffset.UtcNow));
        Assert.True(_store.TryGet("cache-key", out var current));
        Assert.Equal(replacement, current);
    }

    [Fact]
    public void ParallelStoreClearAndTouch_KeepsDocumentConsistent()
    {
        // Production runs Store on track-processing threads while verification touches and the UI
        // clears against the same file; this pins "no torn document, no leftover temp files".
        Parallel.For(0, 32, i =>
        {
            _store.Store($"key-{i}", CreateCatalogFormat(96000, 24, $"song-{i}"));
            if (i % 11 == 0)
            {
                _store.Clear();
            }

            if (_store.TryGet($"key-{i}", out var entry) && entry is not null)
            {
                _store.TryTouchVerification(entry, DateTimeOffset.UtcNow);
            }
        });

        Assert.True(_store.Store("final-key", CreateCatalogFormat(192000, 24, "song-final")));
        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.True(reloaded.TryGet("final-key", out _));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void StoreNoMatch_RoundTripsAndSurvivesReload()
    {
        Assert.True(_store.StoreNoMatch("local-track"));

        Assert.True(_store.TryGet("local-track", out var entry));
        Assert.NotNull(entry);
        Assert.True(entry.NoMatch);
        Assert.Throws<InvalidOperationException>(() => entry.ToResolvedFormat());

        var reloaded = new FormatCacheStore(_cachePath, null);
        Assert.True(reloaded.TryGet("local-track", out var persisted));
        Assert.NotNull(persisted);
        Assert.True(persisted.NoMatch);
    }

    [Fact]
    public void StoreNoMatch_CanBeReplacedByAPositiveMatch()
    {
        // A track that later appears in the catalog upgrades its entry in place.
        Assert.True(_store.StoreNoMatch("cache-key"));
        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        Assert.True(_store.TryGet("cache-key", out var entry));
        Assert.NotNull(entry);
        Assert.False(entry.NoMatch);
        Assert.Equal(96000, entry.SampleRateHz);
    }

    [Fact]
    public void Store_SweepsOrphanedTemporaryFiles()
    {
        Directory.CreateDirectory(_directory);
        var orphan = $"{_cachePath}.deadbeef.tmp";
        File.WriteAllText(orphan, "{}");

        Assert.True(_store.Store("cache-key", CreateCatalogFormat(96000, 24, "song-123")));

        Assert.False(File.Exists(orphan));
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
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A scanner or indexer may briefly hold a just-written file; leftover temp dirs are
            // harmless and must not fail an otherwise-passing test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteCacheDocument(params string[] entryJsonPairs)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _cachePath,
            $"{{\"schemaVersion\":1,\"catalogResolverVersion\":{FormatCacheStore.CatalogResolverVersion}," +
            $"\"entries\":{{{string.Join(",", entryJsonPairs)}}}}}");
    }

    private static string BuildEntryJson(string key, DateTimeOffset lastVerifiedAtUtc) =>
        $"\"{key}\":{{\"uniqueKey\":\"{key}\",\"catalogSongId\":\"song\",\"sampleRateHz\":96000,\"bitDepth\":24," +
        "\"confidence\":\"exact\",\"description\":\"Catalog manifest: 24/96\"," +
        $"\"cachedAtUtc\":\"2026-01-01T00:00:00+00:00\",\"lastVerifiedAtUtc\":\"{lastVerifiedAtUtc:O}\"}}";

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
