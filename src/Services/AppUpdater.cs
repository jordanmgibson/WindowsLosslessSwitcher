using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Velopack;
using Velopack.Sources;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Bridges GitHub Releases, Velopack, and the app UI for update checks.
/// </summary>
public sealed class AppUpdater : IAppUpdater
{
    private readonly DiagnosticsLogger _logger;
    private readonly IInstalledUpdateManager _installedUpdateManager;
    private readonly IGitHubReleaseClient _gitHubReleaseClient;
    private readonly ILinkLauncher _linkLauncher;
    private readonly string _currentVersion;
    private InstalledUpdateInfo? _availableInstalledUpdate;
    private GitHubReleaseInfo? _availablePortableRelease;

    /// <summary>
    /// Creates an updater instance backed by Velopack and GitHub Releases.
    /// </summary>
    public AppUpdater(DiagnosticsLogger logger)
        : this(
            logger,
            new VelopackInstalledUpdateManager(),
            new GitHubReleaseClient(),
            new LinkLauncher())
    {
    }

    internal AppUpdater(
        DiagnosticsLogger logger,
        IInstalledUpdateManager installedUpdateManager,
        IGitHubReleaseClient gitHubReleaseClient,
        ILinkLauncher linkLauncher)
    {
        _logger = logger;
        _installedUpdateManager = installedUpdateManager;
        _gitHubReleaseClient = gitHubReleaseClient;
        _linkLauncher = linkLauncher;
        _currentVersion = ResolveCurrentVersion(installedUpdateManager);
        CurrentStatus = BuildInitialStatus();
    }

    /// <inheritdoc />
    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    /// <inheritdoc />
    public UpdateStatusSnapshot CurrentStatus { get; private set; }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReleaseRepositoryOptions.IsConfigured)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    $"Updates are not configured yet. {ReleaseRepositoryOptions.ConfigurationInstructions}",
                    UpdateActionKind.None,
                    null,
                    false,
                    false,
                    false,
                    false,
                    _installedUpdateManager.IsPortable));
            return Task.CompletedTask;
        }

        if (_installedUpdateManager.PendingRestart is { } pendingRestart)
        {
            _availableInstalledUpdate = null;
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    $"Version {pendingRestart.Version} is ready to apply.",
                    UpdateActionKind.RestartToApply,
                    "Restart to apply",
                    true,
                    true,
                    true,
                    false,
                    _installedUpdateManager.IsPortable,
                    pendingRestart.Version.ToString()));
            return Task.CompletedTask;
        }

        if (_installedUpdateManager.IsPortable)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "Portable package detected. Check GitHub Releases to download newer portable packages.",
                    UpdateActionKind.OpenReleasesPage,
                    "Open releases",
                    true,
                    true,
                    true,
                    false,
                    true));
            return Task.CompletedTask;
        }

        if (_installedUpdateManager.IsInstalled)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "Installed build ready. Automatic update checks are available.",
                    UpdateActionKind.None,
                    null,
                    true,
                    false,
                    true,
                    false,
                    false));
            return Task.CompletedTask;
        }

        Publish(
            new UpdateStatusSnapshot(
                _currentVersion,
                "Development build detected. Release updates are unavailable until the app is packaged.",
                UpdateActionKind.OpenReleasesPage,
                "Open releases",
                false,
                true,
                true,
                false,
                false));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task CheckForUpdatesAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReleaseRepositoryOptions.IsConfigured)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    $"Updates are not configured yet. {ReleaseRepositoryOptions.ConfigurationInstructions}",
                    UpdateActionKind.None,
                    null,
                    false,
                    false,
                    false,
                    false,
                    _installedUpdateManager.IsPortable));
            return;
        }

        if (!_installedUpdateManager.IsInstalled && !_installedUpdateManager.IsPortable)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "This build is not running from a packaged install or portable release, so update checks are disabled.",
                    UpdateActionKind.OpenReleasesPage,
                    "Open releases",
                    false,
                    true,
                    true,
                    false,
                    false));
            return;
        }

        Publish(CurrentStatus with
        {
            StatusText = "Checking GitHub Releases for updates...",
            IsBusy = true,
            CanCheckForUpdates = false,
            CanRunPrimaryAction = false,
            ProgressPercent = null,
        });

        try
        {
            if (_installedUpdateManager.IsPortable)
            {
                await CheckPortableUpdatesAsync(cancellationToken);
            }
            else
            {
                await CheckInstalledUpdatesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed.", ex);
            Publish(
                CurrentStatus with
                {
                    StatusText = $"Update check failed: {ex.Message}",
                    IsBusy = false,
                    CanCheckForUpdates = _installedUpdateManager.IsInstalled || _installedUpdateManager.IsPortable,
                    CanRunPrimaryAction = CurrentStatus.PrimaryActionKind is not UpdateActionKind.None,
                });
        }
        finally
        {
            if (userInitiated)
            {
                _logger.Info("Completed a user-initiated update check.");
            }
        }
    }

    /// <inheritdoc />
    public async Task RunPrimaryActionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (CurrentStatus.PrimaryActionKind)
        {
            case UpdateActionKind.DownloadAndPrepare:
                if (_availableInstalledUpdate is null)
                {
                    Publish(CurrentStatus with
                    {
                        StatusText = "No downloaded update is available yet. Run a fresh check first.",
                        CanCheckForUpdates = true,
                    });
                    return;
                }

                await DownloadInstalledUpdateAsync(_availableInstalledUpdate, cancellationToken);
                return;

            case UpdateActionKind.RestartToApply:
                var pendingRestart = _installedUpdateManager.PendingRestart;
                if (pendingRestart is null)
                {
                    Publish(CurrentStatus with
                    {
                        StatusText = "The prepared update is no longer available. Run another update check.",
                        PrimaryActionKind = UpdateActionKind.None,
                        PrimaryActionLabel = null,
                        CanRunPrimaryAction = false,
                        CanCheckForUpdates = true,
                    });
                    return;
                }

                _logger.Info($"Applying prepared update {pendingRestart.Version} and restarting.");
                _installedUpdateManager.ApplyUpdatesAndRestart(pendingRestart, ["--minimized"]);
                return;

            case UpdateActionKind.OpenPortableDownload:
                if (!string.IsNullOrWhiteSpace(CurrentStatus.DownloadUrl))
                {
                    _linkLauncher.Open(CurrentStatus.DownloadUrl);
                    return;
                }

                OpenReleasesPage();
                return;

            case UpdateActionKind.OpenReleasesPage:
                OpenReleasesPage();
                return;

            case UpdateActionKind.None:
            default:
                return;
        }
    }

    /// <inheritdoc />
    public void OpenReleasesPage()
    {
        if (!ReleaseRepositoryOptions.IsConfigured)
        {
            return;
        }

        _linkLauncher.Open(ReleaseRepositoryOptions.ReleasesUrl);
    }

    private UpdateStatusSnapshot BuildInitialStatus() =>
        UpdateStatusSnapshot.CreateDefault(_currentVersion) with
        {
            IsPortableBuild = _installedUpdateManager.IsPortable,
        };

    private async Task CheckInstalledUpdatesAsync(CancellationToken cancellationToken)
    {
        var update = await _installedUpdateManager.CheckForUpdatesAsync(cancellationToken);
        if (update is null)
        {
            _availableInstalledUpdate = null;
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "You are already running the latest installed build.",
                    UpdateActionKind.None,
                    null,
                    true,
                    false,
                    true,
                    false,
                    false));
            return;
        }

        _availableInstalledUpdate = update;
        Publish(
            new UpdateStatusSnapshot(
                _currentVersion,
                $"Version {update.TargetVersion} is available for download.",
                UpdateActionKind.DownloadAndPrepare,
                "Download update",
                true,
                true,
                true,
                false,
                false,
                update.TargetVersion.ToString()));
    }

    private async Task CheckPortableUpdatesAsync(CancellationToken cancellationToken)
    {
        var release = await _gitHubReleaseClient.GetLatestReleaseAsync(cancellationToken);
        var releaseChannel = GetReleaseChannel(RuntimeInformation.ProcessArchitecture);
        if (release is null)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "No published releases were found yet.",
                    UpdateActionKind.OpenReleasesPage,
                    "Open releases",
                    true,
                    true,
                    true,
                    false,
                    true));
            return;
        }

        _availablePortableRelease = release;
        var currentVersion = ParseVersion(_currentVersion);
        var releaseVersionText = release.TagName.TrimStart('v', 'V');
        var asset = SelectPortableAsset(release.Assets, releaseChannel);

        if (IsPortableUpToDate(currentVersion, _currentVersion, release.Version, release.TagName))
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    "You are already running the latest portable build.",
                    UpdateActionKind.OpenReleasesPage,
                    "Open releases",
                    true,
                    true,
                    true,
                    false,
                    true,
                    releaseVersionText));
            return;
        }

        if (asset is null)
        {
            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    $"Version {releaseVersionText} is available, but no {GetPortableAssetName(releaseChannel)} asset was found.",
                    UpdateActionKind.OpenReleasesPage,
                    "Open releases",
                    true,
                    true,
                    true,
                    false,
                    true,
                    releaseVersionText));
            return;
        }

        Publish(
            new UpdateStatusSnapshot(
                _currentVersion,
                $"Portable update {releaseVersionText} is available.",
                UpdateActionKind.OpenPortableDownload,
                "Open portable download",
                true,
                true,
                true,
                false,
                true,
                releaseVersionText,
                asset.DownloadUrl));
    }

    private async Task DownloadInstalledUpdateAsync(InstalledUpdateInfo update, CancellationToken cancellationToken)
    {
        Publish(
            CurrentStatus with
            {
                StatusText = $"Downloading update {update.TargetVersion}...",
                IsBusy = true,
                CanCheckForUpdates = false,
                CanRunPrimaryAction = false,
                ProgressPercent = 0,
            });

        try
        {
            var prepared = await _installedUpdateManager.DownloadUpdatesAsync(
                update,
                progress =>
                {
                    Publish(
                        CurrentStatus with
                        {
                            StatusText = $"Downloading update {update.TargetVersion}... {progress}%",
                            ProgressPercent = progress,
                        });
                },
                cancellationToken);

            Publish(
                new UpdateStatusSnapshot(
                    _currentVersion,
                    $"Version {prepared.Version} has been downloaded and is ready to apply.",
                    UpdateActionKind.RestartToApply,
                    "Restart to apply",
                    true,
                    true,
                    true,
                    false,
                    false,
                    prepared.Version.ToString()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to download update package.", ex);
            Publish(
                CurrentStatus with
                {
                    StatusText = $"Update download failed: {ex.Message}",
                    PrimaryActionKind = UpdateActionKind.DownloadAndPrepare,
                    PrimaryActionLabel = "Download update",
                    IsBusy = false,
                    CanCheckForUpdates = true,
                    CanRunPrimaryAction = true,
                    ProgressPercent = null,
                });
        }
    }

    private void Publish(UpdateStatusSnapshot status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    internal static string GetReleaseChannel(Architecture architecture) =>
        architecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

    internal static string GetPortableAssetName(string releaseChannel) =>
        $"WindowsLosslessSwitcher-{releaseChannel}-Portable.zip";

    internal static GitHubReleaseAssetInfo? SelectPortableAsset(
        IReadOnlyList<GitHubReleaseAssetInfo> assets,
        string releaseChannel)
    {
        var expectedAssetName = GetPortableAssetName(releaseChannel);
        return assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, expectedAssetName, StringComparison.OrdinalIgnoreCase));
    }

    internal static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // System.Version cannot represent a semver prerelease suffix or build metadata,
        // so "1.0.0-beta.1+sha" is parsed by its version core. Prerelease ordering is
        // handled separately where it matters (see IsPortableUpToDate).
        var normalized = value.Trim().TrimStart('v', 'V');
        var core = normalized.Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    internal static bool IsPrereleaseVersionText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('+', 2)[0].Contains('-');

    /// <summary>
    /// Decides whether the running portable build is already up to date with the newest release.
    /// System.Version comparison alone cannot order prerelease builds against the release that
    /// shares their version core (1.0.0-beta.1 vs 1.0.0), so equal cores fall back to comparing
    /// the tag text: a prerelease build is only up to date when the tags match exactly.
    /// </summary>
    internal static bool IsPortableUpToDate(
        Version? currentVersion,
        string currentVersionText,
        Version releaseVersion,
        string releaseTag)
    {
        if (currentVersion is null)
        {
            return false;
        }

        if (releaseVersion != currentVersion)
        {
            return releaseVersion < currentVersion;
        }

        return !IsPrereleaseVersionText(currentVersionText)
            || string.Equals(
                releaseTag.TrimStart('v', 'V'),
                currentVersionText,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static string? InformationalVersionText
    {
        get
        {
            var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
                ? null
                : attribute.InformationalVersion.Split('+', 2)[0];
        }
    }

    private static string ResolveCurrentVersion(IInstalledUpdateManager installedUpdateManager)
    {
        // The text form is preferred over CurrentVersion because it keeps a semver prerelease
        // suffix (e.g. 1.0.0-beta.1), which System.Version cannot represent. The informational
        // version covers portable and dev runs, where no installed package version exists.
        return installedUpdateManager.CurrentVersionText
            ?? installedUpdateManager.CurrentVersion?.ToString()
            ?? InformationalVersionText
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";
    }
}

internal interface IInstalledUpdateManager
{
    bool IsInstalled { get; }

    bool IsPortable { get; }

    Version? CurrentVersion { get; }

    /// <summary>
    /// The installed package version as reported by the package manager, including any semver
    /// prerelease suffix that <see cref="CurrentVersion"/> cannot represent. Null when the app
    /// is not running from an installed package.
    /// </summary>
    string? CurrentVersionText { get; }

    PreparedUpdateInfo? PendingRestart { get; }

    Task<InstalledUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task<PreparedUpdateInfo> DownloadUpdatesAsync(
        InstalledUpdateInfo update,
        Action<int> progressCallback,
        CancellationToken cancellationToken);

    void ApplyUpdatesAndRestart(PreparedUpdateInfo update, string[] restartArgs);
}

internal sealed class VelopackInstalledUpdateManager : IInstalledUpdateManager
{
    private readonly UpdateManager? _manager;

    public VelopackInstalledUpdateManager()
    {
        if (!ReleaseRepositoryOptions.IsConfigured)
        {
            return;
        }

        // Installed packages stay on the channel they were packaged with. A prerelease build
        // follows GitHub pre-releases (and any later stable release); a stable build only sees
        // stable releases, so publishing a beta never updates stable installs.
        var followPrereleases = AppUpdater.IsPrereleaseVersionText(AppUpdater.InformationalVersionText);
        _manager = new UpdateManager(new GithubSource(ReleaseRepositoryOptions.RepositoryUrl, null, followPrereleases));
    }

    public bool IsInstalled => _manager?.IsInstalled ?? false;

    public bool IsPortable => _manager?.IsPortable ?? false;

    public Version? CurrentVersion => AppUpdater.ParseVersion(_manager?.CurrentVersion?.ToString());

    public string? CurrentVersionText => _manager?.CurrentVersion?.ToString();

    public PreparedUpdateInfo? PendingRestart =>
        _manager?.UpdatePendingRestart is { } asset
            ? new PreparedUpdateInfo(AppUpdater.ParseVersion(asset.Version.ToString()) ?? new Version(0, 0), asset)
            : null;

    public async Task<InstalledUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_manager is null)
        {
            return null;
        }

        var update = await _manager.CheckForUpdatesAsync();
        return update is null
            ? null
            : new InstalledUpdateInfo(
                AppUpdater.ParseVersion(update.TargetFullRelease.Version.ToString()) ?? new Version(0, 0),
                update.TargetFullRelease.NotesMarkdown,
                update);
    }

    public async Task<PreparedUpdateInfo> DownloadUpdatesAsync(
        InstalledUpdateInfo update,
        Action<int> progressCallback,
        CancellationToken cancellationToken)
    {
        if (_manager is null || update.NativeHandle is not UpdateInfo nativeUpdate)
        {
            throw new InvalidOperationException("No installed updater is available for this build.");
        }

        await _manager.DownloadUpdatesAsync(nativeUpdate, progressCallback, cancellationToken);
        var pending = _manager.UpdatePendingRestart;
        if (pending is null)
        {
            throw new InvalidOperationException("Velopack did not report a prepared update after download.");
        }

        return new PreparedUpdateInfo(AppUpdater.ParseVersion(pending.Version.ToString()) ?? new Version(0, 0), pending);
    }

    public void ApplyUpdatesAndRestart(PreparedUpdateInfo update, string[] restartArgs)
    {
        if (_manager is null || update.NativeHandle is not VelopackAsset nativeAsset)
        {
            throw new InvalidOperationException("No prepared update is available to apply.");
        }

        _manager.ApplyUpdatesAndRestart(nativeAsset, restartArgs);
    }
}

internal interface IGitHubReleaseClient
{
    Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken);
}

internal sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private static readonly HttpClient SharedClient = CreateClient();

    public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        if (!ReleaseRepositoryOptions.IsConfigured)
        {
            return null;
        }

        using var response = await SharedClient.GetAsync(ReleaseRepositoryOptions.NewestReleaseApiUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var root = document.RootElement[0];

        var tagName = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var version = AppUpdater.ParseVersion(tagName);
        if (version is null)
        {
            throw new InvalidOperationException("The latest GitHub release tag could not be parsed as a version.");
        }

        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
            ? htmlUrlElement.GetString()
            : ReleaseRepositoryOptions.ReleasesUrl;

        var assets = new List<GitHubReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                var name = assetElement.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var downloadElement)
                    ? downloadElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                assets.Add(new GitHubReleaseAssetInfo(name, downloadUrl));
            }
        }

        return new GitHubReleaseInfo(version, tagName ?? version.ToString(), htmlUrl ?? ReleaseRepositoryOptions.ReleasesUrl, assets);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsLosslessSwitcher-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

internal interface ILinkLauncher
{
    void Open(string url);
}

internal sealed class LinkLauncher : ILinkLauncher
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}

internal sealed record InstalledUpdateInfo(Version TargetVersion, string? NotesMarkdown, object NativeHandle);

internal sealed record PreparedUpdateInfo(Version Version, object NativeHandle);

internal sealed record GitHubReleaseInfo(
    Version Version,
    string TagName,
    string HtmlUrl,
    IReadOnlyList<GitHubReleaseAssetInfo> Assets);

internal sealed record GitHubReleaseAssetInfo(string Name, string DownloadUrl);
