using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Locates the PlayCache file backing a local/cloud-library track and reports the file's actual
/// sample rate and bit depth. PlayCache files carry no tags and GSMTC exposes no track id or file
/// path, so two joins are used, in order:
/// <list type="number">
/// <item>In-progress downloads live under <c>Downloads\&lt;Title&gt; _ &lt;Album&gt;.tmp\download.mp3</c>
/// (the folder name is truncated to ~31 characters), so the folder name is matched against the
/// track title.</item>
/// <item>Completed downloads are renamed to <c>&lt;libraryId&gt;-&lt;cloudId hex&gt;.mp3/.m4a</c>;
/// <c>PlayCacheInfo.xml</c> records each item's cloud-id with a play access-date, so the newest
/// entry identifies the current track's file when its access-date is fresh.</item>
/// </list>
/// Probe results are cached per file (keyed by size + write time), so unchanged files are probed
/// only once across lookups.
/// </summary>
public sealed class PlayCacheTrackFormatReader : ILocalTrackFileFormatReader
{
    private static readonly string[] CandidateExtensions = [".mp3", ".m4a"];

    // Observed Apple Music truncation length for Downloads\*.tmp folder names. At or beyond this
    // length the folder name may be a cut-off prefix of "<Title> _ <Album>".
    private const int TruncatedTmpNameLength = 30;

    // A PlayCacheInfo.xml entry is only trusted when its access-date is no older than the moment
    // the track was detected (with slack for clock skew and flush latency); otherwise it may still
    // describe the previous track.
    private static readonly TimeSpan AccessDateSlack = TimeSpan.FromMinutes(2);

    // A cache file written within this window of the track's detection counts as the track's own
    // (re)download completing. Live sessions show one media-file write per local track, right
    // around its start; PlayCacheInfo.xml, by contrast, is flushed far too rarely to be useful.
    private static readonly TimeSpan FreshWriteSlack = TimeSpan.FromSeconds(15);

    private readonly string _playCacheDirectory;
    private readonly IAudioFileMetadataProbe _probe;
    private readonly DiagnosticsLogger _logger;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, CachedProbe> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public PlayCacheTrackFormatReader(AppleMusicPaths paths, DiagnosticsLogger logger)
        : this(paths.PlayCacheDirectory, new TagLibAudioFileMetadataProbe(), logger)
    {
    }

    internal PlayCacheTrackFormatReader(
        string playCacheDirectory,
        IAudioFileMetadataProbe probe,
        DiagnosticsLogger logger)
    {
        _playCacheDirectory = playCacheDirectory;
        _probe = probe;
        _logger = logger;
    }

    public LocalTrackFileFormat? TryReadFormat(TrackSnapshot track)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || !Directory.Exists(_playCacheDirectory))
        {
            return null;
        }

        try
        {
            return MatchDownloadTemp(track)
                ?? MatchInUseOrFreshlyWrittenFile(track)
                ?? MatchRecentCacheInfoEntry(track);
        }
        catch (Exception ex)
        {
            _logger.Warn($"PlayCache lookup failed under '{_playCacheDirectory}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Matches replayed cache files (no Downloads folder, stale PlayCacheInfo.xml — the common
    /// case for local tracks played before). Two signals, in confidence order: a file the media
    /// agent currently holds OPEN is the one it is playing or loading; failing that, a file
    /// written right at track start is this track's own download completing.
    /// </summary>
    private LocalTrackFileFormat? MatchInUseOrFreshlyWrittenFile(TrackSnapshot track)
    {
        var mediaFiles = Directory.EnumerateFiles(_playCacheDirectory, "*", SearchOption.AllDirectories)
            .Where(path => CandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var file in mediaFiles.Where(IsHeldOpenByAnotherProcess))
        {
            var probed = GetOrProbe(file);
            if (probed is null || probed.SampleRateHz <= 0)
            {
                continue;
            }

            _logger.Info($"PlayCache match for '{track.Title}': file in use by Apple Music ({file.Name}) -> {probed.BitDepth}/{probed.SampleRateHz}.");
            return new LocalTrackFileFormat(probed.SampleRateHz, probed.BitDepth, file.FullName);
        }

        var freshCutoff = (track.DetectedAtUtc - FreshWriteSlack).UtcDateTime;
        foreach (var file in mediaFiles.Where(file => file.LastWriteTimeUtc >= freshCutoff))
        {
            var probed = GetOrProbe(file);
            if (probed is null || probed.SampleRateHz <= 0)
            {
                continue;
            }

            _logger.Info($"PlayCache match for '{track.Title}': freshly written file ({file.Name}, {file.LastWriteTimeUtc:O}) -> {probed.BitDepth}/{probed.SampleRateHz}.");
            return new LocalTrackFileFormat(probed.SampleRateHz, probed.BitDepth, file.FullName);
        }

        return null;
    }

    private static bool IsHeldOpenByAnotherProcess(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private LocalTrackFileFormat? MatchDownloadTemp(TrackSnapshot track)
    {
        var candidates = Directory.EnumerateFiles(_playCacheDirectory, "download.*", SearchOption.AllDirectories)
            .Where(path => CandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => file.Directory?.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(file => file.LastWriteTimeUtc);

        foreach (var file in candidates)
        {
            var folderName = file.Directory!.Name[..^".tmp".Length];
            if (!TitleMatchesFolder(track.Title, track.Album, folderName))
            {
                continue;
            }

            var probed = GetOrProbe(file);
            if (probed is null || probed.SampleRateHz <= 0)
            {
                continue;
            }

            _logger.Info($"PlayCache match for '{track.Title}': download folder '{folderName}' -> {probed.BitDepth}/{probed.SampleRateHz}.");
            return new LocalTrackFileFormat(probed.SampleRateHz, probed.BitDepth, file.FullName);
        }

        return null;
    }

    private LocalTrackFileFormat? MatchRecentCacheInfoEntry(TrackSnapshot track)
    {
        var entry = ReadNewestCacheInfoEntry();
        if (entry is null)
        {
            return null;
        }

        if (entry.AccessDateUtc < track.DetectedAtUtc - AccessDateSlack)
        {
            _logger.Info($"PlayCache info entry is stale ({entry.AccessDateUtc:O} vs track detected {track.DetectedAtUtc:O}); ignoring.");
            return null;
        }

        var suffix = $"-{entry.CloudId:X16}";
        var file = Directory.EnumerateFiles(_playCacheDirectory, "*", SearchOption.AllDirectories)
            .Where(path => CandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        var probed = GetOrProbe(file);
        if (probed is null || probed.SampleRateHz <= 0)
        {
            return null;
        }

        _logger.Info($"PlayCache match for '{track.Title}': cloud-id {entry.CloudId} ({file.Name}) -> {probed.BitDepth}/{probed.SampleRateHz}.");
        return new LocalTrackFileFormat(probed.SampleRateHz, probed.BitDepth, file.FullName);
    }

    private CacheInfoEntry? ReadNewestCacheInfoEntry()
    {
        var infoPath = Path.Combine(_playCacheDirectory, "PlayCacheInfo.xml");
        if (!File.Exists(infoPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                infoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // The plist declares an external Apple DTD; it must be ignored, not fetched.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            var document = XDocument.Load(reader);

            CacheInfoEntry? newest = null;
            foreach (var dict in document.Descendants("array").SelectMany(array => array.Elements("dict")))
            {
                DateTimeOffset? accessDate = null;
                long cloudId = 0;
                var children = dict.Elements().ToList();
                for (var i = 0; i + 1 < children.Count; i += 2)
                {
                    var key = children[i].Value;
                    var value = children[i + 1].Value;
                    if (key == "access-date" &&
                        DateTimeOffset.TryParse(
                            value,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsedDate))
                    {
                        accessDate = parsedDate;
                    }
                    else if (key == "cloud-id" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                    {
                        cloudId = parsedId;
                    }
                }

                if (accessDate is not null && cloudId > 0 &&
                    (newest is null || accessDate > newest.AccessDateUtc))
                {
                    newest = new CacheInfoEntry(accessDate.Value, cloudId);
                }
            }

            return newest;
        }
        catch (Exception ex)
        {
            _logger.Info($"PlayCacheInfo.xml is not readable right now: {ex.Message}");
            return null;
        }
    }

    private AudioFileProbeResult? GetOrProbe(FileInfo file)
    {
        lock (_cacheSync)
        {
            if (_probeCache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.Result;
            }
        }

        AudioFileProbeResult? result;
        try
        {
            result = _probe.Probe(file.FullName);
        }
        catch (IOException ex)
        {
            // Apple Music holds the playing file locked; skip without caching so a later lookup
            // (file unlocked) can succeed.
            _logger.Info($"PlayCache file '{file.Name}' is not readable right now: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Info($"PlayCache file '{file.Name}' is not accessible: {ex.Message}");
            return null;
        }

        lock (_cacheSync)
        {
            _probeCache[file.FullName] = new CachedProbe(file.Length, file.LastWriteTimeUtc, result);
        }

        return result;
    }

    internal static bool TitleMatchesFolder(string? title, string? album, string folderName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var sanitizedTitle = SanitizeForFileName(title.Trim());
        if (folderName.Equals(sanitizedTitle, StringComparison.OrdinalIgnoreCase) ||
            folderName.StartsWith(sanitizedTitle + " _ ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // At the truncation length the folder name is a cut-off prefix of "<Title> _ <Album>"
        // (or of a long title alone). Requiring the full folder name as prefix keeps this strict.
        if (folderName.Length >= TruncatedTmpNameLength)
        {
            var combined = string.IsNullOrWhiteSpace(album)
                ? sanitizedTitle
                : $"{sanitizedTitle} _ {SanitizeForFileName(album.Trim())}";
            return combined.StartsWith(folderName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string SanitizeForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record CacheInfoEntry(DateTimeOffset AccessDateUtc, long CloudId);

    private sealed record CachedProbe(long Length, DateTime LastWriteTimeUtc, AudioFileProbeResult? Result);
}
