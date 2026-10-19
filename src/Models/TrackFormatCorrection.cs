using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.Models;

/// <summary>A user-verified native player format, scoped to one recording/album.</summary>
public sealed record TrackFormatCorrection(string Title, string Artist, string Album, int SampleRateHz, int BitDepth)
{
    public static IReadOnlyList<int> SupportedRates { get; } =
        [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];

    public bool IsValid => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Artist) &&
        !string.IsNullOrWhiteSpace(Album) && SupportedRates.Contains(SampleRateHz) && BitDepth is 16 or 24 or 32;

    public bool Matches(TrackSnapshot track)
    {
        var normalized = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(track);
        return IsValid && Equal(Title, normalized.Title) && Equal(Artist, normalized.Artist) && Equal(Album, normalized.Album);
    }

    public static TrackFormatCorrection FromTrack(TrackSnapshot track, int sampleRateHz, int bitDepth)
    {
        var normalized = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(track);
        return new(normalized.Title ?? "", normalized.Artist ?? "", normalized.Album ?? "", sampleRateHz, bitDepth);
    }

    public static ResolvedAudioFormat? Resolve(IEnumerable<TrackFormatCorrection?> corrections, TrackSnapshot track)
    {
        var correction = corrections.LastOrDefault(candidate => candidate?.Matches(track) == true);
        return correction is null ? null : new(correction.SampleRateHz, correction.BitDepth,
            ResolutionConfidence.Exact, AudioFormatSource.UserVerified,
            $"Native player correction: {correction.BitDepth}/{correction.SampleRateHz / 1000.0:0.###}");
    }

    private static bool Equal(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(right) &&
        AppleMusicTrackMetadataNormalizer.NormalizeForComparison(left) ==
        AppleMusicTrackMetadataNormalizer.NormalizeForComparison(right);
}
