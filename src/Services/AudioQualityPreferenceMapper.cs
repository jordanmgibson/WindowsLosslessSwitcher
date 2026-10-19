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
        // return a low-confidence marker. Its legacy numeric fields are not a measurement;
        // the coordinator keeps the current endpoint format and the UI displays Undetermined.
        return CreateUndetermined(
            $"Rate undetermined (Apple Music quality: {tierName}) — keeping the current device format");
    }

    /// <summary>
    /// Builds an undetermined-rate marker; its legacy numeric fields must not drive a switch.
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
