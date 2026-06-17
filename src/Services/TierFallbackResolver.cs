using System.IO;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class TierFallbackResolver : IFormatResolver
{
    private readonly AppleMusicPaths _paths;
    private readonly BinaryPlistReader _plistReader;
    private readonly DiagnosticsLogger _logger;

    public TierFallbackResolver(
        AppleMusicPaths paths,
        BinaryPlistReader plistReader,
        DiagnosticsLogger logger)
    {
        _paths = paths;
        _plistReader = plistReader;
        _logger = logger;
    }

    public string Name => "TierFallbackResolver";

    public Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_paths.LibraryAgentPreferencesPath))
            {
                _logger.Warn($"Tier fallback preferences file was not found at {_paths.LibraryAgentPreferencesPath}.");
                return Task.FromResult<ResolvedAudioFormat?>(AudioQualityPreferenceMapper.CreateUndetermined(
                    "Rate undetermined (Apple Music preferences unavailable) — defaulting to 24/44.1"));
            }

            var preferences = _plistReader.ReadRootDictionary(_paths.LibraryAgentPreferencesPath);
            var format = AudioQualityPreferenceMapper.Map(preferences);
            _logger.Info($"Tier fallback selected {format.Description} for track {track.UniqueKey}.");
            return Task.FromResult<ResolvedAudioFormat?>(format);
        }
        catch (Exception ex)
        {
            _logger.Error("Tier fallback resolver failed.", ex);
            return Task.FromResult<ResolvedAudioFormat?>(AudioQualityPreferenceMapper.CreateUndetermined(
                "Rate undetermined (Apple Music preferences could not be parsed) — defaulting to 24/44.1"));
        }
    }
}
