using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class ArtworkColorAnalyzerTests
{
    private static byte[] SolidBgra(byte b, byte g, byte r, int pixels = 64)
    {
        var buffer = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            buffer[i * 4] = b;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = r;
            buffer[i * 4 + 3] = 255;
        }

        return buffer;
    }

    [Fact]
    public void ComputeDominantColor_RedArtworkYieldsRedDominantHue()
    {
        var color = ArtworkColorAnalyzer.ComputeDominantColor(SolidBgra(0, 0, 220));

        Assert.NotNull(color);
        Assert.True(color!.Value.R > color.Value.G && color.Value.R > color.Value.B);
    }

    [Fact]
    public void ComputeDominantColor_BlueArtworkYieldsBlueDominantHue()
    {
        var color = ArtworkColorAnalyzer.ComputeDominantColor(SolidBgra(220, 40, 10));

        Assert.NotNull(color);
        Assert.True(color!.Value.B > color.Value.R && color.Value.B > color.Value.G);
    }

    [Fact]
    public void ComputeDominantColor_GrayscaleArtworkYieldsNull()
    {
        Assert.Null(ArtworkColorAnalyzer.ComputeDominantColor(SolidBgra(128, 128, 128)));
        Assert.Null(ArtworkColorAnalyzer.ComputeDominantColor(SolidBgra(5, 5, 5)));
    }

    [Fact]
    public void ComputeDominantColor_FixedToneKeepsGlowInDesignRange()
    {
        var color = ArtworkColorAnalyzer.ComputeDominantColor(SolidBgra(0, 255, 0));

        Assert.NotNull(color);
        var max = Math.Max(color!.Value.R, Math.Max(color.Value.G, color.Value.B));
        // Value is clamped to 0.55 → brightest channel ≈ 140.
        Assert.InRange(max, 120, 160);
    }

    [Fact]
    public void TryGetDominantColor_GarbageBytesReturnNull()
    {
        Assert.Null(ArtworkColorAnalyzer.TryGetDominantColor([1, 2, 3, 4]));
    }
}
