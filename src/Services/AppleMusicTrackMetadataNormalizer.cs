using System.Globalization;
using System.Text;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

internal static class AppleMusicTrackMetadataNormalizer
{
    private static readonly string[] ArtistSeparators =
    [
        " feat. ",
        " feat ",
        " featuring ",
        " ft. ",
        " ft ",
        " & ",
        ",",
        ";",
        "/",
        " x ",
        " and ",
    ];

    public static TrackSnapshot NormalizeSnapshot(TrackSnapshot snapshot)
    {
        var metadata = Normalize(snapshot.Title, snapshot.Artist, snapshot.Album);
        if (string.Equals(snapshot.Title, metadata.Title, StringComparison.Ordinal) &&
            string.Equals(snapshot.Artist, metadata.Artist, StringComparison.Ordinal) &&
            string.Equals(snapshot.Album, metadata.Album, StringComparison.Ordinal))
        {
            return snapshot;
        }

        return snapshot with
        {
            Title = metadata.Title,
            Artist = metadata.Artist,
            Album = metadata.Album,
        };
    }

    public static NormalizedTrackMetadata Normalize(string? title, string? artist, string? album)
    {
        var normalizedTitle = NormalizeWhitespace(title);
        var normalizedArtist = NormalizeWhitespace(artist);
        var normalizedAlbum = NormalizeWhitespace(album);

        if (string.IsNullOrWhiteSpace(normalizedAlbum) &&
            !string.IsNullOrWhiteSpace(normalizedArtist) &&
            TrySplitOnDashSeparator(normalizedArtist, out var splitArtist, out var splitAlbum))
        {
            normalizedArtist = splitArtist;
            normalizedAlbum = splitAlbum;
        }

        // Apple Music on Windows prefixes the GSMTC artist with "By " (the field arrives as
        // "By <artist name>"). Left in place it pollutes the catalog search term and drops artist
        // scoring from an exact match (50) to a mere substring match (35), which alone can sink an
        // otherwise-correct match.
        normalizedArtist = StripLeadingByPrefix(normalizedArtist);

        return new NormalizedTrackMetadata(
            normalizedTitle,
            normalizedArtist,
            normalizedAlbum,
            ExtractPrimaryArtist(normalizedArtist));
    }

    public static HashSet<string> SplitArtists(string? artist)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return results;
        }

        var segments = new List<string> { artist };
        foreach (var separator in ArtistSeparators)
        {
            segments = segments
                .SelectMany(segment => segment.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }

        foreach (var segment in segments)
        {
            var normalized = NormalizeForComparison(segment);
            if (!string.IsNullOrEmpty(normalized))
            {
                results.Add(normalized);
            }
        }

        return results;
    }

    public static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string StripLeadingByPrefix(string value)
    {
        const string prefix = "By ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].TrimStart()
            : value;
    }

    private static string ExtractPrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        foreach (var separator in ArtistSeparators)
        {
            var index = artist.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return artist[..index].Trim();
            }
        }

        return artist.Trim();
    }

    /// <summary>
    /// Splits a value on the first em-/en-dash separator into its leading and trailing parts.
    /// Apple Music packs "&lt;artist&gt; — &lt;album&gt;" into a single field when one is missing, and
    /// "&lt;performers&gt; — &lt;album&gt;" into the album field for classical tracks; both use this split.
    /// </summary>
    internal static bool TrySplitOnDashSeparator(string? value, out string before, out string after)
    {
        before = string.Empty;
        after = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var separator in new[] { " — ", " – " })
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length)
            {
                continue;
            }

            before = value[..index].Trim();
            after = value[(index + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
            {
                return true;
            }
        }

        return false;
    }
}

internal readonly record struct NormalizedTrackMetadata(
    string Title,
    string Artist,
    string Album,
    string PrimaryArtist);
