using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class FormatSelectionPolicyTests
{
    [Fact]
    public void SelectBest_24BitTrackWith24BitSupport_Selects24Bit()
    {
        // Issue #1 regression scenario: once 24-bit is detected, a 24/48 track must apply as 24/48.
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(48000, 24),
            currentFormat: new AudioFormatCandidate(48000, 16, 2),
            supportedFormats:
            [
                new(44100, 16, 2),
                new(48000, 16, 2),
                new(48000, 24, 2),
            ],
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false);

        Assert.Equal(new AudioFormatCandidate(48000, 24, 2), selected);
    }

    [Fact]
    public void SelectBest_Only16BitSupported_FallsBackTo16Bit()
    {
        // Documents the pre-fix behavior on affected DACs: with no 24-bit candidates detected,
        // the nearest available depth is 16.
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(48000, 24),
            currentFormat: new AudioFormatCandidate(48000, 16, 2),
            supportedFormats:
            [
                new(44100, 16, 2),
                new(48000, 16, 2),
            ],
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false);

        Assert.Equal(new AudioFormatCandidate(48000, 16, 2), selected);
    }

    [Fact]
    public void SelectBest_BitDepthSwitchingDisabled_UsesDefaultBitDepth()
    {
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(44100, 16),
            currentFormat: new AudioFormatCandidate(48000, 16, 2),
            supportedFormats:
            [
                new(44100, 16, 2),
                new(44100, 24, 2),
            ],
            switchBitDepth: false,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false);

        Assert.Equal(new AudioFormatCandidate(44100, 24, 2), selected);
    }

    private static ResolvedAudioFormat CreateRequested(int sampleRateHz, int bitDepth) =>
        new(sampleRateHz, bitDepth, ResolutionConfidence.Exact, AudioFormatSource.CatalogManifest, "test");
}
