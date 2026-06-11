using System.Net.Http;
using System.Text.Json;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AppleMusicCatalogResolverTests
{
    // ── TryGetHighestLosslessVariant ──────────────────────────────────────────

    [Fact]
    public void TryGetHighestLosslessVariant_ReturnsFalseForEmptyManifest()
    {
        var result = AppleMusicCatalogResolver.TryGetHighestLosslessVariant(string.Empty, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetHighestLosslessVariant_ReturnsFalseWhenNoLosslessLines()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES
            #EXT-X-STREAM-INF:BANDWIDTH=256000,CODECS="mp4a.40.2"
            audio.m3u8
            """;

        var result = AppleMusicCatalogResolver.TryGetHighestLosslessVariant(manifest, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetHighestLosslessVariant_ParsesSingleLosslessVariant()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="alac_96000_24",NAME="English",DEFAULT=YES,SAMPLE-RATE=96000,BIT-DEPTH=24
            """;

        var result = AppleMusicCatalogResolver.TryGetHighestLosslessVariant(manifest, out var sampleRate, out var bitDepth);

        Assert.True(result);
        Assert.Equal(96000, sampleRate);
        Assert.Equal(24, bitDepth);
    }

    [Fact]
    public void TryGetHighestLosslessVariant_ReturnsHighestSampleRateWhenMultipleVariants()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,SAMPLE-RATE=44100,BIT-DEPTH=16
            #EXT-X-MEDIA:TYPE=AUDIO,SAMPLE-RATE=192000,BIT-DEPTH=24
            #EXT-X-MEDIA:TYPE=AUDIO,SAMPLE-RATE=96000,BIT-DEPTH=24
            """;

        var result = AppleMusicCatalogResolver.TryGetHighestLosslessVariant(manifest, out var sampleRate, out var bitDepth);

        Assert.True(result);
        Assert.Equal(192000, sampleRate);
        Assert.Equal(24, bitDepth);
    }

    [Fact]
    public void TryGetHighestLosslessVariant_PrefersHigherBitDepthAtSameSampleRate()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,SAMPLE-RATE=96000,BIT-DEPTH=16
            #EXT-X-MEDIA:TYPE=AUDIO,SAMPLE-RATE=96000,BIT-DEPTH=24
            """;

        var result = AppleMusicCatalogResolver.TryGetHighestLosslessVariant(manifest, out var sampleRate, out var bitDepth);

        Assert.True(result);
        Assert.Equal(96000, sampleRate);
        Assert.Equal(24, bitDepth);
    }

    // ── GetDeveloperTokenAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetDeveloperTokenAsync_ReturnsNullWhenBrowsePageFails()
    {
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>(null));

        var token = await resolver.GetDeveloperTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetDeveloperTokenAsync_ReturnsNullWhenScriptUrlNotFound()
    {
        // Browse page has no matching script src.
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>("<html>no script</html>"));

        var token = await resolver.GetDeveloperTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetDeveloperTokenAsync_ReturnsNullWhenScriptHasNoToken()
    {
        // Browse page has a script link; the script itself has no JWT.
        var resolver = CreateResolver(sendAsync: (request, _) =>
        {
            var html = request.RequestUri!.AbsolutePath.EndsWith("browse", StringComparison.Ordinal)
                ? "<html><script src=\"/assets/index-abc123.js\"></script></html>"
                : "var x = 'no token here';";
            return Task.FromResult<string?>(html);
        });

        var token = await resolver.GetDeveloperTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetDeveloperTokenAsync_ParsesTokenFromScript()
    {
        // Build a minimal JWT (header.payload.signature) with a future expiry.
        var expiry = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new { exp = expiry }));
        var fakeJwt = $"eyJhbGciOiJFUzI1NiJ9.{payload}.fakesignature";

        var resolver = CreateResolver(sendAsync: (request, _) =>
        {
            var content = request.RequestUri!.AbsolutePath.EndsWith("browse", StringComparison.Ordinal)
                ? "<html><script src=\"/assets/index-abc123.js\"></script></html>"
                : $"var token = '{fakeJwt}';";
            return Task.FromResult<string?>(content);
        });

        var token = await resolver.GetDeveloperTokenAsync(CancellationToken.None);

        Assert.Equal(fakeJwt, token);
    }

    [Fact]
    public async Task GetDeveloperTokenAsync_CachesTokenAndDoesNotRefetchUntilExpiry()
    {
        var fetchCount = 0;
        var expiry = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new { exp = expiry }));
        var fakeJwt = $"eyJhbGciOiJFUzI1NiJ9.{payload}.sig";

        var resolver = CreateResolver(sendAsync: (request, _) =>
        {
            fetchCount++;
            var content = request.RequestUri!.AbsolutePath.EndsWith("browse", StringComparison.Ordinal)
                ? "<html><script src=\"/assets/index-abc123.js\"></script></html>"
                : $"var t = '{fakeJwt}';";
            return Task.FromResult<string?>(content);
        });

        var first = await resolver.GetDeveloperTokenAsync(CancellationToken.None);
        var second = await resolver.GetDeveloperTokenAsync(CancellationToken.None);

        // Two HTTP round-trips per fetch (browse page + script), so fetchCount == 2 means cached.
        Assert.Equal(first, second);
        Assert.Equal(2, fetchCount);
    }

    // ── SearchBestMatchAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchBestMatchAsync_ReturnsNullWhenApiReturnsEmpty()
    {
        var emptyResponse = JsonSerializer.Serialize(new
        {
            results = new { songs = new { data = Array.Empty<object>() } },
        });
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>(emptyResponse));
        var track = CreateTrack("Song", "Artist");

        var match = await resolver.SearchBestMatchAsync(track, "fake-token", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task SearchBestMatchAsync_ReturnsNullForLowScoreMatch()
    {
        // A result that shares nothing meaningful with the query.
        var response = BuildSearchResponse("Completely Different Title", "Completely Different Artist", "Different Album");
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>(response));
        var track = CreateTrack("Midnight Rain", "Taylor Swift");

        var match = await resolver.SearchBestMatchAsync(track, "fake-token", CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task SearchBestMatchAsync_ReturnsMatchForExactTitleAndArtist()
    {
        var response = BuildSearchResponse("Midnight Rain", "Taylor Swift", "Midnights");
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>(response));
        var track = CreateTrack("Midnight Rain", "Taylor Swift");

        var match = await resolver.SearchBestMatchAsync(track, "fake-token", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Midnight Rain", match!.Value.Title);
    }

    [Fact]
    public async Task SearchBestMatchAsync_RejectsExactTitleWithOnlyArtistOverlap()
    {
        // Exact title (100) + artist overlap only (25) + no album match (0) = 125, which is below the
        // raised MinimumAcceptedScore of 150. This guards a local file with similar-but-not-exact
        // artist metadata from being treated as the catalog track.
        var response = BuildSearchResponse("Shared Title", "Future & Metro Boomin", "Some Other Album");
        var resolver = CreateResolver(sendAsync: (_, _) => Task.FromResult<string?>(response));
        var track = CreateTrack("Shared Title", "Drake & Future");

        var match = await resolver.SearchBestMatchAsync(track, "fake-token", CancellationToken.None);

        Assert.Null(match);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppleMusicCatalogResolver CreateResolver(
        Func<HttpRequestMessage, CancellationToken, Task<string?>> sendAsync)
    {
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new AppleMusicCatalogResolver(logger, sendAsync, "us");
    }

    private static TrackSnapshot CreateTrack(string title, string artist) =>
        new(
            "AppleInc.AppleMusicWin_nzyj5cx40ttqa",
            null,
            title,
            artist,
            "Album",
            "test",
            DateTimeOffset.UtcNow);

    private static string BuildSearchResponse(string title, string artist, string album)
    {
        var data = new[]
        {
            new
            {
                id = "12345",
                attributes = new { name = title, artistName = artist, albumName = album },
            },
        };
        return JsonSerializer.Serialize(new
        {
            results = new { songs = new { data } },
        });
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
