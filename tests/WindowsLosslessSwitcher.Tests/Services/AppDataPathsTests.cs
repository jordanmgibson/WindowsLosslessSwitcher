using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "WindowsLosslessSwitcher.Tests",
        Guid.NewGuid().ToString("N"));

    private string LegacyRoot => Path.Combine(_testRoot, "legacy");

    private string NewRoot => Path.Combine(_testRoot, "new");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void MigrateFromLegacyLocation_MovesSettingsAndLogs()
    {
        Directory.CreateDirectory(Path.Combine(LegacyRoot, "logs"));
        File.WriteAllText(Path.Combine(LegacyRoot, "settings.json"), "{\"launchAtLogin\":true}");
        File.WriteAllText(Path.Combine(LegacyRoot, "logs", "switcher.log"), "old entries");

        AppDataPaths.MigrateFromLegacyLocation(LegacyRoot, NewRoot);

        Assert.Equal("{\"launchAtLogin\":true}", File.ReadAllText(Path.Combine(NewRoot, "settings.json")));
        Assert.Equal("old entries", File.ReadAllText(Path.Combine(NewRoot, "logs", "switcher.log")));
        Assert.False(File.Exists(Path.Combine(LegacyRoot, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(LegacyRoot, "logs")));
    }

    [Fact]
    public void MigrateFromLegacyLocation_DoesNotOverwriteExistingFilesAtNewLocation()
    {
        Directory.CreateDirectory(Path.Combine(LegacyRoot, "logs"));
        File.WriteAllText(Path.Combine(LegacyRoot, "settings.json"), "legacy settings");
        File.WriteAllText(Path.Combine(LegacyRoot, "logs", "switcher.log"), "legacy log");
        Directory.CreateDirectory(Path.Combine(NewRoot, "logs"));
        File.WriteAllText(Path.Combine(NewRoot, "settings.json"), "new settings");
        File.WriteAllText(Path.Combine(NewRoot, "logs", "switcher.log"), "new log");

        AppDataPaths.MigrateFromLegacyLocation(LegacyRoot, NewRoot);

        Assert.Equal("new settings", File.ReadAllText(Path.Combine(NewRoot, "settings.json")));
        Assert.Equal("new log", File.ReadAllText(Path.Combine(NewRoot, "logs", "switcher.log")));
        Assert.Equal("legacy settings", File.ReadAllText(Path.Combine(LegacyRoot, "settings.json")));
        Assert.Equal("legacy log", File.ReadAllText(Path.Combine(LegacyRoot, "logs", "switcher.log")));
    }

    [Fact]
    public void MigrateFromLegacyLocation_LeavesVelopackInstallContentInPlace()
    {
        Directory.CreateDirectory(Path.Combine(LegacyRoot, "current"));
        Directory.CreateDirectory(Path.Combine(LegacyRoot, "packages"));
        File.WriteAllText(Path.Combine(LegacyRoot, "Update.exe"), "stub");
        File.WriteAllText(Path.Combine(LegacyRoot, "settings.json"), "{}");

        AppDataPaths.MigrateFromLegacyLocation(LegacyRoot, NewRoot);

        Assert.True(Directory.Exists(Path.Combine(LegacyRoot, "current")));
        Assert.True(Directory.Exists(Path.Combine(LegacyRoot, "packages")));
        Assert.True(File.Exists(Path.Combine(LegacyRoot, "Update.exe")));
        Assert.True(File.Exists(Path.Combine(NewRoot, "settings.json")));
    }

    [Fact]
    public void MigrateFromLegacyLocation_NoOpWhenLegacyRootMissing()
    {
        AppDataPaths.MigrateFromLegacyLocation(LegacyRoot, NewRoot);

        Assert.False(Directory.Exists(NewRoot));
    }

    [Fact]
    public void MigrateFromLegacyLocation_NoOpWhenLegacyRootHasNoDataFiles()
    {
        Directory.CreateDirectory(Path.Combine(LegacyRoot, "current"));

        AppDataPaths.MigrateFromLegacyLocation(LegacyRoot, NewRoot);

        Assert.False(Directory.Exists(NewRoot));
        Assert.True(Directory.Exists(Path.Combine(LegacyRoot, "current")));
    }
}
