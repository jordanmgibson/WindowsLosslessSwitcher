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
        }
        finally
        {
            if (Directory.Exists(settingsDirectory))
            {
                Directory.Delete(settingsDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(45, 30)]
    [InlineData(100, 90)]
    [InlineData(0, 1)]
    public void SnapToNearestRefreshDaysPreset_SnapsUnsupportedValuesToNearestPreset(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.SnapToNearestRefreshDaysPreset(input));
    }
}
