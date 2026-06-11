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
            TrySplitArtistAndAlbum(normalizedArtist, out var splitArtist, out var splitAlbum))
        {
            normalizedArtist = splitArtist;
            normalizedAlbum = splitAlbum;
        }

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

    private static bool TrySplitArtistAndAlbum(string value, out string artist, out string album)
    {
        artist = string.Empty;
        album = string.Empty;

        foreach (var separator in new[] { " — ", " – " })
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length)
            {
                continue;
            }

            artist = value[..index].Trim();
            album = value[(index + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
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
