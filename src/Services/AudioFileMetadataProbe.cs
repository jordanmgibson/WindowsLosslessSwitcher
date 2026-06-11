namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Stream properties extracted from a single audio file. BitDepth is zero for lossy codecs that do
/// not expose one (MP3/AAC).
/// </summary>
public sealed record AudioFileProbeResult(
    int SampleRateHz,
    int BitDepth);

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
            using var file = TagLib.File.Create(
                new ReadSharingFileAbstraction(filePath),
                mimetype: null,
                TagLib.ReadStyle.Average);
            var properties = file.Properties;
            return new AudioFileProbeResult(
                properties?.AudioSampleRate ?? 0,
                properties?.BitsPerSample ?? 0);
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
