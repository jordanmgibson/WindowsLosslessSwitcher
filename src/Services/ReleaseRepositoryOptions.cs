using System.Reflection;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Defines the GitHub repository used for release publishing and update checks.
/// </summary>
public static class ReleaseRepositoryOptions
{
    internal const string PlaceholderOwner = "your-github-owner";
    internal const string PlaceholderRepositoryName = "your-github-repository";
    private static readonly string DefaultOwner = ReadAssemblyMetadata("WlsGitHubOwner") ?? PlaceholderOwner;
    private static readonly string DefaultRepositoryName = ReadAssemblyMetadata("WlsGitHubRepository") ?? PlaceholderRepositoryName;

    /// <summary>
    /// Gets the GitHub owner name. Set <c>WLS_GITHUB_OWNER</c> to override this value locally.
    /// </summary>
    public static string Owner => ReadOverride("WLS_GITHUB_OWNER") ?? DefaultOwner;

    /// <summary>
    /// Gets the GitHub repository name. Set <c>WLS_GITHUB_REPOSITORY</c> to override the default locally.
    /// </summary>
    public static string Repository => ReadOverride("WLS_GITHUB_REPOSITORY") ?? DefaultRepositoryName;

    /// <summary>
    /// Returns true when the updater has a real GitHub repository target.
    /// </summary>
    public static bool IsConfigured => IsConfiguredRepositoryTarget(Owner, Repository);

    /// <summary>
    /// Gets a concise description of how to configure the repository target.
    /// </summary>
    public static string ConfigurationInstructions =>
        "Set WlsGitHubOwner/WlsGitHubRepository or override them with WLS_GITHUB_OWNER/WLS_GITHUB_REPOSITORY.";

    /// <summary>
    /// Gets the public repository URL.
    /// </summary>
    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    /// <summary>
    /// Gets the repository releases page URL.
    /// </summary>
    public static string ReleasesUrl => $"{RepositoryUrl}/releases";

    /// <summary>
    /// Gets the GitHub Releases API URL for the newest published release. The list endpoint is
    /// used instead of <c>/releases/latest</c> because the latter never returns pre-releases.
    /// </summary>
    public static string NewestReleaseApiUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=1";

    private static string? ReadOverride(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static bool IsConfiguredRepositoryTarget(string owner, string repository) =>
        IsConfiguredValue(owner, PlaceholderOwner) &&
        IsConfiguredValue(repository, PlaceholderRepositoryName);

    private static bool IsConfiguredValue(string value, string placeholder) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, placeholder, StringComparison.OrdinalIgnoreCase);

    private static string? ReadAssemblyMetadata(string key) =>
        typeof(ReleaseRepositoryOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
}
