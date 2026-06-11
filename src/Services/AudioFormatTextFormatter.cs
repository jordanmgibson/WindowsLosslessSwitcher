using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Formats audio-related values for the UI, tray menu, and diagnostics.
/// </summary>
public static class AudioFormatTextFormatter
{
    private const string NoSupportedFormatsText = "No supported formats detected.";

    public static string Format(AudioFormatCandidate format) =>
        $"{format.BitDepth}-bit / {FormatSampleRate(format.SampleRateHz)}";

    public static string Format(ResolvedAudioFormat format) =>
        $"{format.BitDepth}-bit / {FormatSampleRate(format.SampleRateHz)}";

    public static string FormatOrUnknown(AudioFormatCandidate? format) =>
        format is null ? "Unknown" : Format(format);

    public static string BuildTrayCurrentFormatText(AudioFormatCandidate? format) =>
        $"Current format: {FormatOrUnknown(format)}";

    public static string BuildSupportedSampleRatesText(IReadOnlyList<AudioFormatCandidate> formats) =>
        formats.Count == 0
            ? NoSupportedFormatsText
            : string.Join(", ", formats
                .Select(format => format.SampleRateHz)
                .Distinct()
                .OrderBy(sampleRateHz => sampleRateHz)
                .Select(FormatSampleRate));

    public static string BuildSupportedBitDepthsText(IReadOnlyList<AudioFormatCandidate> formats) =>
        formats.Count == 0
            ? NoSupportedFormatsText
            : string.Join(", ", formats
                .Select(format => format.BitDepth)
                .Distinct()
                .OrderBy(bitDepth => bitDepth)
                .Select(bitDepth => $"{bitDepth}-bit"));

    public static string BuildSupportedFormatsText(IReadOnlyList<AudioFormatCandidate> formats) =>
        formats.Count == 0
            ? NoSupportedFormatsText
            : string.Join(", ", formats
                .Distinct()
                .OrderBy(format => format.SampleRateHz)
                .ThenBy(format => format.BitDepth)
                .Select(Format));

    private static string FormatSampleRate(int sampleRateHz) => $"{sampleRateHz / 1000.0:0.###} kHz";
}
