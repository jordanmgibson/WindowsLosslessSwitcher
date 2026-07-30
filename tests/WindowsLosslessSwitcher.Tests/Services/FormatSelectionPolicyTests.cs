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

    // ── allowedSampleRates (issue #7) ─────────────────────────────────────────
    // A virtual cable between the switched endpoint and the physical DAC reports rates the real
    // hardware never locks to; the allow-list clamps the applied rate to what the DAC accepts.

    [Theory]
    [InlineData(88200, 44100)]  // exact ÷2 family beats the arithmetically nearer 96 kHz
    [InlineData(176400, 44100)] // repeated halving: 176.4 → 88.2 (not allowed) → 44.1
    [InlineData(192000, 96000)] // ÷2 family
    [InlineData(384000, 96000)] // repeated halving: 384 → 192 (not allowed) → 96
    [InlineData(96000, 96000)]  // rate already allowed is untouched
    public void SelectBest_AllowedSampleRates_ClampsToFamilyRateFirst(int requestedRate, int expectedRate)
    {
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(requestedRate, 24),
            currentFormat: new AudioFormatCandidate(48000, 24, 2),
            supportedFormats: CreateWideOpenFormats(),
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false,
            allowedSampleRates: [44100, 48000, 96000]);

        Assert.Equal(new AudioFormatCandidate(expectedRate, 24, 2), selected);
    }

    [Fact]
    public void SelectBest_AllowedSampleRates_FallsBackToNearestWhenNoFamilyRateIsAllowed()
    {
        // 88.2 with only 48/96 allowed: no halved rate matches, so nearest-by-distance wins (96).
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(88200, 24),
            currentFormat: null,
            supportedFormats: CreateWideOpenFormats(),
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false,
            allowedSampleRates: [48000, 96000]);

        Assert.Equal(new AudioFormatCandidate(96000, 24, 2), selected);
    }

    [Fact]
    public void SelectBest_AllowedSampleRatesMatchingNothing_FallsBackToUnrestrictedSelection()
    {
        // A misconfigured list (no overlap with the device) must never dead-end the switch.
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(96000, 24),
            currentFormat: null,
            supportedFormats:
            [
                new(44100, 24, 2),
                new(96000, 24, 2),
            ],
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false,
            allowedSampleRates: [22050]);

        Assert.Equal(new AudioFormatCandidate(96000, 24, 2), selected);
    }

    [Fact]
    public void SelectBest_WithoutAllowedSampleRates_DoesNotPreferFamilyOverBandwidth()
    {
        // The family-halving preference is gated to the allow-list path: without a list, a 176.4
        // request on a device topping out at 96 must land on 96, not divide down to 44.1.
        var selected = FormatSelectionPolicy.SelectBest(
            CreateRequested(176400, 24),
            currentFormat: null,
            supportedFormats:
            [
                new(44100, 24, 2),
                new(48000, 24, 2),
                new(96000, 24, 2),
            ],
            switchBitDepth: true,
            defaultBitDepth: 24,
            preferClosestSampleRateMultiple: false);

        Assert.Equal(new AudioFormatCandidate(96000, 24, 2), selected);
    }

    private static List<AudioFormatCandidate> CreateWideOpenFormats() =>
        // Mimics a virtual cable claiming everything from 44.1 to 384.
        [
            new(44100, 24, 2),
            new(48000, 24, 2),
            new(88200, 24, 2),
            new(96000, 24, 2),
            new(176400, 24, 2),
            new(192000, 24, 2),
            new(352800, 24, 2),
            new(384000, 24, 2),
        ];

    private static ResolvedAudioFormat CreateRequested(int sampleRateHz, int bitDepth) =>
        new(sampleRateHz, bitDepth, ResolutionConfidence.Exact, AudioFormatSource.CatalogManifest, "test");
}
