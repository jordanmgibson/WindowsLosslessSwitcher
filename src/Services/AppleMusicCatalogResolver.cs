using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class AppleMusicCatalogResolver : IFormatResolver
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    // Number of search results to request per query attempt. 25 gives enough breadth to find
    // live, deluxe, and remaster variants without requesting an excessively large payload.
    private const int SearchResultLimit = 25;

    // Minimum score required to accept a catalog match and treat the track as an Apple Music catalog
    // (cloud) track. Scoring breakdown: title exact=100, title contains=70; artist exact=50,
    // artist contains=35, artist overlaps=25; album exact=10, album contains=5. A score of 150
    // requires an exact title (100) AND an exact artist (50) — a confident "this is the same track"
    // match. The catalog runs first and is the authoritative cloud-vs-local test: weaker matches fall
    // through to the device-max resolver, so a local file with merely similar metadata is not treated
    // as the catalog track and instead plays at the device's highest format.
    private const int MinimumAcceptedScore = 150;

    private readonly DiagnosticsLogger _logger;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<string?>> _sendAsync;
    private readonly string _storefront;
    private readonly object _tokenSync = new();
    private string? _developerToken;
    private DateTimeOffset _developerTokenExpiresAtUtc;

    public AppleMusicCatalogResolver(DiagnosticsLogger logger, string? configuredStorefront = null)
        : this(logger, SendAsync, ResolveStorefront(configuredStorefront, logger))
    {
    }

    internal AppleMusicCatalogResolver(
        DiagnosticsLogger logger,
        Func<HttpRequestMessage, CancellationToken, Task<string?>> sendAsync,
        string storefront)
    {
        _logger = logger;
        _sendAsync = sendAsync;
        _storefront = storefront;
    }

    /// <summary>
    /// Resolves the Apple Music storefront: an explicit user override wins, otherwise the OS region,
    /// otherwise "us". Catalog availability and matching differ by storefront, so guessing "us" for a
    /// non-US listener can return different results (or none) for the track they are actually playing.
    /// </summary>
    internal static string ResolveStorefront(string? configuredStorefront, DiagnosticsLogger logger)
    {
        if (!string.IsNullOrWhiteSpace(configuredStorefront))
        {
            var configured = configuredStorefront.Trim().ToLowerInvariant();
            logger.Info($"Catalog resolver storefront '{configured}' (source: configured override).");
            return configured;
        }

        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (!string.IsNullOrWhiteSpace(region))
            {
                var detected = region.ToLowerInvariant();
                logger.Info($"Catalog resolver storefront '{detected}' (source: OS region).");
                return detected;
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Catalog resolver storefront detection from OS region failed: {ex.Message}");
        }

        logger.Info("Catalog resolver storefront 'us' (source: default).");
        return "us";
    }

    public string Name => "AppleMusicCatalogResolver";

    public async Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
        {
            return null;
        }

        try
        {
            return await ResolveInternalAsync(track, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.Warn($"Catalog resolver failed for {track.UniqueKey}: {ex.Message}");
            return null;
        }
    }

    private async Task<ResolvedAudioFormat?> ResolveInternalAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        var normalizedTrack = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(track);
        var developerToken = await GetDeveloperTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(developerToken))
        {
            return null;
        }

        var bestMatch = await SearchBestMatchAsync(normalizedTrack, developerToken, cancellationToken);
        if (bestMatch is null)
        {
            _logger.Info($"Catalog resolver found no Apple Music match for {normalizedTrack.UniqueKey}.");
            return null;
        }

        var matchedSong = bestMatch.Value;
        var song = await GetSongDetailAsync(matchedSong.Id, developerToken, cancellationToken);
        if (song is null)
        {
            _logger.Info($"Catalog resolver found no enhanced HLS manifest for {normalizedTrack.UniqueKey}.");
            return null;
        }

        var songDetail = song.Value;
        if (string.IsNullOrWhiteSpace(songDetail.EnhancedHlsUrl))
        {
            _logger.Info($"Catalog resolver found no enhanced HLS manifest for {normalizedTrack.UniqueKey}.");
            return null;
        }

        var manifest = await FetchStringAsync(songDetail.EnhancedHlsUrl, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(manifest) ||
            !TryGetHighestLosslessVariant(manifest, out var sampleRateHz, out var bitDepth))
        {
            return null;
        }

        _logger.Info(
            $"Catalog resolver matched {songDetail.Id} for {normalizedTrack.UniqueKey}: {bitDepth}/{sampleRateHz} " +
            $"({string.Join(", ", songDetail.AudioTraits)}).");

        return new ResolvedAudioFormat(
            sampleRateHz,
            bitDepth,
            ResolutionConfidence.Exact,
            AudioFormatSource.CatalogManifest,
            $"Catalog manifest: {bitDepth}/{sampleRateHz / 1000.0:0.###}")
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    internal async Task<string?> GetDeveloperTokenAsync(CancellationToken cancellationToken)
    {
        lock (_tokenSync)
        {
            if (!string.IsNullOrWhiteSpace(_developerToken) &&
                _developerTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _developerToken;
            }
        }

        var browseHtml = await FetchStringAsync($"https://music.apple.com/{_storefront}/browse", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(browseHtml))
        {
            return null;
        }

        var scriptMatch = System.Text.RegularExpressions.Regex.Match(
            browseHtml,
            "src=\"(?<path>/assets/index[^\"']+\\.js)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!scriptMatch.Success)
        {
            return null;
        }

        var scriptUrl = $"https://music.apple.com{scriptMatch.Groups["path"].Value}";
        var scriptContent = await FetchStringAsync(scriptUrl, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            return null;
        }

        var tokenMatch = System.Text.RegularExpressions.Regex.Match(
            scriptContent,
            @"eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+");
        if (!tokenMatch.Success)
        {
            return null;
        }

        var token = tokenMatch.Value;
        var expiresAtUtc = TryParseJwtExpiryUtc(token, out var parsedExpiryUtc)
            ? parsedExpiryUtc
            : DateTimeOffset.UtcNow.AddHours(6);

        lock (_tokenSync)
        {
            _developerToken = token;
            _developerTokenExpiresAtUtc = expiresAtUtc;
        }

        return token;
    }

    internal async Task<CatalogSongMatch?> SearchBestMatchAsync(
        TrackSnapshot track,
        string developerToken,
        CancellationToken cancellationToken)
    {
        CatalogSongMatch? bestMatch = null;
        _logger.Info(
            $"Catalog resolver searching storefront '{_storefront}' for {track.UniqueKey}: " +
            $"title='{track.Title}', artist='{track.Artist}', album='{track.Album}'.");
        foreach (var attempt in BuildSearchAttempts(track))
        {
            var query = Uri.EscapeDataString(attempt.Query);
            var url = $"https://amp-api.music.apple.com/v1/catalog/{_storefront}/search?term={query}&types=songs&limit={SearchResultLimit}";
            var json = await FetchStringAsync(
                url,
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {developerToken}",
                    ["Origin"] = "https://music.apple.com",
                },
                cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.Info($"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned no response for {track.UniqueKey}.");
                continue;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("songs", out var songs) ||
                !songs.TryGetProperty("data", out var data))
            {
                _logger.Info($"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned 0 song results for {track.UniqueKey}.");
                continue;
            }

            // Score every raw result up front so we can log the top candidates with their scores —
            // including negative (hard-rejected) ones. Without this, a 100% miss is indistinguishable
            // from "Apple returned nothing", which is exactly what we need to tell apart.
            var rawCandidates = new List<(CatalogSongMatch Match, int Score)>();
            foreach (var item in data.EnumerateArray())
            {
                var match = CatalogSongMatch.FromJson(item);
                if (match is null)
                {
                    continue;
                }

                rawCandidates.Add((match.Value, ScoreMatch(track, match.Value.Title, match.Value.Artist, match.Value.Album)));
            }

            var topCandidates = rawCandidates
                .OrderByDescending(candidate => candidate.Score)
                .Take(3)
                .Select(candidate =>
                    $"\"{candidate.Match.Title}\" / \"{candidate.Match.Artist}\" / \"{candidate.Match.Album}\" (score={candidate.Score})");
            _logger.Info(
                $"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned {data.GetArrayLength()} result(s) for {track.UniqueKey}; " +
                $"top: {(rawCandidates.Count == 0 ? "none" : string.Join("; ", topCandidates))}.");

            CatalogSongMatch? bestAttemptMatch = null;
            foreach (var (match, score) in rawCandidates)
            {
                if (score < 0)
                {
                    continue;
                }

                var candidate = match with { Score = score };
                if (bestAttemptMatch is null || candidate.CompareTo(bestAttemptMatch.Value) > 0)
                {
                    bestAttemptMatch = candidate;
                }
            }

            if (bestAttemptMatch is null)
            {
                _logger.Info($"Catalog resolver query '{attempt.Name}' returned no scored candidates for {track.UniqueKey}.");
                continue;
            }

            _logger.Info(
                $"Catalog resolver query '{attempt.Name}' best candidate for {track.UniqueKey}: " +
                $"{bestAttemptMatch.Value.Title} / {bestAttemptMatch.Value.Artist} (score={bestAttemptMatch.Value.Score}).");

            if (bestAttemptMatch.Value.Score >= MinimumAcceptedScore)
            {
                return bestAttemptMatch;
            }

            if (bestMatch is null || bestAttemptMatch.Value.CompareTo(bestMatch.Value) > 0)
            {
                bestMatch = bestAttemptMatch;
            }
        }

        if (bestMatch is not null)
        {
            _logger.Info(
                $"Catalog resolver rejected best candidate for {track.UniqueKey}: " +
                $"{bestMatch.Value.Title} / {bestMatch.Value.Artist} (score={bestMatch.Value.Score}, required={MinimumAcceptedScore}).");
        }

        return null;
    }

    internal async Task<CatalogSongDetail?> GetSongDetailAsync(
        string songId,
        string developerToken,
        CancellationToken cancellationToken)
    {
        var url = $"https://amp-api.music.apple.com/v1/catalog/{_storefront}/songs/{songId}?extend=extendedAssetUrls";
        var json = await FetchStringAsync(
            url,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {developerToken}",
                ["Origin"] = "https://music.apple.com",
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            return null;
        }

        return CatalogSongDetail.FromJson(data[0]);
    }

    internal static bool TryGetHighestLosslessVariant(string manifest, out int sampleRateHz, out int bitDepth)
    {
        sampleRateHz = 0;
        bitDepth = 0;

        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal) ||
                line.IndexOf("SAMPLE-RATE=", StringComparison.OrdinalIgnoreCase) < 0 ||
                line.IndexOf("BIT-DEPTH=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var sampleRate = TryParseManifestAttribute(line, "SAMPLE-RATE");
            var depth = TryParseManifestAttribute(line, "BIT-DEPTH");
            if (sampleRate is null || depth is null)
            {
                continue;
            }

            if (sampleRate > sampleRateHz || (sampleRate == sampleRateHz && depth > bitDepth))
            {
                sampleRateHz = sampleRate.Value;
                bitDepth = depth.Value;
            }
        }

        return sampleRateHz > 0 && bitDepth > 0;
    }

    private async Task<string?> FetchStringAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        return await _sendAsync(request, cancellationToken);
    }

    private static async Task<string?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WindowsLosslessSwitcher/1.0");
        return client;
    }

    private static bool TryParseJwtExpiryUtc(string token, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(PadBase64(segments[1].Replace('-', '+').Replace('_', '/')));
            using var document = JsonDocument.Parse(payloadBytes);
            if (!document.RootElement.TryGetProperty("exp", out var expElement))
            {
                return false;
            }

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expElement.GetInt64());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PadBase64(string value)
    {
        var remainder = value.Length % 4;
        return remainder == 0 ? value : value.PadRight(value.Length + (4 - remainder), '=');
    }

    private static int? TryParseManifestAttribute(string line, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            $"{name}=(?<value>\\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : null;
    }

    private static int ScoreMatch(TrackSnapshot track, string title, string artist, string album)
    {
        var titleScore = ScoreTitle(track.Title, title);
        if (titleScore < 0)
        {
            return -1;
        }

        // Classical tracks arrive with the COMPOSER in the artist field and "<performers> — <album>"
        // packed into the album field, whereas Apple's catalog lists the PERFORMERS as the artist.
        // So score the candidate's artist against either the (composer) artist field or the
        // performers parsed from the album, and score the album against either the whole field or its
        // album part. This stays strictly exact and recording-specific: a different recording has
        // different performers (and album) and will not reach MinimumAcceptedScore.
        var hasPerformers = AppleMusicTrackMetadataNormalizer.TrySplitOnDashSeparator(
            track.Album, out var performers, out var albumPart);

        var artistScore = Math.Max(
            ScoreArtist(track.Artist, artist),
            hasPerformers ? ScoreArtist(performers, artist) : -1);
        if (artistScore < 0)
        {
            return -1;
        }

        var albumScore = Math.Max(
            ScoreAlbum(track.Album, album),
            hasPerformers ? ScoreAlbum(albumPart, album) : 0);

        return titleScore + artistScore + albumScore;
    }

    private static int ScoreTitle(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return -1;
        }

        if (normalizedExpected == normalizedActual)
        {
            return 100;
        }

        return normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
               normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
            ? 70
            : -1;
    }

    private static int ScoreArtist(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return -1;
        }

        if (normalizedExpected == normalizedActual)
        {
            return 50;
        }

        if (normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
            normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            return 35;
        }

        return AppleMusicTrackMetadataNormalizer.SplitArtists(expected)
            .Overlaps(AppleMusicTrackMetadataNormalizer.SplitArtists(actual)) ? 25 : -1;
    }

    private static int ScoreAlbum(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return 0;
        }

        if (normalizedExpected == normalizedActual)
        {
            return 10;
        }

        return normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
               normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
            ? 5
            : 0;
    }

    private static IReadOnlyList<CatalogSearchAttempt> BuildSearchAttempts(TrackSnapshot track)
    {
        var normalized = AppleMusicTrackMetadataNormalizer.Normalize(track.Title, track.Artist, track.Album);
        var attempts = new List<CatalogSearchAttempt>();
        AddAttempt("title-primary-artist", $"{normalized.Title} {normalized.PrimaryArtist}");
        AddAttempt("title-artist", $"{normalized.Title} {normalized.Artist}");
        AddAttempt("title-album", $"{normalized.Title} {normalized.Album}");
        AddAttempt("title-only", normalized.Title);
        return attempts;

        void AddAttempt(string name, string query)
        {
            var trimmedQuery = query.Trim();
            if (string.IsNullOrWhiteSpace(trimmedQuery) ||
                attempts.Any(existing => string.Equals(existing.Query, trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            attempts.Add(new CatalogSearchAttempt(name, trimmedQuery));
        }
    }

    internal readonly record struct CatalogSongMatch(
        string Id,
        string Title,
        string Artist,
        string Album,
        int Score)
    {
        public int CompareTo(CatalogSongMatch other)
        {
            return Score.CompareTo(other.Score);
        }

        public static CatalogSongMatch? FromJson(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idElement) ||
                !element.TryGetProperty("attributes", out var attributes))
            {
                return null;
            }

            return new CatalogSongMatch(
                idElement.GetString() ?? string.Empty,
                attributes.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                attributes.TryGetProperty("artistName", out var artistElement) ? artistElement.GetString() ?? string.Empty : string.Empty,
                attributes.TryGetProperty("albumName", out var albumElement) ? albumElement.GetString() ?? string.Empty : string.Empty,
                0);
        }
    }

    internal readonly record struct CatalogSongDetail(
        string Id,
        string EnhancedHlsUrl,
        IReadOnlyList<string> AudioTraits)
    {
        public static CatalogSongDetail? FromJson(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idElement) ||
                !element.TryGetProperty("attributes", out var attributes))
            {
                return null;
            }

            var traits = Array.Empty<string>();
            if (attributes.TryGetProperty("audioTraits", out var audioTraitsElement) &&
                audioTraitsElement.ValueKind == JsonValueKind.Array)
            {
                traits = audioTraitsElement
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
            }

            var enhancedHlsUrl = string.Empty;
            if (attributes.TryGetProperty("extendedAssetUrls", out var extendedAssetUrls) &&
                extendedAssetUrls.TryGetProperty("enhancedHls", out var enhancedHlsElement))
            {
                enhancedHlsUrl = enhancedHlsElement.GetString() ?? string.Empty;
            }

            return new CatalogSongDetail(idElement.GetString() ?? string.Empty, enhancedHlsUrl, traits);
        }
    }

    internal readonly record struct CatalogSearchAttempt(string Name, string Query);
}
