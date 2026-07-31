using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class SpectrumAnalyzerTests
{
    private const int SampleRate = 48000;

    private static float[] StereoSine(double frequencyHz, double seconds, float amplitude = 1f)
    {
        var frames = (int)(SampleRate * seconds);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = amplitude * (float)Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    private static int DominantBand(float[] bars)
    {
        var best = 0;
        for (var i = 1; i < bars.Length; i++)
        {
            if (bars[i] > bars[best])
            {
                best = i;
            }
        }

        return best;
    }

    [Fact]
    public void FullScaleSine1kHz_DominatesItsBandStrongly()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = StereoSine(1000, 0.5);

        analyzer.ProcessSamples(samples, samples.Length);

        var bars = analyzer.LatestBars;
        // 1 kHz falls in band 3 (890–2330 Hz).
        Assert.Equal(3, DominantBand(bars));
        Assert.True(bars[3] > 0.6, $"expected a strong bar, got {bars[3]}");
        Assert.False(analyzer.LastHopWasSilent);
    }

    [Fact]
    public void LowFrequencySine_LandsInFirstBand()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = StereoSine(100, 0.5);

        analyzer.ProcessSamples(samples, samples.Length);

        Assert.Equal(0, DominantBand(analyzer.LatestBars));
    }

    [Fact]
    public void HighFrequencySine_LandsInLastBand()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = StereoSine(10000, 0.5);

        analyzer.ProcessSamples(samples, samples.Length);

        Assert.Equal(5, DominantBand(analyzer.LatestBars));
    }

    [Fact]
    public void Silence_KeepsAllBarsAtZeroAndReportsSilent()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = new float[SampleRate]; // 0.5 s of stereo silence

        analyzer.ProcessSamples(samples, samples.Length);

        Assert.All(analyzer.LatestBars, bar => Assert.Equal(0f, bar));
        Assert.True(analyzer.LastHopWasSilent);
        Assert.True(analyzer.SecondsSinceNonSilent > 0.2);
    }

    [Fact]
    public void WhiteNoise_LightsEveryBand()
    {
        var analyzer = new SpectrumAnalyzer();
        var random = new Random(42);
        var samples = new float[SampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;
        }

        analyzer.ProcessSamples(samples, samples.Length);

        Assert.All(analyzer.LatestBars, bar => Assert.InRange(bar, 0.05f, 1f));
    }

    [Fact]
    public void SineThenSilence_BarsDecaySmoothly()
    {
        var analyzer = new SpectrumAnalyzer();
        var tone = StereoSine(1000, 0.5);
        analyzer.ProcessSamples(tone, tone.Length);
        var loud = analyzer.LatestBars[3];

        // 150 ms of silence: decayed but not gone (τ ≈ 300 ms).
        var shortSilence = new float[(int)(SampleRate * 0.15) * 2];
        analyzer.ProcessSamples(shortSilence, shortSilence.Length);
        var decayed = analyzer.LatestBars[3];
        Assert.True(decayed < loud, $"expected decay: {loud} → {decayed}");
        Assert.True(decayed > 0.1f * loud, $"decayed too fast: {loud} → {decayed}");

        // A second and a half of silence: visually gone.
        var longSilence = new float[(int)(SampleRate * 1.5) * 2];
        analyzer.ProcessSamples(longSilence, longSilence.Length);
        Assert.True(analyzer.LatestBars[3] < 0.1f);
    }

    [Fact]
    public void OddPacketSizes_AccumulateCorrectly()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = StereoSine(1000, 0.5);
        var offset = 0;
        var chunks = new[] { 62, 1000, 3, 4096, 511 };
        var chunkIndex = 0;
        while (offset < samples.Length)
        {
            var take = Math.Min(chunks[chunkIndex++ % chunks.Length] * 2, samples.Length - offset);
            var slice = new float[take];
            Array.Copy(samples, offset, slice, 0, take);
            analyzer.ProcessSamples(slice, take);
            offset += take;
        }

        Assert.Equal(3, DominantBand(analyzer.LatestBars));
        Assert.True(analyzer.LatestBars[3] > 0.6);
    }

    [Fact]
    public void Reset_ClearsBarsAndSilenceState()
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = StereoSine(1000, 0.5);
        analyzer.ProcessSamples(samples, samples.Length);
        Assert.True(analyzer.LatestBars[3] > 0);

        analyzer.Reset();

        Assert.All(analyzer.LatestBars, bar => Assert.Equal(0f, bar));
        Assert.True(double.IsPositiveInfinity(analyzer.SecondsSinceNonSilent));
    }
}
