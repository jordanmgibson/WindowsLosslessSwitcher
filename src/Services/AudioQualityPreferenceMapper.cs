using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class AudioQualityPreferenceMapper
{
    public static ResolvedAudioFormat Map(IDictionary<string, object?> preferences)
    {
        var losslessEnabled = GetBoolean(preferences, "losslessEnabled");
        var qualityToken = GetQualityToken(preferences, "preferredStreamPlaybackAudioQuality");

        var tierName = !losslessEnabled
            ? "AAC"
            : IsHighRes(qualityToken) ? "Hi-Res Lossless" : "Lossless";

        // The tier preference only tells us the user's ceiling, not the playing track's real rate.
        // Emitting a tier-specific guess (e.g. 48 kHz) misrepresents that rate and forces the device
        // there, upsampling redbook and capping hi-res. When we genuinely cannot determine the rate
        // we return an honest, conservative 24/44.1 and let the UI tell the user it's a fallback.
        return CreateUndetermined(
            $"Rate undetermined (Apple Music quality: {tierName}) — defaulting to 24/44.1");
    }

    /// <summary>
    /// Builds the conservative "we could not determine the real track rate" fallback format.
    /// </summary>
    internal static ResolvedAudioFormat CreateUndetermined(string description) =>
        new(
            44100,
            24,
            ResolutionConfidence.Tier,
            AudioFormatSource.TierFallback,
            description);

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
