using NAudio.CoreAudioApi;
using NAudio.Wave;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class CoreAudioEndpointController : IAudioEndpointController
{
    // Standard PCM sample rates probed via exclusive-mode to build the supported-formats cache.
    // Covers CD (44.1/48 kHz), high-res (88.2/96/176.4/192 kHz), DXD (352.8/384 kHz), and the
    // 705.6/768 kHz ceiling of current XMOS/Thesycon and C-Media DACs (e.g. FiiO KA3/KA5/K17/
    // K5 Pro). Rates absent from this list are never queried, so a DAC's top rates would
    // otherwise go undetected.
    private static readonly int[] CandidateRates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];

    // Bit depths probed for each sample rate. 32-bit is included because some DACs advertise
    // 32-bit float shared-mode support even when 24-bit is the effective hardware depth.
    private static readonly int[] CandidateDepths = [16, 24, 32];

    // Number of polling attempts after calling SetDeviceFormat before reporting failure.
    // PolicyConfig changes propagate asynchronously through the audio engine; 5 attempts at
    // 200 ms each gives up to 1 second of confirmation time before giving up.
    private const int VerificationAttempts = 5;

    // Delay between each format-verification poll. Together with VerificationAttempts this
    // defines the total window: 5 × 200 ms = 1 000 ms maximum before declaring a failure.
    private static readonly TimeSpan VerificationRetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly object _supportedFormatsSync = new();
    private readonly Dictionary<string, IReadOnlyList<EndpointFormatDescriptor>> _supportedFormatsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastApplyDiagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastProbeDiagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EndpointFormatKind> _lastSuccessfulKinds = new(StringComparer.Ordinal);

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultDevice.ID))
                .OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public AudioDeviceInfo? GetDefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioDeviceInfo(device.ID, device.FriendlyName, true);
        }
        catch
        {
            return null;
        }
    }

    public AudioFormatCandidate? GetCurrentDeviceFormat(string deviceId)
    {
        try
        {
            return ReadCurrentDeviceFormatSnapshot(deviceId).EffectiveFormat;
        }
        catch
        {
            return null;
        }
    }

    public AudioFormatCandidate? GetCurrentMixFormat(string deviceId)
    {
        try
        {
            using var device = GetDevice(deviceId);
            var mixFormat = device.AudioClient.MixFormat;
            return new AudioFormatCandidate(mixFormat.SampleRate, EndpointFormatSupport.GetValidBitsPerSample(mixFormat), mixFormat.Channels);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string deviceId, bool forceRefresh = false)
    {
        try
        {
            var descriptors = GetCachedOrProbedDescriptors(deviceId, forceRefresh);
            return descriptors
                .Select(descriptor => descriptor.Format)
                .Distinct()
                .OrderBy(format => format.SampleRateHz)
                .ThenBy(format => format.BitDepth)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public string? DescribeSupportedFormat(string deviceId, AudioFormatCandidate format)
    {
        try
        {
            var descriptors = GetCachedOrProbedDescriptors(deviceId);
            var matches = EndpointFormatSupport.GetMatchingDescriptors(descriptors, format);
            return matches.Count == 0
                ? null
                : string.Join(", ", matches.Select(match => match.Description).Distinct());
        }
        catch
        {
            return null;
        }
    }

    public string? GetLastApplyDiagnostics(string deviceId)
    {
        lock (_supportedFormatsSync)
        {
            return _lastApplyDiagnostics.TryGetValue(deviceId, out var diagnostics) ? diagnostics : null;
        }
    }

    public string? GetLastProbeDiagnostics(string deviceId)
    {
        lock (_supportedFormatsSync)
        {
            return _lastProbeDiagnostics.TryGetValue(deviceId, out var diagnostics) ? diagnostics : null;
        }
    }

    public float? GetMasterPeakValue(string deviceId)
    {
        try
        {
            using var device = GetDevice(deviceId);
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            return null;
        }
    }

    public bool? IsProcessSessionActive(string deviceId, string processName)
    {
        try
        {
            using var device = GetDevice(deviceId);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            if (sessions is null)
            {
                return null;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                {
                    continue;
                }

                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                    if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Session owner exited between enumeration and lookup — not active for our purposes.
                }
            }

            return false;
        }
        catch
        {
            return null;
        }
    }

    public float? GetProcessSessionPeak(string deviceId, string processName)
    {
        // The per-session meter isolates the process's own audio: the endpoint MASTER meter mixes
        // every app on the device, so with anything else playing it reads audio even when this
        // process renders nothing (verified live — it falsely confirmed dead playback as
        // restored). Session meters also read upstream of the endpoint mute, so the value stays
        // meaningful while the device is muted during a switch.
        try
        {
            using var device = GetDevice(deviceId);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            if (sessions is null)
            {
                return null;
            }

            var maxPeak = 0f;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                    if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var peak = session.AudioMeterInformation?.MasterPeakValue ?? 0f;
                    if (peak > maxPeak)
                    {
                        maxPeak = peak;
                    }
                }
                catch
                {
                    // Session owner exited between enumeration and lookup — contributes no audio.
                }
            }

            return maxPeak;
        }
        catch
        {
            return null;
        }
    }

    public bool? GetMasterMute(string deviceId)
    {
        try
        {
            using var device = GetDevice(deviceId);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return null;
        }
    }

    public bool TrySetMasterMute(string deviceId, bool muted)
    {
        try
        {
            using var device = GetDevice(deviceId);
            device.AudioEndpointVolume.Mute = muted;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryApplyFormat(
        string deviceId,
        AudioFormatCandidate format,
        out AudioFormatCandidate? verifiedDeviceFormat,
        out string failureReason)
    {
        verifiedDeviceFormat = null;
        var descriptor = default(EndpointFormatDescriptor);
        var beforeFormat = default(AudioFormatCandidate);

        try
        {
            descriptor = GetDescriptor(deviceId, format, out var descriptorSelectionDiagnostics);
            if (descriptor is null)
            {
                CacheApplyDiagnostics(deviceId, $"Unsupported target {format.DisplayName}: not proven settable by exclusive-mode probing.");
                failureReason = $"Target format {format.DisplayName} is not proven settable by exclusive-mode probing.";
                return false;
            }

            beforeFormat = GetCurrentDeviceFormat(deviceId);
            PolicyConfigInterop.SetDeviceFormat(deviceId, descriptor.NativeFormat);
            // Verify against the descriptor's own candidate: the legacy container-bits fallback
            // can select a descriptor whose valid-bits depth differs from the requested format,
            // and the device reports the descriptor's depth afterwards.
            var verification = EndpointFormatSupport.VerifyTargetFormat(
                descriptor.Format,
                VerificationAttempts,
                VerificationRetryDelay,
                () => ReadCurrentDeviceFormatSnapshot(deviceId),
                Thread.Sleep);

            verifiedDeviceFormat = verification.VerifiedFormat;
            var diagnostics =
                $"before={(beforeFormat?.DisplayName ?? "unknown")}, " +
                $"propertyAfter={(verification.LastPropertyStoreFormat?.DisplayName ?? "unknown")}, " +
                $"policyAfter={(verification.LastPolicyConfigFormat?.DisplayName ?? "unknown")}, " +
                $"verifiedVia={verification.VerificationSource}, attempts={verification.Attempts}, postSwitchReprobe=skipped, {descriptorSelectionDiagnostics}.";
            CacheApplyDiagnostics(deviceId, diagnostics);

            if (!verification.Success)
            {
                failureReason =
                    $"SetDeviceFormat using {descriptor.Kind} did not update the shared device format. " +
                    diagnostics;
                return false;
            }

            CacheLastSuccessfulKind(deviceId, descriptor.Kind);
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            verifiedDeviceFormat = GetCurrentDeviceFormat(deviceId);
            var beforeText = beforeFormat?.DisplayName ?? "unknown";
            var afterText = verifiedDeviceFormat?.DisplayName ?? "unknown";
            var kindText = descriptor?.Kind.ToString() ?? "unknown format payload";
            CacheApplyDiagnostics(deviceId, $"before={beforeText}, propertyAfter={afterText}, policyAfter={afterText}, verifiedVia=exception, attempts=1, postSwitchReprobe=skipped.");
            failureReason = $"{kindText}: {ex.Message}; before={beforeText}, after={afterText}.";
            return false;
        }
    }

    private static MMDevice GetDevice(string deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(deviceId);
    }

    private EndpointFormatDescriptor? GetDescriptor(string deviceId, AudioFormatCandidate format, out string diagnostics)
    {
        var descriptors = GetCachedOrProbedDescriptors(deviceId);
        var currentPropertyStoreDescriptor = TryReadCurrentPropertyStoreDescriptor(deviceId);
        var currentPropertyStoreKind = currentPropertyStoreDescriptor?.Kind;
        var lastSuccessfulKind = GetLastSuccessfulKind(deviceId);
        var matches = EndpointFormatSupport.GetMatchingDescriptors(descriptors, format);
        var selected = EndpointFormatSupport.SelectBestDescriptor(descriptors, format, currentPropertyStoreKind, lastSuccessfulKind);
        string reason;
        if (selected is not null)
        {
            reason = currentPropertyStoreKind is not null && selected.Kind == currentPropertyStoreKind.Value
                ? "preferred currentPropertyStoreFamily"
                : lastSuccessfulKind is not null && selected.Kind == lastSuccessfulKind.Value
                    ? "preferred lastSuccessfulFamily"
                    : "used remaining variant";
        }
        else
        {
            // Pre-fix OriginalTarget snapshots stored container bits (e.g. 32 on a 24-in-32
            // device); candidates now carry valid bits, so match those targets by container.
            selected = EndpointFormatSupport.SelectContainerBitsFallback(descriptors, format);
            reason = selected is null ? "no variant matched" : "legacyContainerBitsFallback";
        }

        var variants = matches.Count == 0
            ? "none"
            : string.Join(" | ", matches.Select(match => match.Description));

        diagnostics =
            $"currentPropertyStoreFamily={(currentPropertyStoreKind?.ToString() ?? "unknown")}, " +
            $"lastSuccessfulFamily={(lastSuccessfulKind?.ToString() ?? "unknown")}, " +
            $"candidateVariants={variants}, chosen={(selected?.Description ?? "none")}, selection={reason}";
        return selected;
    }

    private IReadOnlyList<EndpointFormatDescriptor> GetCachedOrProbedDescriptors(string deviceId, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            lock (_supportedFormatsSync)
            {
                if (_supportedFormatsCache.TryGetValue(deviceId, out var descriptors))
                {
                    return descriptors;
                }
            }
        }

        var probed = ProbeSupportedFormats(deviceId);
        CacheSupportedFormats(deviceId, probed);
        return probed;
    }

    private IReadOnlyList<EndpointFormatDescriptor> ProbeSupportedFormats(string deviceId)
    {
        using var device = GetDevice(deviceId);
        var currentDeviceFormat = EndpointFormatSupport.TryReadPropertyStoreDeviceFormat(device);
        var channels = currentDeviceFormat?.Format.Channels ?? device.AudioClient.MixFormat.Channels;

        var probeCount = 0;
        var acceptedCount = 0;
        // Tallied by HRESULT so an all-rejected probe on a real device reveals *why* (e.g.
        // AUDCLNT_E_UNSUPPORTED_FORMAT vs an exclusive-mode/in-use error) — the only way to
        // confirm a device-specific detection failure without the hardware in hand.
        var errorCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        bool RecordingIsSupported(WaveFormat waveFormat)
        {
            probeCount++;
            try
            {
                var supported = device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, waveFormat);
                if (supported)
                {
                    acceptedCount++;
                }

                return supported;
            }
            catch (Exception ex)
            {
                var key = $"0x{ex.HResult:X8}";
                errorCounts[key] = errorCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                return false;
            }
        }

        var descriptors = EndpointFormatSupport.BuildSupportedFormats(
            currentDeviceFormat?.NativeFormat,
            channels,
            CandidateRates,
            CandidateDepths,
            RecordingIsSupported);

        CacheProbeDiagnostics(deviceId, BuildProbeDiagnostics(currentDeviceFormat, channels, probeCount, acceptedCount, errorCounts, descriptors));
        return descriptors;
    }

    private static string BuildProbeDiagnostics(
        EndpointFormatDescriptor? currentDeviceFormat,
        int channels,
        int probeCount,
        int acceptedCount,
        IReadOnlyDictionary<string, int> errorCounts,
        IReadOnlyList<EndpointFormatDescriptor> descriptors)
    {
        var acceptedFormats = descriptors
            .Where(descriptor => descriptor.Origin == EndpointFormatOrigin.ExclusiveProbe)
            .Select(descriptor => descriptor.Format)
            .Distinct()
            .OrderBy(format => format.SampleRateHz)
            .ThenBy(format => format.BitDepth)
            .Select(format => format.DisplayName);

        var errors = errorCounts.Count == 0
            ? "none"
            : string.Join(", ", errorCounts.Select(pair => $"{pair.Key}×{pair.Value}"));

        return
            $"channels={channels}, currentFormat={(currentDeviceFormat?.Format.DisplayName ?? "unknown")}, " +
            $"probed={probeCount}, acceptedProbes={acceptedCount}, " +
            $"acceptedFormats=[{string.Join(" ", acceptedFormats)}], probeErrors={{{errors}}}";
    }

    private void CacheSupportedFormats(string deviceId, IReadOnlyList<EndpointFormatDescriptor> descriptors)
    {
        lock (_supportedFormatsSync)
        {
            _supportedFormatsCache[deviceId] = descriptors;
        }
    }

    private void CacheProbeDiagnostics(string deviceId, string diagnostics)
    {
        lock (_supportedFormatsSync)
        {
            _lastProbeDiagnostics[deviceId] = diagnostics;
        }
    }

    private DeviceFormatSnapshot ReadCurrentDeviceFormatSnapshot(string deviceId)
    {
        AudioFormatCandidate? propertyStoreFormat = null;
        AudioFormatCandidate? policyConfigFormat = null;

        try
        {
            using var device = GetDevice(deviceId);
            propertyStoreFormat = EndpointFormatSupport.TryReadPropertyStoreDeviceFormat(device)?.Format;
        }
        catch
        {
        }

        try
        {
            var policyFormat = PolicyConfigInterop.GetDeviceFormat(deviceId);
            if (policyFormat is not null)
            {
                policyConfigFormat = new AudioFormatCandidate(
                    policyFormat.SampleRate,
                    EndpointFormatSupport.GetValidBitsPerSample(policyFormat),
                    policyFormat.Channels);
            }
        }
        catch
        {
        }

        return new DeviceFormatSnapshot(propertyStoreFormat, policyConfigFormat);
    }

    private void CacheApplyDiagnostics(string deviceId, string diagnostics)
    {
        lock (_supportedFormatsSync)
        {
            _lastApplyDiagnostics[deviceId] = diagnostics;
        }
    }

    private EndpointFormatDescriptor? TryReadCurrentPropertyStoreDescriptor(string deviceId)
    {
        try
        {
            using var device = GetDevice(deviceId);
            return EndpointFormatSupport.TryReadPropertyStoreDeviceFormat(device);
        }
        catch
        {
            return null;
        }
    }

    private EndpointFormatKind? GetLastSuccessfulKind(string deviceId)
    {
        lock (_supportedFormatsSync)
        {
            return _lastSuccessfulKinds.TryGetValue(deviceId, out var kind) ? kind : null;
        }
    }

    private void CacheLastSuccessfulKind(string deviceId, EndpointFormatKind kind)
    {
        lock (_supportedFormatsSync)
        {
            _lastSuccessfulKinds[deviceId] = kind;
        }
    }
}
