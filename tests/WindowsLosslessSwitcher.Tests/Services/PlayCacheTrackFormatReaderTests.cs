using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class PlayCacheTrackFormatReaderTests : IDisposable
{
    private const string LibraryId = "000000000C719DA9";

    private readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "wls-playcache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void DownloadTempFolderMatchingTitle_ReturnsFileFormat()
    {
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ My Album.tmp\\download.mp3");
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "My Song"));

        Assert.NotNull(result);
        Assert.Equal(44100, result!.SampleRateHz);
        Assert.Equal(0, result.BitDepth);
        Assert.Equal(path, result.FilePath);
    }

    [Fact]
    public void TruncatedDownloadFolder_MatchesTitlePrefix()
    {
        // Apple Music truncates the tmp folder name to ~31 characters.
        var folder = "Runner [V1] (feat. Lil Uzi Vert";
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\{folder}.tmp\\download.mp3");
        var probe = new StubProbe { [path] = new AudioFileProbeResult(48000, 0) };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "Runner [V1] (feat. Lil Uzi Vert)"));

        Assert.NotNull(result);
        Assert.Equal(48000, result!.SampleRateHz);
    }

    [Fact]
    public void TitleWithInvalidFileNameCharacters_IsSanitizedBeforeMatching()
    {
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\Clean _ Money Can't Buy Dreams.tmp\\download.mp3");
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "Clean / Money Can't Buy Dreams"));

        Assert.NotNull(result);
        Assert.Equal(44100, result!.SampleRateHz);
    }

    [Fact]
    public void ShortFolderName_DoesNotMatchLongerTitlePrefix()
    {
        // "Clean" must not match a folder for the track "Cleaner".
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\Cleaner _ Album.tmp\\download.mp3", ageMinutes: 10);
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);

        Assert.Null(reader.TryReadFormat(CreateTrack(title: "Clean")));
    }

    [Fact]
    public void FreshlyWrittenFile_MatchesUnidentifiedLocalTrack()
    {
        // Replayed/cached local tracks leave no Downloads folder and PlayCacheInfo.xml is flushed
        // far too rarely to help — but the track's own (re)download completes right at track
        // start, so a just-written media file identifies it.
        var stale = CreateCacheFile($"{LibraryId}\\02\\00\\00\\{LibraryId}-0000000000000457.mp3", ageMinutes: 10);
        var fresh = CreateCacheFile($"{LibraryId}\\02\\00\\01\\{LibraryId}-00000000000004D2.mp3");
        var probe = new StubProbe
        {
            [stale] = new AudioFileProbeResult(44100, 0),
            [fresh] = new AudioFileProbeResult(48000, 0),
        };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "Some Replayed Song"));

        Assert.NotNull(result);
        Assert.Equal(fresh, result!.FilePath);
        Assert.Equal(48000, result.SampleRateHz);
    }

    [Fact]
    public void FileHeldOpenByAnotherProcess_MatchesCurrentTrack()
    {
        // The media agent keeps an open handle on the file it is playing or loading; that handle
        // is the strongest replay signal even when the file was written long ago.
        var path = CreateCacheFile($"{LibraryId}\\02\\00\\00\\{LibraryId}-0000000000000457.mp3", ageMinutes: 10);
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = reader.TryReadFormat(CreateTrack(title: "Some Replayed Song"));

        Assert.NotNull(result);
        Assert.Equal(path, result!.FilePath);
        Assert.Equal(44100, result.SampleRateHz);
    }

    [Fact]
    public void MostRecentlyWrittenMatchingDownload_Wins()
    {
        var older = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ Old.tmp\\download.mp3");
        var newer = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ New.tmp\\download.m4a");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-7));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var probe = new StubProbe
        {
            [older] = new AudioFileProbeResult(44100, 0),
            [newer] = new AudioFileProbeResult(96000, 24),
        };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "My Song"));

        Assert.NotNull(result);
        Assert.Equal(newer, result!.FilePath);
        Assert.Equal(24, result.BitDepth);
    }

    [Fact]
    public void LockedDownloadFile_IsSkippedAndRetriedNextLookup()
    {
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ My Album.tmp\\download.mp3", ageMinutes: 10);
        var probe = new StubProbe
        {
            [path] = new AudioFileProbeResult(44100, 0),
            ThrowIoExceptionOnce = true,
        };
        var reader = CreateReader(probe);
        var track = CreateTrack(title: "My Song");

        // First lookup hits the lock and falls through; the failure must not be cached.
        Assert.Null(reader.TryReadFormat(track));
        Assert.NotNull(reader.TryReadFormat(track));
    }

    [Fact]
    public void ProbeResults_AreCachedForUnchangedFiles()
    {
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ My Album.tmp\\download.mp3");
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);
        var track = CreateTrack(title: "My Song");

        Assert.NotNull(reader.TryReadFormat(track));
        Assert.NotNull(reader.TryReadFormat(track));

        Assert.Equal(1, probe.ProbeCounts[path]);
    }

    [Fact]
    public void FreshCacheInfoEntry_ResolvesFileByCloudIdSuffix()
    {
        // cloud-id 304158 = 0x4A41E -> file suffix 000000000004A41E.
        var path = CreateCacheFile($"{LibraryId}\\02\\00\\00\\{LibraryId}-000000000004A41E.mp3");
        WriteCacheInfo(accessDateUtc: DateTimeOffset.UtcNow, cloudId: 304158);
        var probe = new StubProbe { [path] = new AudioFileProbeResult(48000, 0) };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "My Song", detectedAtUtc: DateTimeOffset.UtcNow));

        Assert.NotNull(result);
        Assert.Equal(48000, result!.SampleRateHz);
        Assert.Equal(path, result.FilePath);
    }

    [Fact]
    public void StaleCacheInfoEntry_IsIgnored()
    {
        var path = CreateCacheFile($"{LibraryId}\\02\\00\\00\\{LibraryId}-000000000004A41E.mp3", ageMinutes: 10);
        WriteCacheInfo(accessDateUtc: DateTimeOffset.UtcNow.AddHours(-1), cloudId: 304158);
        var probe = new StubProbe { [path] = new AudioFileProbeResult(48000, 0) };
        var reader = CreateReader(probe);

        Assert.Null(reader.TryReadFormat(CreateTrack(title: "My Song", detectedAtUtc: DateTimeOffset.UtcNow)));
        Assert.Empty(probe.ProbeCounts);
    }

    [Fact]
    public void NewestCacheInfoEntry_IsSelected()
    {
        var older = CreateCacheFile($"{LibraryId}\\01\\{LibraryId}-0000000000000457.mp3", ageMinutes: 10);
        var newer = CreateCacheFile($"{LibraryId}\\02\\{LibraryId}-00000000000004D2.m4a", ageMinutes: 10);
        WriteCacheInfo(
            (DateTimeOffset.UtcNow.AddMinutes(-30), 1111),
            (DateTimeOffset.UtcNow, 1234));
        var probe = new StubProbe
        {
            [older] = new AudioFileProbeResult(44100, 0),
            [newer] = new AudioFileProbeResult(96000, 24),
        };
        var reader = CreateReader(probe);

        var result = reader.TryReadFormat(CreateTrack(title: "My Song", detectedAtUtc: DateTimeOffset.UtcNow));

        Assert.NotNull(result);
        Assert.Equal(newer, result!.FilePath);
    }

    [Fact]
    public void MissingCacheDirectory_ReturnsNull()
    {
        var reader = CreateReader(new StubProbe());

        Assert.Null(reader.TryReadFormat(CreateTrack(title: "My Song")));
    }

    [Fact]
    public void UntitledTrack_ReturnsNullWithoutScanning()
    {
        var path = CreateCacheFile($"{LibraryId}\\Downloads\\My Song _ My Album.tmp\\download.mp3");
        var probe = new StubProbe { [path] = new AudioFileProbeResult(44100, 0) };
        var reader = CreateReader(probe);

        Assert.Null(reader.TryReadFormat(CreateTrack(title: null)));
        Assert.Empty(probe.ProbeCounts);
    }

    private PlayCacheTrackFormatReader CreateReader(StubProbe probe)
    {
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new PlayCacheTrackFormatReader(_cacheDirectory, probe, logger);
    }

    private string CreateCacheFile(string relativePath, int ageMinutes = 0)
    {
        var path = Path.Combine(_cacheDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00]);
        if (ageMinutes > 0)
        {
            // Old enough to defeat the freshly-written match — models a replayed cache file.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
        }

        return path;
    }

    private void WriteCacheInfo(DateTimeOffset accessDateUtc, long cloudId) =>
        WriteCacheInfo([(accessDateUtc, cloudId)]);

    private void WriteCacheInfo(params (DateTimeOffset AccessDateUtc, long CloudId)[] items)
    {
        var entries = string.Join(string.Empty, items.Select(item => $"""
        <dict>
            <key>access-date</key>
            <date>{item.AccessDateUtc:yyyy-MM-ddTHH:mm:ssZ}</date>
            <key>cloud-id</key>
            <integer>{item.CloudId}</integer>
            <key>file-size</key>
            <integer>1</integer>
        </dict>
"""));
        Directory.CreateDirectory(_cacheDirectory);
        File.WriteAllText(
            Path.Combine(_cacheDirectory, "PlayCacheInfo.xml"),
            $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>cache-size</key>
    <integer>624090552</integer>
    <key>items</key>
    <array>
{entries}
    </array>
</dict>
</plist>
""");
    }

    private static TrackSnapshot CreateTrack(
        string? title,
        string? artist = "Artist",
        string? album = null,
        DateTimeOffset? detectedAtUtc = null) =>
        new(
            "AppleInc.AppleMusicWin_nzyj5cx40ttqa",
            null,
            title,
            artist,
            album,
            "test",
            detectedAtUtc ?? DateTimeOffset.UtcNow);

    private sealed class StubProbe : IAudioFileMetadataProbe
    {
        private readonly Dictionary<string, AudioFileProbeResult?> _results = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> ProbeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowIoExceptionOnce { get; set; }

        public AudioFileProbeResult? this[string path]
        {
            set => _results[path] = value;
        }

        public AudioFileProbeResult? Probe(string filePath)
        {
            if (ThrowIoExceptionOnce)
            {
                ThrowIoExceptionOnce = false;
                throw new IOException("file locked");
            }

            ProbeCounts[filePath] = ProbeCounts.GetValueOrDefault(filePath) + 1;
            return _results.GetValueOrDefault(filePath);
        }
    }
}
