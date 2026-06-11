using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

internal enum EndpointFormatKind
{
    WaveFormatEx,
    WaveFormatExtensible,
}

internal enum EndpointFormatOrigin
{
    PropertyStore,
    ExclusiveProbe,
}

internal sealed record EndpointFormatDescriptor(
    AudioFormatCandidate Format,
    WaveFormat NativeFormat,
    EndpointFormatKind Kind,
    EndpointFormatOrigin Origin)
{
    public string Description => $"{Kind} via {Origin}";
}

internal sealed record DeviceFormatSnapshot(
    AudioFormatCandidate? PropertyStoreFormat,
    AudioFormatCandidate? PolicyConfigFormat)
{
    public AudioFormatCandidate? EffectiveFormat => PropertyStoreFormat ?? PolicyConfigFormat;
}

internal sealed record DeviceFormatVerificationResult(
    bool Success,
    AudioFormatCandidate? VerifiedFormat,
    AudioFormatCandidate? LastPropertyStoreFormat,
    AudioFormatCandidate? LastPolicyConfigFormat,
    string VerificationSource,
    int Attempts);

internal static class EndpointFormatSupport
{
    private static readonly FieldInfo? PropertyValueField = typeof(PropertyStoreProperty).GetField(
        "propertyValue",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? GetBlobMethod = typeof(PropVariant).GetMethod(
        "GetBlob",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static EndpointFormatDescriptor? TryReadPropertyStoreDeviceFormat(MMDevice device)
    {
        var blob = TryReadDeviceFormatBlob(device);
        return blob is null ? null : TryParseDeviceFormat(blob, EndpointFormatOrigin.PropertyStore);
    }

    public static byte[]? TryReadDeviceFormatBlob(MMDevice device)
    {
        try
        {
            if (!device.Properties.Contains(PropertyKeys.PKEY_AudioEngine_DeviceFormat))
            {
                return null;
            }

            var property = device.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat];
            if (property.Value is byte[] blob)
            {
                return blob;
            }

            if (PropertyValueField?.GetValue(property) is PropVariant propVariant)
            {
                return GetBlobMethod?.Invoke(propVariant, null) as byte[];
            }
        }
        catch
        {
        }

        return null;
    }

    public static EndpointFormatDescriptor? TryParseDeviceFormat(byte[] blob, EndpointFormatOrigin origin = EndpointFormatOrigin.PropertyStore)
    {
        if (blob.Length < Marshal.SizeOf<WAVEFORMATEX>())
        {
            return null;
        }

        var pointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pointer, blob.Length);
            var waveFormat = WaveFormat.MarshalFromPtr(pointer);
            return CreateDescriptor(waveFormat, origin);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static byte[] SerializeWaveFormat(WaveFormat waveFormat)
    {
        var pointer = WaveFormat.MarshalToPtr(waveFormat);
        try
        {
            var length = GetWaveFormatSize(waveFormat);
            var blob = new byte[length];
            Marshal.Copy(pointer, blob, 0, length);
            return blob;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static IReadOnlyList<EndpointFormatDescriptor> BuildSupportedFormats(
        WaveFormat? currentDeviceFormat,
        int channels,
        IReadOnlyList<int> candidateRates,
        IReadOnlyList<int> candidateDepths,
        Func<WaveFormat, bool> isSupported)
    {
        var descriptors = new List<EndpointFormatDescriptor>();

        if (currentDeviceFormat is not null)
        {
            AddOrReplaceDescriptor(descriptors, CreateDescriptor(currentDeviceFormat, EndpointFormatOrigin.PropertyStore));
        }

        foreach (var sampleRate in candidateRates)
        {
            foreach (var bitDepth in candidateDepths)
            {
                foreach (var waveFormat in CreateProbeFormats(sampleRate, bitDepth, channels))
                {
                    if (!isSupported(waveFormat))
                    {
                        continue;
                    }

                    AddOrReplaceDescriptor(descriptors, CreateDescriptor(waveFormat, EndpointFormatOrigin.ExclusiveProbe));
                }
            }
        }

        return descriptors
            .OrderBy(descriptor => descriptor.Format.SampleRateHz)
            .ThenBy(descriptor => descriptor.Format.BitDepth)
            .ThenBy(descriptor => descriptor.Kind)
            .ToList();
    }

    public static IEnumerable<WaveFormat> CreateProbeFormats(int sampleRate, int bitDepth, int channels)
    {
        if (channels <= 2)
        {
            yield return new WaveFormat(sampleRate, bitDepth, channels);
            yield return new WaveFormatExtensible(sampleRate, bitDepth, channels);
            yield break;
        }

        yield return new WaveFormatExtensible(sampleRate, bitDepth, channels);
    }

    public static EndpointFormatDescriptor CreateDescriptor(WaveFormat waveFormat, EndpointFormatOrigin origin)
    {
        var format = new AudioFormatCandidate(waveFormat.SampleRate, waveFormat.BitsPerSample, waveFormat.Channels);
        var kind = waveFormat is WaveFormatExtensible ? EndpointFormatKind.WaveFormatExtensible : EndpointFormatKind.WaveFormatEx;
        return new EndpointFormatDescriptor(format, waveFormat, kind, origin);
    }

    public static DeviceFormatVerificationResult VerifyTargetFormat(
        AudioFormatCandidate targetFormat,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<DeviceFormatSnapshot> snapshotReader,
        Action<TimeSpan> wait)
    {
        DeviceFormatSnapshot snapshot = new(null, null);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            snapshot = snapshotReader();

            if (snapshot.PropertyStoreFormat == targetFormat)
            {
                return new DeviceFormatVerificationResult(
                    true,
                    snapshot.PropertyStoreFormat,
                    snapshot.PropertyStoreFormat,
                    snapshot.PolicyConfigFormat,
                    "property store",
                    attempt);
            }

            if (snapshot.PolicyConfigFormat == targetFormat)
            {
                return new DeviceFormatVerificationResult(
                    true,
                    snapshot.PolicyConfigFormat,
                    snapshot.PropertyStoreFormat,
                    snapshot.PolicyConfigFormat,
                    "PolicyConfig",
                    attempt);
            }

            if (attempt < maxAttempts)
            {
                wait(retryDelay);
            }
        }

        return new DeviceFormatVerificationResult(
            false,
            snapshot.EffectiveFormat,
            snapshot.PropertyStoreFormat,
            snapshot.PolicyConfigFormat,
            "unverified",
            maxAttempts);
    }

    public static IReadOnlyList<EndpointFormatDescriptor> GetMatchingDescriptors(
        IReadOnlyList<EndpointFormatDescriptor> descriptors,
        AudioFormatCandidate targetFormat) =>
        descriptors
            .Where(descriptor => descriptor.Format == targetFormat)
            .OrderBy(descriptor => descriptor.Kind)
            .ThenBy(GetPreference)
            .ToList();

    public static EndpointFormatDescriptor? SelectBestDescriptor(
        IReadOnlyList<EndpointFormatDescriptor> descriptors,
        AudioFormatCandidate targetFormat,
        EndpointFormatKind? currentPropertyStoreKind,
        EndpointFormatKind? lastSuccessfulKind)
    {
        var matches = GetMatchingDescriptors(descriptors, targetFormat);
        if (matches.Count == 0)
        {
            return null;
        }

        var currentMatch = currentPropertyStoreKind is null
            ? null
            : matches.FirstOrDefault(descriptor => descriptor.Kind == currentPropertyStoreKind.Value);
        if (currentMatch is not null)
        {
            return currentMatch;
        }

        var lastSuccessMatch = lastSuccessfulKind is null
            ? null
            : matches.FirstOrDefault(descriptor => descriptor.Kind == lastSuccessfulKind.Value);
        if (lastSuccessMatch is not null)
        {
            return lastSuccessMatch;
        }

        return matches[0];
    }

    private static void AddOrReplaceDescriptor(
        IList<EndpointFormatDescriptor> descriptors,
        EndpointFormatDescriptor candidate)
    {
        var existing = descriptors.FirstOrDefault(descriptor =>
            descriptor.Format == candidate.Format &&
            descriptor.Kind == candidate.Kind);

        if (existing is null)
        {
            descriptors.Add(candidate);
            return;
        }

        if (GetPreference(candidate) < GetPreference(existing))
        {
            descriptors.Remove(existing);
            descriptors.Add(candidate);
        }
    }

    private static int GetPreference(EndpointFormatDescriptor descriptor)
    {
        if (descriptor.Origin == EndpointFormatOrigin.PropertyStore)
        {
            return 0;
        }

        return descriptor.Kind == EndpointFormatKind.WaveFormatEx ? 1 : 2;
    }

    private static int GetWaveFormatSize(WaveFormat waveFormat) => Marshal.SizeOf<WAVEFORMATEX>() + waveFormat.ExtraSize;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}
