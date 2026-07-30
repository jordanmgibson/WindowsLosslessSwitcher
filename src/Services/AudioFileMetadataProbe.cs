namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Stream properties extracted from a single audio file. BitDepth is zero for lossy codecs that do
/// not expose one (MP3/AAC). Codec is a short label ("ALAC", "AAC", "MP3", or null when unknown)
/// used in diagnostics to explain which depth path was taken.
/// </summary>
public sealed record AudioFileProbeResult(
    int SampleRateHz,
    int BitDepth,
    string? Codec = null);

/// <summary>
/// Extracts stream properties from an audio file on disk.
/// </summary>
public interface IAudioFileMetadataProbe
{
    /// <summary>
    /// Probes the file, returning null when the format is unsupported or the file is corrupt.
    /// Throws <see cref="System.IO.IOException"/> when the file cannot be opened (e.g. locked by
    /// Apple Music while playing) so callers can retry on a later lookup.
    /// </summary>
    AudioFileProbeResult? Probe(string filePath);
}

/// <summary>
/// TagLib-backed probe. TagLib reads only headers, so probing is cheap even for large files.
/// </summary>
public sealed class TagLibAudioFileMetadataProbe : IAudioFileMetadataProbe
{
    public AudioFileProbeResult? Probe(string filePath)
    {
        try
        {
            int sampleRate;
            int taglibBitDepth;
            using (var file = TagLib.File.Create(
                new ReadSharingFileAbstraction(filePath),
                mimetype: null,
                TagLib.ReadStyle.Average))
            {
                var properties = file.Properties;
                sampleRate = properties?.AudioSampleRate ?? 0;
                taglibBitDepth = properties?.BitsPerSample ?? 0;
            }

            var (codec, bitDepth) = ResolveCodecAndDepth(filePath, taglibBitDepth);
            return new AudioFileProbeResult(sampleRate, bitDepth, codec);
        }
        catch (TagLib.CorruptFileException)
        {
            return null;
        }
        catch (TagLib.UnsupportedFormatException)
        {
            return null;
        }
    }

    // TagLib reads MP4 bit depth from the legacy sample-entry samplesize field (often 16 even for
    // 24-bit ALAC) and reports 0 for AAC, so for .m4a we consult the ALAC magic cookie: a value
    // there means the stream is lossless ALAC and gives its true depth; its absence means AAC.
    private static (string? Codec, int BitDepth) ResolveCodecAndDepth(string filePath, int taglibBitDepth)
    {
        var extension = System.IO.Path.GetExtension(filePath);

        if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase))
        {
            var alacDepth = TryReadAlacBitDepth(filePath);
            return alacDepth is int depth ? ("ALAC", depth) : ("AAC", taglibBitDepth);
        }

        // WAV/AIFF (and MP3) expose bit depth directly in their headers, so TagLib's value is
        // trustworthy as-is.
        var codec = extension?.ToLowerInvariant() switch
        {
            ".mp3" => "MP3",
            ".wav" => "WAV",
            ".aiff" or ".aif" => "AIFF",
            _ => null,
        };
        return (codec, taglibBitDepth);
    }

    private static int? TryReadAlacBitDepth(string filePath)
    {
        try
        {
            return AlacBitDepthReader.TryReadBitDepth(filePath);
        }
        catch (System.IO.IOException)
        {
            // File momentarily locked: keep TagLib's value for this lookup rather than failing the
            // whole probe (a later lookup re-reads once the file is free).
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Apple Music keeps the playing PlayCache file open with write access, so the read stream must
    // allow write/delete sharing or the open fails even though the bytes are readable.
    private sealed class ReadSharingFileAbstraction(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;

        public System.IO.Stream ReadStream =>
            new System.IO.FileStream(
                path,
                System.IO.FileMode.Open,
                System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);

        public System.IO.Stream WriteStream => throw new NotSupportedException();

        public void CloseStream(System.IO.Stream stream) => stream.Dispose();
    }
}
