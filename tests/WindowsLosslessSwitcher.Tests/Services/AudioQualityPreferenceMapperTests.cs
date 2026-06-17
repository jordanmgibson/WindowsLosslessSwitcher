using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AudioQualityPreferenceMapperTests
{
    [Theory]
    [InlineData(false, null)]                 // AAC (lossless off)
    [InlineData(true, "lossless")]            // standard lossless
    [InlineData(true, "hires_lossless")]      // hi-res lossless
    [InlineData(true, "numeric:3")]           // hi-res via numeric token
    public void Map_AlwaysReturnsUndetermined24At44100(bool losslessEnabled, string? qualityToken)
    {
        var preferences = new Dictionary<string, object?>
        {
            ["losslessEnabled"] = losslessEnabled,
        };
        if (qualityToken is not null)
        {
            preferences["preferredStreamPlaybackAudioQuality"] = qualityToken;
        }

        var result = AudioQualityPreferenceMapper.Map(preferences);

        Assert.Equal(44100, result.SampleRateHz);
        Assert.Equal(24, result.BitDepth);
        Assert.Equal(ResolutionConfidence.Tier, result.Confidence);
        Assert.Equal(AudioFormatSource.TierFallback, result.Source);
    }

    [Fact]
    public void Map_DescriptionNamesTheConfiguredTier()
    {
        var preferences = new Dictionary<string, object?>
        {
            ["losslessEnabled"] = true,
            ["preferredStreamPlaybackAudioQuality"] = "hires_lossless",
        };

        var result = AudioQualityPreferenceMapper.Map(preferences);

        Assert.Contains("Hi-Res Lossless", result.Description);
    }

    [Fact]
    public void CreateUndetermined_UsesConservative24At44100TierFallback()
    {
        var result = AudioQualityPreferenceMapper.CreateUndetermined("test");

        Assert.Equal(44100, result.SampleRateHz);
        Assert.Equal(24, result.BitDepth);
        Assert.Equal(ResolutionConfidence.Tier, result.Confidence);
        Assert.Equal(AudioFormatSource.TierFallback, result.Source);
        Assert.Equal("test", result.Description);
    }
}
