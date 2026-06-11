using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class FormatSelectionPolicy
{
    public static AudioFormatCandidate? SelectBest(
        ResolvedAudioFormat requestedFormat,
        AudioFormatCandidate? currentFormat,
        IReadOnlyList<AudioFormatCandidate> supportedFormats,
        bool switchBitDepth,
        int defaultBitDepth,
        bool preferClosestSampleRateMultiple)
    {
        if (supportedFormats.Count == 0)
        {
            return null;
        }

        var targetBitDepth = switchBitDepth
            ? requestedFormat.BitDepth
            : AppSettings.NormalizeBitDepth(defaultBitDepth);
        var supportedSampleRates = supportedFormats
            .Select(candidate => candidate.SampleRateHz)
            .Distinct()
            .ToList();

        var selectedSampleRate = supportedSampleRates
            .MinBy(sampleRateHz => Math.Abs((long)sampleRateHz - requestedFormat.SampleRateHz));

        if (preferClosestSampleRateMultiple &&
            selectedSampleRate != requestedFormat.SampleRateHz &&
            requestedFormat.SampleRateHz % 2 == 0)
        {
            var halfRate = requestedFormat.SampleRateHz / 2;
            if (supportedSampleRates.Contains(halfRate))
            {
                selectedSampleRate = halfRate;
            }
        }

        return supportedFormats
            .Where(candidate => candidate.SampleRateHz == selectedSampleRate)
            .MinBy(candidate => Math.Abs(candidate.BitDepth - targetBitDepth));
    }
}
