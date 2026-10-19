using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WindowsLosslessSwitcher.Converters;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class UiFormattingTests
{
    [Theory]
    [InlineData(44100, "en-US", "44.1 kHz")]
    [InlineData(96000, "en-US", "96 kHz")]
    [InlineData(176400, "en-US", "176.4 kHz")]
    [InlineData(705600, "en-US", "705.6 kHz")]
    [InlineData(44100, "de-DE", "44,1 kHz")]
    public void FormatSelector_UsesReadableUnitsAndLocalDecimalSeparator(int rate, string culture, string expected)
    {
        var converter = new SampleRateLabelConverter();
        Assert.Equal(expected, converter.Convert(rate, typeof(string), null!, CultureInfo.GetCultureInfo(culture)));
        Assert.Same(System.Windows.Data.Binding.DoNothing, converter.ConvertBack(expected, typeof(int), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ContrastTheme_RestoresDefaultResourcesAndPreservesUnrelatedValues()
    {
        var defaultBrush = new SolidColorBrush(Colors.Purple);
        var resources = new ResourceDictionary { MergedDictionaries = { new ResourceDictionary { ["Nocturne.Brush.Text"] = defaultBrush } }, ["Other"] = "preserved" };
        AccessibleTheme.Apply(resources, true);
        Assert.Same(SystemColors.WindowTextBrush, resources["Nocturne.Brush.Text"]);
        AccessibleTheme.Apply(resources, false);
        Assert.Same(defaultBrush, resources["Nocturne.Brush.Text"]);
        Assert.Equal("preserved", resources["Other"]);
    }

    [Fact]
    public void SecondInstance_RequestsTheExistingWindowInsteadOfStartingAnotherInstance()
    {
        var name = @"Local\WLS.UiTest." + Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim();
        using var primary = new StartupInstanceGuard(name);
        primary.ActivationRequested += (_, _) => activated.Set();
        Assert.True(primary.TryAcquire());
        (bool Acquired, bool Signaled) request = default;
        // A named mutex is thread-owned. Keep acquire/dispose on this thread and perform
        // the second-instance request on a distinct thread rather than using reentrancy.
        var requester = new Thread(() =>
        {
            using var secondary = new StartupInstanceGuard(name);
            request = (secondary.TryAcquire(), secondary.RequestActivation());
        }) { IsBackground = true };
        requester.Start();
        Assert.True(requester.Join(TimeSpan.FromSeconds(3)));
        Assert.False(request.Acquired);
        Assert.True(request.Signaled);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }
}
