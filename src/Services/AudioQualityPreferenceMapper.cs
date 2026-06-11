using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class AudioQualityPreferenceMapper
{
    public static ResolvedAudioFormat Map(IDictionary<string, object?> preferences)
    {
        var losslessEnabled = GetBoolean(preferences, "losslessEnabled");
        var qualityToken = GetQualityToken(preferences, "preferredStreamPlaybackAudioQuality");

        if (!losslessEnabled)
        {
            return new ResolvedAudioFormat(
                44100,
                16,
                ResolutionConfidence.Tier,
                AudioFormatSource.TierFallback,
                "Tier fallback: AAC");
        }

        if (IsHighRes(qualityToken))
        {
            return new ResolvedAudioFormat(
                192000,
                24,
                ResolutionConfidence.Tier,
                AudioFormatSource.TierFallback,
                "Tier fallback: Hi-Res Lossless");
        }

        return new ResolvedAudioFormat(
            48000,
            24,
            ResolutionConfidence.Tier,
            AudioFormatSource.TierFallback,
            "Tier fallback: Lossless");
    }

    private static bool GetBoolean(IDictionary<string, object?> preferences, string key)
    {
        if (!preferences.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool boolean => boolean,
            long number => number != 0,
            int number => number != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };
    }

    private static string? GetQualityToken(IDictionary<string, object?> preferences, string key)
    {
        if (!preferences.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text.Trim().ToLowerInvariant(),
            long number => $"numeric:{number}",
            int number => $"numeric:{number}",
            double number => $"numeric:{number:0}",
            _ => value.ToString()?.Trim().ToLowerInvariant(),
        };
    }

    private static bool IsHighRes(string? qualityToken)
    {
        if (string.IsNullOrWhiteSpace(qualityToken))
        {
            return false;
        }

        return qualityToken.Contains("hires", StringComparison.Ordinal) ||
               qualityToken.Contains("highlossless", StringComparison.Ordinal) ||
               qualityToken.Contains("high_lossless", StringComparison.Ordinal) ||
               qualityToken.Contains("hi-res", StringComparison.Ordinal) ||
               qualityToken is "numeric:2" or "numeric:3";
    }
}
