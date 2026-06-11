using System.Runtime.InteropServices;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

[Collection("UpdateEnvironment")]
public sealed class AppUpdaterTests
{
    [Theory]
    [InlineData(Architecture.X64, "win-x64")]
    [InlineData(Architecture.Arm64, "win-arm64")]
    [InlineData(Architecture.X86, "win-x64")]
    public void GetReleaseChannel_MapsArchitecturesToReleaseChannels(Architecture architecture, string expectedChannel)
    {
        Assert.Equal(expectedChannel, AppUpdater.GetReleaseChannel(architecture));
    }

    [Fact]
    public void SelectPortableAsset_ReturnsMatchingChannelizedPortablePackage()
    {
        var expected = new GitHubReleaseAssetInfo(
            "WindowsLosslessSwitcher-win-arm64-Portable.zip",
            "https://example.test/win-arm64");
        var assets = new[]
        {
            new GitHubReleaseAssetInfo(
                "WindowsLosslessSwitcher-win-x64-Portable.zip",
                "https://example.test/win-x64"),
            expected,
        };

        var selected = AppUpdater.SelectPortableAsset(assets, "win-arm64");

        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("v1.0.0-beta.1+abc123", "1.0.0")]
    [InlineData("not-a-version", null)]
    public void ParseVersion_ParsesTheVersionCore(string? input, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), AppUpdater.ParseVersion(input));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+abc123", false)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("1.0.0-beta.1+abc123", true)]
    public void IsPrereleaseVersionText_DetectsSemverPrereleaseSuffix(string? input, bool expected)
    {
        Assert.Equal(expected, AppUpdater.IsPrereleaseVersionText(input));
    }

    [Theory]
    [InlineData("1.0.0", "v1.0.0", true)] // stable matching the release
    [InlineData("1.0.0", "v0.9.0", true)] // release older than the build
    [InlineData("1.0.0", "v1.0.0-beta.1", true)] // stable is never downgraded to a prerelease
    [InlineData("1.0.0-beta.1", "v1.0.0-beta.1", true)] // prerelease matching the release
    [InlineData("1.0.0-beta.1", "v1.0.0", false)] // stable release supersedes the prerelease
    [InlineData("1.0.0-beta.1", "v1.0.0-beta.2", false)] // newer prerelease of the same core
    [InlineData("0.9.0", "v1.0.0", false)] // plain newer release
    public void IsPortableUpToDate_BreaksVersionCoreTiesByTagText(string current, string releaseTag, bool expected)
    {
        var result = AppUpdater.IsPortableUpToDate(
            AppUpdater.ParseVersion(current),
            current,
            AppUpdater.ParseVersion(releaseTag)!,
            releaseTag);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task InitializeAsync_SetsPortableStatusForPortablePackages()
    {
        var updater = CreateUpdater(new TestInstalledUpdateManager
        {
            IsPortable = true,
            CurrentVersion = new Version(0, 1, 0),
        });

        await updater.InitializeAsync(CancellationToken.None);

        Assert.True(updater.CurrentStatus.IsPortableBuild);
        Assert.Equal(UpdateActionKind.OpenReleasesPage, updater.CurrentStatus.PrimaryActionKind);
        Assert.Contains("Portable package detected", updater.CurrentStatus.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_PublishesPortableDownloadForMatchingAsset()
    {
        var updater = CreateUpdater(
            new TestInstalledUpdateManager
            {
                IsPortable = true,
                CurrentVersion = new Version(0, 1, 0),
            },
            new TestGitHubReleaseClient(new GitHubReleaseInfo(
                new Version(0, 2, 0),
                "v0.2.0",
                "https://example.test/releases/v0.2.0",
                new[]
                {
                    new GitHubReleaseAssetInfo(
                        "WindowsLosslessSwitcher-win-x64-Portable.zip",
                        "https://example.test/downloads/win-x64")
                })));

        await updater.CheckForUpdatesAsync(userInitiated: true, CancellationToken.None);

        Assert.Equal(UpdateActionKind.OpenPortableDownload, updater.CurrentStatus.PrimaryActionKind);
        Assert.Equal("https://example.test/downloads/win-x64", updater.CurrentStatus.DownloadUrl);
        Assert.Equal("0.2.0", updater.CurrentStatus.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShowsThePrereleaseTagForPortableUpdates()
    {
        var updater = CreateUpdater(
            new TestInstalledUpdateManager
            {
                IsPortable = true,
                CurrentVersion = new Version(0, 1, 0),
            },
            new TestGitHubReleaseClient(new GitHubReleaseInfo(
                new Version(1, 0, 0),
                "v1.0.0-beta.1",
                "https://example.test/releases/v1.0.0-beta.1",
                new[]
                {
                    new GitHubReleaseAssetInfo(
                        "WindowsLosslessSwitcher-win-x64-Portable.zip",
                        "https://example.test/downloads/win-x64")
                })));

        await updater.CheckForUpdatesAsync(userInitiated: true, CancellationToken.None);

        Assert.Equal(UpdateActionKind.OpenPortableDownload, updater.CurrentStatus.PrimaryActionKind);
        Assert.Equal("1.0.0-beta.1", updater.CurrentStatus.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_PublishesInstalledUpdateForInstalledBuild()
    {
        var updater = CreateUpdater(new TestInstalledUpdateManager
        {
            IsInstalled = true,
            CurrentVersion = new Version(0, 1, 0),
            AvailableUpdate = new InstalledUpdateInfo(new Version(0, 2, 0), null, new object()),
        });

        await updater.CheckForUpdatesAsync(userInitiated: true, CancellationToken.None);

        Assert.False(updater.CurrentStatus.IsPortableBuild);
        Assert.Equal(UpdateActionKind.DownloadAndPrepare, updater.CurrentStatus.PrimaryActionKind);
        Assert.Equal("Download update", updater.CurrentStatus.PrimaryActionLabel);
        Assert.Equal("0.2.0", updater.CurrentStatus.LatestVersion);
    }

    private static AppUpdater CreateUpdater(
        TestInstalledUpdateManager installedUpdateManager,
        TestGitHubReleaseClient? gitHubReleaseClient = null,
        TestLinkLauncher? linkLauncher = null)
    {
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.Tests", Guid.NewGuid().ToString("N")));
        return new AppUpdater(
            logger,
            installedUpdateManager,
            gitHubReleaseClient ?? new TestGitHubReleaseClient(null),
            linkLauncher ?? new TestLinkLauncher());
    }

    private sealed class TestInstalledUpdateManager : IInstalledUpdateManager
    {
        public bool IsInstalled { get; init; }

        public bool IsPortable { get; init; }

        public Version? CurrentVersion { get; init; }

        public string? CurrentVersionText { get; init; }

        public PreparedUpdateInfo? PendingRestart { get; init; }

        public InstalledUpdateInfo? AvailableUpdate { get; init; }

        public PreparedUpdateInfo? DownloadedUpdate { get; init; }

        public Task<InstalledUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken)
            => Task.FromResult(AvailableUpdate);

        public Task<PreparedUpdateInfo> DownloadUpdatesAsync(
            InstalledUpdateInfo update,
            Action<int> progressCallback,
            CancellationToken cancellationToken)
        {
            progressCallback(100);
            return Task.FromResult(DownloadedUpdate ?? new PreparedUpdateInfo(update.TargetVersion, new object()));
        }

        public void ApplyUpdatesAndRestart(PreparedUpdateInfo update, string[] restartArgs)
        {
        }
    }

    private sealed class TestGitHubReleaseClient(GitHubReleaseInfo? release) : IGitHubReleaseClient
    {
        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
            => Task.FromResult(release);
    }

    private sealed class TestLinkLauncher : ILinkLauncher
    {
        public string? LastOpenedUrl { get; private set; }

        public void Open(string url) => LastOpenedUrl = url;
    }
}
