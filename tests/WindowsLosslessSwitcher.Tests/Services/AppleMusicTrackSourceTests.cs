using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AppleMusicTrackSourceTests
{
    private static readonly TimeSpan ImmediateDebounce = TimeSpan.Zero;
    private static readonly TimeSpan LongDebounce = TimeSpan.FromSeconds(60);

    // ── IsPlaceholderTrack ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Connecting…")]
    [InlineData("Connecting...")]
    [InlineData("Connecting")]
    [InlineData("  connecting…  ")]
    public void IsPlaceholderTrack_ReturnsTrueForConnectingVariants(string connectingValue)
    {
        var byTitle = CreateTrack(title: connectingValue, artist: "Real Artist");
        var byArtist = CreateTrack(title: "Real Title", artist: connectingValue);

        Assert.True(AppleMusicTrackSource.IsPlaceholderTrack(byTitle));
        Assert.True(AppleMusicTrackSource.IsPlaceholderTrack(byArtist));
    }

    [Theory]
    [InlineData("Midnight Rain", "Taylor Swift")]
    [InlineData("Flowers", "Miley Cyrus")]
    [InlineData("", "")]
    public void IsPlaceholderTrack_ReturnsFalseForRealOrEmptyTrack(string title, string artist)
    {
        var track = CreateTrack(title: title, artist: artist);

        Assert.False(AppleMusicTrackSource.IsPlaceholderTrack(track));
    }

    // ── IsAppleMusicSessionId ────────────────────────────────────────────────

    [Theory]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")] // Windows 11 package AUMID
    [InlineData("appleinc.applemusicwin_nzyj5cx40ttqa!App")]
    [InlineData("AppleMusic.exe")] // Windows 10 reports the bare executable name (seen on 19045)
    [InlineData("applemusic.exe")]
    public void IsAppleMusicSessionId_MatchesAppleMusicSessions(string sourceAppUserModelId)
    {
        Assert.True(AppleMusicTrackSource.IsAppleMusicSessionId(sourceAppUserModelId));
    }

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("Chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("NotAppleMusic.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAppleMusicSessionId_RejectsOtherSessions(string? sourceAppUserModelId)
    {
        Assert.False(AppleMusicTrackSource.IsAppleMusicSessionId(sourceAppUserModelId));
    }

    // ── HandleSnapshot: real tracks ──────────────────────────────────────────

    [Fact]
    public async Task HandleSnapshot_PublishesRealTrack()
    {
        await using var source = CreateSource();
        TrackSnapshot? received = null;
        source.TrackChanged += (_, e) => received = e;

        source.HandleSnapshot(CreateTrack("Song", "Artist"));

        Assert.NotNull(received);
        Assert.Equal("Song", received!.Title);
    }

    [Fact]
    public async Task HandleSnapshot_SuppressesDuplicateTrack()
    {
        await using var source = CreateSource();
        var count = 0;
        source.TrackChanged += (_, _) => count++;

        source.HandleSnapshot(CreateTrack("Song", "Artist"));
        source.HandleSnapshot(CreateTrack("Song", "Artist"));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task HandleSnapshot_PublishesAgainAfterDifferentTrack()
    {
        await using var source = CreateSource();
        var received = new List<string?>();
        source.TrackChanged += (_, e) => received.Add(e.Title);

        source.HandleSnapshot(CreateTrack("Song A", "Artist"));
        source.HandleSnapshot(CreateTrack("Song B", "Artist"));

        Assert.Equal(["Song A", "Song B"], received);
    }

    [Fact]
    public async Task HandleSnapshot_IgnoresSnapshotWithEmptyTitleAndArtist()
    {
        await using var source = CreateSource();
        var count = 0;
        source.TrackChanged += (_, _) => count++;

        source.HandleSnapshot(CreateTrack("", ""));

        Assert.Equal(0, count);
    }

    // ── HandleSnapshot: placeholder debounce ─────────────────────────────────

    [Fact]
    public async Task HandleSnapshot_PublishesPlaceholderAfterDebounce()
    {
        await using var source = CreateSource(placeholderDebounce: ImmediateDebounce);
        TrackSnapshot? received = null;
        source.TrackChanged += (_, e) => received = e;

        source.HandleSnapshot(CreateTrack("Connecting…", "Artist"));

        // Allow the debounce task to complete.
        await Task.Delay(50);
        Assert.NotNull(received);
    }

    [Fact]
    public async Task HandleSnapshot_SuppressesPlaceholderWhenLastRealTrackExists()
    {
        await using var source = CreateSource(placeholderDebounce: ImmediateDebounce);
        var received = new List<string?>();
        source.TrackChanged += (_, e) => received.Add(e.Title);

        // Establish a real track first.
        source.HandleSnapshot(CreateTrack("Real Song", "Artist"));
        // Placeholder should be suppressed — the last real track is preserved.
        source.HandleSnapshot(CreateTrack("Connecting…", "Artist"));

        await Task.Delay(50);
        Assert.Equal(["Real Song"], received);
    }

    [Fact]
    public async Task HandleSnapshot_DiscardsStalePlaceholderWhenRealTrackArrivesFirst()
    {
        // Use a long debounce so the placeholder is still pending when the real track arrives.
        await using var source = CreateSource(placeholderDebounce: LongDebounce);
        var received = new List<string?>();
        source.TrackChanged += (_, e) => received.Add(e.Title);

        source.HandleSnapshot(CreateTrack("Connecting…", "Artist"));
        source.HandleSnapshot(CreateTrack("Real Song", "Artist"));

        // Only the real track should appear; the deferred placeholder was discarded.
        await Task.Delay(50);
        Assert.Equal(["Real Song"], received);
    }

    // ── Artwork tagging + stale-read retry ────────────────────────────────────

    [Fact]
    public async Task ProcessResolvedSnapshot_TagsArtworkWithTrackKey()
    {
        await using var source = CreateSource();
        var track = CreateTrack("Song A", "Artist");

        await source.ProcessResolvedSnapshotAsync(
            track,
            CreateSessionSnapshot(track),
            _ => Task.FromResult(CreateArtwork([1, 2, 3], "AAA")),
            publishTrackSnapshot: false,
            reason: "test",
            callbackLatencyMs: 0,
            CancellationToken.None);

        var applied = source.GetArtworkSnapshot();
        Assert.Equal("AAA", applied.Revision);
        Assert.Equal(track.UniqueKey, applied.TrackUniqueKey);
    }

    [Fact]
    public async Task ProcessResolvedSnapshot_RetriesByteIdenticalArtworkForDifferentTrack()
    {
        // GSMTC often serves the PREVIOUS track's thumbnail right after a track change; that
        // reads as byte-identical artwork (same SHA revision) for a new track key, and one
        // delayed re-read must replace it.
        var originalDelay = AppleMusicTrackSource.StaleArtworkRetryDelay;
        AppleMusicTrackSource.StaleArtworkRetryDelay = TimeSpan.FromMilliseconds(30);
        try
        {
            await using var source = CreateSource();
            var trackA = CreateTrack("Song A", "Artist");
            await source.ProcessResolvedSnapshotAsync(
                trackA,
                CreateSessionSnapshot(trackA),
                _ => Task.FromResult(CreateArtwork([1, 2, 3], "AAA")),
                publishTrackSnapshot: false,
                reason: "test",
                callbackLatencyMs: 0,
                CancellationToken.None);

            var retryCalls = 0;
            var trackB = CreateTrack("Song B", "Artist");
            await source.ProcessResolvedSnapshotAsync(
                trackB,
                CreateSessionSnapshot(trackB),
                _ => Task.FromResult(CreateArtwork([1, 2, 3], "AAA")),
                publishTrackSnapshot: false,
                reason: "test",
                callbackLatencyMs: 0,
                CancellationToken.None,
                _ =>
                {
                    retryCalls++;
                    return Task.FromResult(CreateArtwork([9, 9, 9], "BBB"));
                });

            Assert.Equal(1, retryCalls);
            var applied = source.GetArtworkSnapshot();
            Assert.Equal("BBB", applied.Revision);
            Assert.Equal(trackB.UniqueKey, applied.TrackUniqueKey);
        }
        finally
        {
            AppleMusicTrackSource.StaleArtworkRetryDelay = originalDelay;
        }
    }

    [Fact]
    public async Task ProcessResolvedSnapshot_DoesNotRetryIdenticalArtworkForSameTrack()
    {
        // Repeat callbacks for the SAME track legitimately return the same bytes — no retry.
        await using var source = CreateSource();
        var track = CreateTrack("Song A", "Artist");
        await source.ProcessResolvedSnapshotAsync(
            track,
            CreateSessionSnapshot(track),
            _ => Task.FromResult(CreateArtwork([1, 2, 3], "AAA")),
            publishTrackSnapshot: false,
            reason: "test",
            callbackLatencyMs: 0,
            CancellationToken.None);

        var retryCalls = 0;
        await source.ProcessResolvedSnapshotAsync(
            track,
            CreateSessionSnapshot(track),
            _ => Task.FromResult(CreateArtwork([1, 2, 3], "AAA")),
            publishTrackSnapshot: false,
            reason: "test",
            callbackLatencyMs: 0,
            CancellationToken.None,
            _ =>
            {
                retryCalls++;
                return Task.FromResult(MediaArtworkSnapshot.CreateUnavailable());
            });

        Assert.Equal(0, retryCalls);
        Assert.Equal("AAA", source.GetArtworkSnapshot().Revision);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MediaSessionSnapshot CreateSessionSnapshot(TrackSnapshot track) =>
        MediaSessionSnapshot.CreateUnavailable() with
        {
            SourceAppUserModelId = track.SourceAppUserModelId,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
        };

    private static MediaArtworkSnapshot CreateArtwork(byte[] bytes, string revision) =>
        new(bytes, "image/jpeg", revision, DateTimeOffset.UtcNow);

    private static AppleMusicTrackSource CreateSource(
        TimeSpan? placeholderDebounce = null,
        TimeSpan? sessionLossDebounce = null)
    {
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new AppleMusicTrackSource(
            logger,
            placeholderDebounce ?? TimeSpan.FromSeconds(2),
            sessionLossDebounce ?? TimeSpan.FromSeconds(3));
    }

    private static TrackSnapshot CreateTrack(string title = "Title", string artist = "Artist") =>
        new(
            "AppleInc.AppleMusicWin_nzyj5cx40ttqa",
            null,
            title,
            artist,
            "Album",
            "test",
            DateTimeOffset.UtcNow);
}
