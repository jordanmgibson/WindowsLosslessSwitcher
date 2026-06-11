using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

[Collection("UpdateEnvironment")]
public sealed class ReleaseRepositoryOptionsTests
{
    [Fact]
    public void Defaults_PointAtWindowsLosslessSwitcherRepository()
    {
        var originalOwner = Environment.GetEnvironmentVariable("WLS_GITHUB_OWNER");
        var originalRepository = Environment.GetEnvironmentVariable("WLS_GITHUB_REPOSITORY");

        try
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", null);
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", null);

            Assert.Equal("jordanmgibson", ReleaseRepositoryOptions.Owner);
            Assert.Equal("WindowsLosslessSwitcher", ReleaseRepositoryOptions.Repository);
            Assert.True(ReleaseRepositoryOptions.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", originalOwner);
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", originalRepository);
        }
    }

    [Fact]
    public void PlaceholderOverrides_DisableUpdaterConfiguration()
    {
        var originalOwner = Environment.GetEnvironmentVariable("WLS_GITHUB_OWNER");
        var originalRepository = Environment.GetEnvironmentVariable("WLS_GITHUB_REPOSITORY");

        try
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", ReleaseRepositoryOptions.PlaceholderOwner);
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", ReleaseRepositoryOptions.PlaceholderRepositoryName);

            Assert.False(ReleaseRepositoryOptions.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", originalOwner);
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", originalRepository);
        }
    }

    [Fact]
    public void ExplicitOverrides_ReplaceDefaultRepositoryTarget()
    {
        var originalOwner = Environment.GetEnvironmentVariable("WLS_GITHUB_OWNER");
        var originalRepository = Environment.GetEnvironmentVariable("WLS_GITHUB_REPOSITORY");

        try
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", "octocat");
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", "Hello-World");

            Assert.Equal("octocat", ReleaseRepositoryOptions.Owner);
            Assert.Equal("Hello-World", ReleaseRepositoryOptions.Repository);
            Assert.True(ReleaseRepositoryOptions.IsConfigured);
            Assert.Equal("https://github.com/octocat/Hello-World", ReleaseRepositoryOptions.RepositoryUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WLS_GITHUB_OWNER", originalOwner);
            Environment.SetEnvironmentVariable("WLS_GITHUB_REPOSITORY", originalRepository);
        }
    }
}
