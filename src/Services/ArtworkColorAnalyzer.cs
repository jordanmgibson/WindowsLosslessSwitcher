using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Extracts a dominant hue from album artwork for the hero glow and placeholder gradient. The
/// artwork is decoded tiny (24px), pixels vote for a hue weighted by how colorful they are, and
/// the result is re-emitted at a fixed, design-controlled saturation/brightness so any artwork
/// produces a glow in the same tonal range as the prototype's oklch(0.45 0.13 h) values.
/// </summary>
public static class ArtworkColorAnalyzer
{
    private const int SamplePixels = 24;

    public static Color? TryGetDominantColor(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var scale = SamplePixels / (double)Math.Max(1, Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource small = scale < 1
                ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                : frame;
            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            return ComputeDominantColor(pixels);
        }
        catch
        {
            // Any decode failure just means no glow tint; callers fall back to the default.
            return null;
        }
    }

    /// <summary>Hue vote over BGRA pixels; internal for direct unit testing without a codec.</summary>
    internal static Color? ComputeDominantColor(ReadOnlySpan<byte> bgraPixels)
    {
        double sumX = 0;
        double sumY = 0;
        double totalWeight = 0;

        for (var i = 0; i + 3 < bgraPixels.Length; i += 4)
        {
            var b = bgraPixels[i] / 255.0;
            var g = bgraPixels[i + 1] / 255.0;
            var r = bgraPixels[i + 2] / 255.0;
            var a = bgraPixels[i + 3] / 255.0;
            if (a < 0.5)
            {
                continue;
            }

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var chroma = max - min;
            if (chroma < 0.03 || max < 0.08)
            {
                // Near-grayscale or near-black pixels carry no hue information.
                continue;
            }

            var hue = ComputeHueDegrees(r, g, b, max, chroma);
            // Colorful mid-brightness pixels dominate the vote.
            var weight = chroma * max;
            var radians = hue * Math.PI / 180.0;
            sumX += Math.Cos(radians) * weight;
            sumY += Math.Sin(radians) * weight;
            totalWeight += weight;
        }

        if (totalWeight < 0.5)
        {
            return null;
        }

        var meanHue = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
        if (meanHue < 0)
        {
            meanHue += 360;
        }

        // Fixed tone: hue from the artwork, saturation/value from the design system.
        return FromHsv(meanHue, 0.55, 0.55);
    }

    private static double ComputeHueDegrees(double r, double g, double b, double max, double chroma)
    {
        double hue;
        if (max == r)
        {
            hue = ((g - b) / chroma % 6 + 6) % 6;
        }
        else if (max == g)
        {
            hue = (b - r) / chroma + 2;
        }
        else
        {
            hue = (r - g) / chroma + 4;
        }

        return hue * 60;
    }

    internal static Color FromHsv(double hueDegrees, double saturation, double value)
    {
        var c = value * saturation;
        var hPrime = hueDegrees / 60.0;
        var x = c * (1 - Math.Abs(hPrime % 2 - 1));
        var (r, g, b) = ((int)hPrime % 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = value - c;
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
