using WindowsLosslessSwitcher.Models;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class TrackFormatCorrectionTests
{
    private static readonly TrackSnapshot Track = new("AppleMusic.exe", null, "Dirty Diana", "Michael Jackson", "Number Ones", "test", DateTimeOffset.UtcNow);

    [Fact]
    public void NativeCorrection_UsesVerified96kInsteadOfWeb48k()
    {
        var result = TrackFormatCorrection.Resolve([new("Dirty Diana", "Michael Jackson", "Number Ones", 96000, 24)], Track);
        Assert.NotNull(result);
        Assert.Equal(96000, result.SampleRateHz);
        Assert.Equal(AudioFormatSource.UserVerified, result.Source);
    }

    [Theory]
    [InlineData("Dirty Diana", "Michael Jackson", "Bad")]
    [InlineData("Dirty Diana (Live)", "Michael Jackson", "Number Ones")]
    [InlineData("Dirty Diana", "Another Artist", "Number Ones")]
    [InlineData("Dirty Diana", "Michael Jackson", "")]
    public void Correction_DoesNotLeakIntoOtherRecordings(string title, string artist, string album)
    {
        Assert.Null(TrackFormatCorrection.Resolve([new("Dirty Diana", "Michael Jackson", "Number Ones", 96000, 24)],
            Track with { Title = title, Artist = artist, Album = album }));
    }

    [Fact]
    public void Correction_HandlesNativeCombinedArtistAlbumMetadata()
    {
        var raw = Track with { Artist = "Michael Jackson — Number Ones", Album = "" };
        var correction = TrackFormatCorrection.FromTrack(raw, 96000, 24);
        Assert.True(correction.Matches(Track));
        Assert.Equal("Michael Jackson", correction.Artist);
        Assert.Equal("Number Ones", correction.Album);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(-96000, 24)]
    [InlineData(12345, 24)]
    [InlineData(96000, 0)]
    [InlineData(96000, 64)]
    public void CorruptCorrection_IsIgnored(int rate, int depth)
    {
        Assert.Null(TrackFormatCorrection.Resolve([null, new("Dirty Diana", "Michael Jackson", "Number Ones", rate, depth)], Track));
    }

    [Fact]
    public void LatestValidCorrectionWins()
    {
        var result = TrackFormatCorrection.Resolve([
            new("Dirty Diana", "Michael Jackson", "Number Ones", 48000, 24),
            new("Dirty Diana", "Michael Jackson", "Number Ones", 96000, 24)], Track);
        Assert.Equal(96000, result!.SampleRateHz);
    }
}
