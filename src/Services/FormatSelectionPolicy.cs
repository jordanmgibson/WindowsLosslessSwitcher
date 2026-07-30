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
        bool preferClosestSampleRateMultiple,
        IReadOnlyCollection<int>? allowedSampleRates = null,
        IReadOnlyCollection<int>? allowedBitDepths = null)
    {
        if (supportedFormats.Count == 0)
        {
            return null;
        }

        // User-declared hardware allow-lists (issue #7): a virtual cable or bridge between the
        // switched endpoint and the physical DAC reports its own inflated capabilities, so a rate
        // or depth the real hardware never locks to would otherwise be applied. Restrict the
        // candidate pool before rate selection; each list independently falls back to the
        // unrestricted pool when nothing matches, so a misconfigured list degrades to today's
        // behavior instead of dead-ending the switch. Depths are restricted before the rate is
        // chosen so a rate whose only candidates carry disallowed depths is never selected.
        var effectiveFormats = supportedFormats;
        var allowListApplied = false;
        var allowList = allowedSampleRates?.Where(rate => rate > 0).ToHashSet();
        if (allowList is { Count: > 0 })
        {
            var restricted = supportedFormats
                .Where(candidate => allowList.Contains(candidate.SampleRateHz))
                .ToList();
            if (restricted.Count > 0)
            {
                effectiveFormats = restricted;
                allowListApplied = true;
            }
        }

        var depthAllowList = allowedBitDepths?.Where(depth => depth > 0).ToHashSet();
        if (depthAllowList is { Count: > 0 })
        {
            var depthRestricted = effectiveFormats
                .Where(candidate => depthAllowList.Contains(candidate.BitDepth))
                .ToList();
            if (depthRestricted.Count > 0)
            {
                effectiveFormats = depthRestricted;
            }
        }

        var targetBitDepth = switchBitDepth
            ? requestedFormat.BitDepth
            : AppSettings.NormalizeBitDepth(defaultBitDepth);
        var supportedSampleRates = effectiveFormats
            .Select(candidate => candidate.SampleRateHz)
            .Distinct()
            .ToList();

        var selectedSampleRate = supportedSampleRates
            .MinBy(sampleRateHz => Math.Abs((long)sampleRateHz - requestedFormat.SampleRateHz));

        // When the allow-list forces a clamp, prefer the closest same-family rate (repeated ÷2:
        // 176400 → 88200 → 44100) over the arithmetically nearest one: 88.2 kHz clamped to 96
        // resamples at a messy non-integer ratio, while 44.1 is an exact half. Deliberately gated
        // to the allow-list path — without a list, bandwidth wins over family (a 176.4 file on a
        // 44.1/96 device should land on 96, not divide down to 44.1).
        if (allowListApplied && selectedSampleRate != requestedFormat.SampleRateHz)
        {
            var familyRate = requestedFormat.SampleRateHz;
            while (familyRate % 2 == 0 && familyRate > 8000)
            {
                familyRate /= 2;
                if (supportedSampleRates.Contains(familyRate))
                {
                    selectedSampleRate = familyRate;
                    break;
                }
            }
        }

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

        return effectiveFormats
            .Where(candidate => candidate.SampleRateHz == selectedSampleRate)
            .MinBy(candidate => Math.Abs(candidate.BitDepth - targetBitDepth));
    }
}
