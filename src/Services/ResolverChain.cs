using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class ResolverChain
{
    private readonly IReadOnlyList<IFormatResolver> _resolvers;

    public ResolverChain(IEnumerable<IFormatResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = resolvers.ToList();
    }

    public async Task<ResolvedAudioFormat?> ResolveAsync(
        TrackSnapshot track,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in _resolvers)
        {
            var result = await resolver.ResolveAsync(track, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
