namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Represents the audio format read from a local track file. A <see cref="BitDepth"/> of zero means
/// the container does not expose one (lossy codecs such as MP3/AAC).
/// </summary>
public sealed record LocalTrackFileFormat(
    int SampleRateHz,
    int BitDepth,
    string FilePath);
