using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Resolves local files (and tracks the catalog could not identify) by reading the actual format
/// from the track's PlayCache file. Switching the device beyond the file's own sample rate gains
/// nothing (shared mode resamples regardless) and the extreme jumps (e.g. 24/48 -> 24/384) the
/// old device-max behavior made destabilized Apple Music's renderer — so when the file cannot be
/// located or read (locked by Apple Music, missing cache), this resolver returns null and the
/// chain falls through to the tier fallback (the user's quality-setting format).
/// </summary>
public sealed class LocalDeviceMaxResolver : IFormatResolver
{
    private readonly IAudioEndpointController _audioEndpointController;
    private readonly ILocalTrackFileFormatReader? _fileFormatReader;
    private readonly DiagnosticsLogger _logger;

    public LocalDeviceMaxResolver(
        IAudioEndpointController audioEndpointController,
        ILocalTrackFileFormatReader? fileFormatReader,
        DiagnosticsLogger logger)
    {
        _audioEndpointController = audioEndpointController;
        _fileFormatReader = fileFormatReader;
        _logger = logger;
    }

    public string Name => "LocalDeviceMaxResolver";

    // On the FIRST play of a track, resolution runs well before Apple Music opens or starts
    // downloading the cache file (verified live: a lookup ~0.6 s after the track change found
    // nothing; a replay matched the same track via the open-file signal). One short retry
    // converts most of those first-play misses into exact file matches.
    private static readonly TimeSpan FileLookupRetryDelay = TimeSpan.FromMilliseconds(2500);

    public async Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        try
        {
            var device = _audioEndpointController.GetDefaultRenderDevice();
            if (device is null)
            {
                _logger.Info("Local device-max resolver: no default render device; falling through.");
                return null;
            }

            // GetSupportedFormats returns ascending by sample rate then bit depth, so the last entry
            // is the highest sample rate with the highest bit depth available at that rate.
            var supported = _audioEndpointController.GetSupportedFormats(device.Id);
            if (supported.Count == 0)
            {
                _logger.Info($"Local device-max resolver: device '{device.FriendlyName}' reported no supported formats; falling through.");
                return null;
            }

            var fromFile = ResolveFromFile(track, device, supported);
            if (fromFile is not null)
            {
                return fromFile;
            }

            // Give Apple Music a moment to open or start downloading the file, then try once more.
            await Task.Delay(FileLookupRetryDelay, cancellationToken);
            fromFile = ResolveFromFile(track, device, supported);
            if (fromFile is not null)
            {
                return fromFile;
            }

            // File format unknown: do NOT gamble on a device-max jump. Returning null lets the
            // tier fallback apply the user's quality-setting format instead.
            _logger.Info($"Local device-max resolver: file format unknown for '{track.Title}'; falling through to the tier fallback.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Local device-max resolver failed for {track.UniqueKey}.", ex);
            return null;
        }
    }

    private ResolvedAudioFormat? ResolveFromFile(
        TrackSnapshot track,
        AudioDeviceInfo device,
        IReadOnlyList<AudioFormatCandidate> supported)
    {
        if (_fileFormatReader is null)
        {
            return null;
        }

        LocalTrackFileFormat? fileFormat;
        try
        {
            fileFormat = _fileFormatReader.TryReadFormat(track);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Local device-max resolver: file format lookup failed for '{track.Title}': {ex.Message}");
            return null;
        }

        if (fileFormat is null)
        {
            _logger.Info($"Local device-max resolver: no readable cache file for '{track.Title}'.");
            return null;
        }

        var atFileRate = supported.Where(candidate => candidate.SampleRateHz == fileFormat.SampleRateHz).ToList();
        if (atFileRate.Count == 0)
        {
            _logger.Info($"Local device-max resolver: device '{device.FriendlyName}' does not support file rate {fileFormat.SampleRateHz}.");
            return null;
        }

        // Match the file's bit depth when the device supports it at that rate; otherwise take the
        // highest depth available at the rate (lossy files report no depth).
        var chosen = (fileFormat.BitDepth > 0
            ? atFileRate.FirstOrDefault(candidate => candidate.BitDepth == fileFormat.BitDepth)
            : null) ?? atFileRate[^1];

        _logger.Info($"Local device-max resolver: local track '{track.Title}' matched file with format {fileFormat.BitDepth}/{fileFormat.SampleRateHz} -> applying {chosen.BitDepth}/{chosen.SampleRateHz} on '{device.FriendlyName}'.");
        return new ResolvedAudioFormat(
            chosen.SampleRateHz,
            chosen.BitDepth,
            ResolutionConfidence.Exact,
            AudioFormatSource.LocalFile,
            $"Local file: {chosen.BitDepth}/{chosen.SampleRateHz / 1000.0:0.###} from file metadata");
    }
}
