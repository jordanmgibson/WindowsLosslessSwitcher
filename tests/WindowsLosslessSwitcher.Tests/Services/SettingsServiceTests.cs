using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsOriginalTargetSnapshot()
    {
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "WindowsLosslessSwitcher.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SettingsService(settingsDirectory);
            var settings = new AppSettings
            {
                DeviceSelectionMode = DeviceSelectionMode.PinnedDevice,
                PinnedDeviceId = "device-1",
                LaunchAtLogin = true,
                SwitchBitDepth = false,
                DefaultBitDepth = 16,
                EnableSwitchToasts = true,
                IncludeTrackMetadataInSwitchToasts = true,
                FormatCacheRefreshDays = 60,
                AllowedSampleRates = [44100, 48000, 96000],
                AllowedBitDepths = [16, 24],
                AppleMusicStorefront = "gb",
                EnableVerboseDiagnostics = false,
                RestartAppleMusicOnPlaybackFailure = false,
                OriginalTarget = new OriginalTargetSnapshot(
                    "device-1",
                    "USB DAC",
                    96000,
                    24,
                    2,
                    new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero)),
            };

            service.Save(settings);

            var json = File.ReadAllText(service.GetSettingsPath());
            var loaded = service.Load();

            Assert.Contains("\"originalTarget\"", json);
            Assert.DoesNotContain("originalTargetDeviceId", json, StringComparison.Ordinal);
            Assert.Equal(settings.OriginalTarget, loaded.OriginalTarget);
            Assert.Equal(60, loaded.FormatCacheRefreshDays);
            Assert.Equal([44100, 48000, 96000], loaded.AllowedSampleRates);
            Assert.Equal([16, 24], loaded.AllowedBitDepths);
            Assert.Equal("gb", loaded.AppleMusicStorefront);
            Assert.False(loaded.EnableVerboseDiagnostics);
            Assert.False(loaded.RestartAppleMusicOnPlaybackFailure);
        }
        finally
        {
            if (Directory.Exists(settingsDirectory))
            {
                Directory.Delete(settingsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void AllowedSampleRates_DropsInvalidEntriesAndNormalizesOrder()
    {
        // A hand-edited settings file may contain junk; the setter keeps only positive, distinct
        // rates in ascending order, and null resets to unrestricted.
        var settings = new AppSettings { AllowedSampleRates = [96000, -5, 0, 44100, 96000] };
        Assert.Equal([44100, 96000], settings.AllowedSampleRates);

        settings.AllowedSampleRates = null!;
        Assert.Empty(settings.AllowedSampleRates);

        var depths = new AppSettings { AllowedBitDepths = [32, 0, -1, 16, 32] };
        Assert.Equal([16, 32], depths.AllowedBitDepths);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(45, 30)]
    [InlineData(100, 90)]
    [InlineData(0, 1)]
    // Out-of-range values come from hand-edited or corrupt settings files; int.MinValue would
    // overflow an int distance and invert the snap.
    [InlineData(-5, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, 365)]
    public void SnapToNearestRefreshDaysPreset_SnapsUnsupportedValuesToNearestPreset(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.SnapToNearestRefreshDaysPreset(input));
    }
}
