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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
