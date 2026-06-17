using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AppleMusicTrackMetadataNormalizerTests
{
    [Fact]
    public void Normalize_StripsLeadingByPrefixFromArtist()
    {
        var result = AppleMusicTrackMetadataNormalizer.Normalize("Some Song", "By Taylor Swift", "Midnights");

        Assert.Equal("Taylor Swift", result.Artist);
        Assert.Equal("Taylor Swift", result.PrimaryArtist);
    }

    [Fact]
    public void Normalize_StripIsCaseInsensitiveAndTrimsExtraWhitespace()
    {
        var result = AppleMusicTrackMetadataNormalizer.Normalize("Song", "by  Drake", "Album");

        Assert.Equal("Drake", result.Artist);
    }

    [Fact]
    public void Normalize_LeavesArtistsWithoutByPrefixUntouched()
    {
        var result = AppleMusicTrackMetadataNormalizer.Normalize("Song", "Byron Messia", "Album");

        // "Byron" must not be mangled into "ron": only a "By " prefix (with trailing space) is stripped.
        Assert.Equal("Byron Messia", result.Artist);
    }

    [Fact]
    public void Normalize_StripsByPrefixAfterSplittingArtistAndAlbumFromCombinedField()
    {
        // Apple Music on Windows packs "By <composer> — <performers> — <album>" into the artist field
        // with an empty album. The normalizer splits on the em-dash, then strips the "By " prefix.
        var result = AppleMusicTrackMetadataNormalizer.Normalize(
            "Symphony No. 5 in C Minor, Op. 67: I. Allegro con brio",
            "By Ludwig van Beethoven — Vienna Philharmonic & Carlos Kleiber — Beethoven: Symphony No. 5",
            string.Empty);

        Assert.Equal("Ludwig van Beethoven", result.Artist);
        Assert.StartsWith("Vienna Philharmonic", result.Album);
    }
}
