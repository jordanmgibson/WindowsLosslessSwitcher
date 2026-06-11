using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class ResolverChainTests
{
    [Fact]
    public void Constructor_ThrowsWhenResolversAreNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new ResolverChain(null!));

        Assert.Equal("resolvers", exception.ParamName);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsFirstResolvedFormatWithoutCallingRemainingResolvers()
    {
        var expected = new ResolvedAudioFormat(
            96000,
            24,
            ResolutionConfidence.Exact,
            AudioFormatSource.CatalogManifest,
            "expected");
        var first = new TestResolver("first", null);
        var second = new TestResolver("second", expected);
        var third = new TestResolver("third", new ResolvedAudioFormat(
            44100,
            16,
            ResolutionConfidence.Tier,
            AudioFormatSource.TierFallback,
            "unused"));
        var chain = new ResolverChain([first, second, third]);
        var track = new TrackSnapshot("AppleMusic", null, "Track", "Artist", "Album", "test", DateTimeOffset.UtcNow);

        var result = await chain.ResolveAsync(track, CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        Assert.Equal(0, third.CallCount);
    }

    private sealed class TestResolver(string name, ResolvedAudioFormat? result) : IFormatResolver
    {
        public string Name => name;

        public int CallCount { get; private set; }

        public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
