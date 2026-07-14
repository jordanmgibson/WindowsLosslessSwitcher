using System.Text;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

internal static class FormatCacheKey
{
    public static string Create(string storefront, TrackSnapshot track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storefront);
        ArgumentNullException.ThrowIfNull(track);

        var normalizedTrack = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(track);
        var builder = new StringBuilder();
        Append(builder, storefront.Trim().ToLowerInvariant());
        Append(builder, normalizedTrack.SourceAppUserModelId);
        Append(builder, normalizedTrack.TrackId);
        Append(builder, normalizedTrack.Title);
        Append(builder, normalizedTrack.Artist);
        Append(builder, normalizedTrack.Album);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value);
    }
}
