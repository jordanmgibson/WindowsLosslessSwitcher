using System.Buffers.Binary;
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

    // PCM extensible with valid bits equal to the container size (e.g. 24-in-24).
    WaveFormatExtensible,

    // PCM extensible with fewer valid bits than the container (e.g. 24-in-32). USB Audio
    // Class 2 devices typically expose 24-bit audio only in this shape.
    WaveFormatExtensiblePadded,

    WaveFormatExtensibleIeeeFloat,
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
    private static readonly FieldInfo? ValidBitsPerSampleField = typeof(WaveFormatExtensible).GetField(
        "wValidBitsPerSample",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Guid KsDataFormatSubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsDataFormatSubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

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

        var waveFormat = TryMarshalWaveFormat(blob);
        return waveFormat is null ? null : CreateDescriptor(waveFormat, origin);
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
        }

        yield return new WaveFormatExtensible(sampleRate, bitDepth, channels);

        // USB Audio Class 2 devices commonly accept 24-bit audio only as 24 valid bits in a
        // 32-bit container, and 32-bit only as integer PCM. NAudio's extensible constructor
        // can express neither shape: it ties valid bits to the container size and forces
        // IEEE float at 32 bits.
        //
        // For each manually-built extensible shape we probe both the speaker-position channel
        // mask and dwChannelMask=0 ("positions unspecified"): some USB-audio firmware/drivers
        // (observed across FiiO's Thesycon and C-Media stacks) reject the positioned mask for a
        // plain stereo stream but accept the unspecified one. Duplicates collapse downstream —
        // AddOrReplaceDescriptor keys on (Format, Kind) — so both masks are probed without
        // producing duplicate descriptors.
        if (bitDepth == 24)
        {
            // Build the 24-in-24 (packed-container) shape from the trusted blob writer rather
            // than relying on NAudio's constructor, so the channel mask is always well-formed.
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 24, containerBits: 24, channels);
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 24, containerBits: 24, channels, channelMask: 0);
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 24, containerBits: 32, channels);
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 24, containerBits: 32, channels, channelMask: 0);
        }
        else if (bitDepth == 32)
        {
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 32, containerBits: 32, channels);
            yield return CreatePcmExtensibleFormat(sampleRate, validBits: 32, containerBits: 32, channels, channelMask: 0);
        }
    }

    public static WaveFormat CreatePcmExtensibleFormat(int sampleRate, int validBits, int containerBits, int channels, int? channelMask = null)
    {
        var blockAlign = channels * containerBits / 8;
        var mask = channelMask ?? (1 << channels) - 1;
        var blob = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0), 0xFFFE); // WAVE_FORMAT_EXTENSIBLE
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(14), (ushort)containerBits);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(16), 22); // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(18), (ushort)validBits);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(20), (uint)mask); // dwChannelMask
        KsDataFormatSubTypePcm.TryWriteBytes(blob.AsSpan(24));

        return TryMarshalWaveFormat(blob)
            ?? throw new InvalidOperationException($"Failed to marshal {validBits}-in-{containerBits} extensible format.");
    }

    public static int GetValidBitsPerSample(WaveFormat waveFormat)
    {
        if (waveFormat is WaveFormatExtensible extensible &&
            ValidBitsPerSampleField?.GetValue(extensible) is short validBits &&
            validBits > 0)
        {
            return validBits;
        }

        // A wValidBitsPerSample of 0 means container-defined; non-extensible formats have no
        // separate valid-bits field at all.
        return waveFormat.BitsPerSample;
    }

    public static EndpointFormatDescriptor CreateDescriptor(WaveFormat waveFormat, EndpointFormatOrigin origin)
    {
        var validBits = GetValidBitsPerSample(waveFormat);
        var format = new AudioFormatCandidate(waveFormat.SampleRate, validBits, waveFormat.Channels);
        return new EndpointFormatDescriptor(format, waveFormat, ClassifyKind(waveFormat, validBits), origin);
    }

    private static EndpointFormatKind ClassifyKind(WaveFormat waveFormat, int validBits)
    {
        if (waveFormat is not WaveFormatExtensible extensible)
        {
            return EndpointFormatKind.WaveFormatEx;
        }

        if (extensible.SubFormat == KsDataFormatSubTypeIeeeFloat)
        {
            return EndpointFormatKind.WaveFormatExtensibleIeeeFloat;
        }

        return validBits < waveFormat.BitsPerSample
            ? EndpointFormatKind.WaveFormatExtensiblePadded
            : EndpointFormatKind.WaveFormatExtensible;
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

    public static EndpointFormatDescriptor? SelectContainerBitsFallback(
        IReadOnlyList<EndpointFormatDescriptor> descriptors,
        AudioFormatCandidate targetFormat) =>
        descriptors
            .Where(descriptor =>
                descriptor.NativeFormat.SampleRate == targetFormat.SampleRateHz &&
                descriptor.NativeFormat.Channels == targetFormat.Channels &&
                descriptor.NativeFormat.BitsPerSample == targetFormat.BitDepth)
            .OrderBy(descriptor => descriptor.Kind == EndpointFormatKind.WaveFormatExtensiblePadded ? 0 : 1)
            .ThenBy(GetPreference)
            .FirstOrDefault();

    private static WaveFormat? TryMarshalWaveFormat(byte[] blob)
    {
        var pointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pointer, blob.Length);
            return WaveFormat.MarshalFromPtr(pointer);
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

    // Pack = 2 matches the native mmreg.h layout (18 bytes); the default packing pads the
    // struct to 20, which overstates blob sizes and rejects valid plain-format blobs.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
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
